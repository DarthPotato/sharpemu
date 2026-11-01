// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;

// Only an already-associated stencil plane is eligible. Creating or transferring a
// plane can record copies; that must retain the serial path at the original caller.
public readonly record struct GpuWrittenSliceMark(ResourceSlotIdentifier Image, CachedImage Expected,
    ResourceSlotIdentifier Stencil, CachedImage ExpectedStencil, GuestSpan Data, GuestSpan StencilRange,
    ulong LookupGeneration);

public sealed unsafe partial class GuestImageCache
{
    public bool TryCaptureGpuWrittenMark(ResourceSlotIdentifier identifier, out GpuWrittenSliceMark mark)
    {
        using var held = _lock.Read();
        mark = default;
        if (_slots.TryGet(identifier) is not { Registered: true, Parked: false } image ||
            image.DepthOwner.IsValid || !image.Backing.Exists || !image.Description.HasStencil ||
            image.Description.Samples != 1)
            return false;

        var stencil = ResourceSlotIdentifier.Invalid;
        foreach (var candidate in CollectImagesInRange(image.Description.Stencil.Address,
                     image.Description.Stencil.Size, includeParked: true))
        {
            var proxy = _slots.TryGet(candidate);
            if (proxy is null || proxy.Description.Data != image.Description.Stencil) continue;
            // AssociateStencilRange must select this same existing proxy, with no transfer.
            if (stencil.IsValid || proxy.DepthOwner != identifier || proxy.Parked || !proxy.Registered ||
                !proxy.Description.Extent.Equals(image.Description.Extent))
                return false;
            stencil = candidate;
        }
        if (!stencil.IsValid) return false;
        mark = new(identifier, image, stencil, _slots[stencil], image.Description.Data,
            image.Description.Stencil, _lookupGeneration);
        return true;
    }

    public bool ValidateGpuWrittenMarks(List<GpuWrittenSliceMark> marks)
    {
        if (marks.Count == 0) return true;
        using var held = _lock.Read();
        foreach (var mark in marks)
        {
            if (!ReferenceEquals(_slots.TryGet(mark.Image), mark.Expected) ||
                !ReferenceEquals(_slots.TryGet(mark.Stencil), mark.ExpectedStencil) ||
                !mark.Expected.Registered || mark.Expected.Parked || mark.Expected.DepthOwner.IsValid ||
                !mark.Expected.Backing.Exists || mark.Expected.Description.Data != mark.Data ||
                mark.Expected.Description.Stencil != mark.StencilRange ||
                !mark.ExpectedStencil.Registered || mark.ExpectedStencil.Parked ||
                mark.ExpectedStencil.DepthOwner != mark.Image || mark.ExpectedStencil.Description.Data != mark.StencilRange ||
                !mark.ExpectedStencil.Description.Extent.Equals(mark.Expected.Description.Extent) ||
                !LookupUnchangedSince(mark.LookupGeneration, mark.Data.Address, mark.Data.End) ||
                !LookupUnchangedSince(mark.LookupGeneration, mark.StencilRange.Address, mark.StencilRange.End))
                return false;
        }
        return true;
    }
}
