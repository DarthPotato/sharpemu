// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // Only the ordered render thread appends/checks history. Workers acquire the
        // published sequence at BeginSegment; they never enumerate this ring.
        private OwnershipChange[]? _ownershipChanges;
        private const int OwnershipHistoryLimit = 65536;
        private readonly record struct OwnershipChange(long Sequence, ResourceSlotIdentifier Image, GuestSpan Data,
            ResourceSlotIdentifier Stencil, GuestSpan StencilRange);

        internal void MarkGpuWrittenOrdered(ResourceSlotIdentifier identifier)
        {
            _imageCache.MarkGpuWritten(identifier);
            if (!DeferredGpuWritten) return;
            var image = _imageCache.GetImage(identifier);
            var sequence = GpuWrittenEpoch + 1;
            var proxy = image.Description.Stencil.Size != 0 && _imageCache.TryCaptureGpuWrittenMark(identifier, out var mark)
                ? mark.Stencil : ResourceSlotIdentifier.Invalid;
            _ownershipChanges ??= new OwnershipChange[OwnershipHistoryLimit];
            _ownershipChanges[sequence % OwnershipHistoryLimit] = new(sequence, identifier, image.Description.Data,
                proxy, image.Description.Stencil);
            Volatile.Write(ref GpuWrittenEpoch, sequence);
        }

        private bool ValidateOwnership(SliceRequests requests)
        {
            if (!DeferredGpuWritten || requests.GpuWrittenEpoch == GpuWrittenEpoch) return true;
            // Unknown shader pointer accesses and history truncation cannot prove
            // independence. Never silently accept an incomplete access footprint.
            if (GpuWrittenEpoch - requests.GpuWrittenEpoch > OwnershipHistoryLimit)
                return Conflict(fallback: true);
            for (var sequence = requests.GpuWrittenEpoch + 1; sequence <= GpuWrittenEpoch; sequence++)
            {
                var change = _ownershipChanges![sequence % OwnershipHistoryLimit];
                if (change.Sequence != sequence) return Conflict(true);
                foreach (var use in requests.Images)
                    if (SameImage(use.Image) || Touches(use.Request.Description.Data) ||
                        Touches(use.Request.Description.Stencil) || Touches(use.Request.Description.Metadata.Range)) return Conflict(false);
                foreach (var use in requests.OwnershipImages)
                    if (SameImage(use.Image) || Touches(use.Data) || Touches(use.Stencil) || Touches(use.Metadata)) return Conflict(false);
                foreach (var use in requests.Buffers)
                    if (Touches(new(use.Address, use.Size))) return Conflict(false);
                foreach (var range in requests.OwnershipReads)
                    if (Touches(range)) return Conflict(false);
                foreach (var range in requests.SyncRanges)
                    if (Touches(new(range.Address, range.Size))) return Conflict(false);
                foreach (var mark in requests.GpuWritten)
                    if (SameImage(mark.Image) || SameImage(mark.Stencil) ||
                        Touches(mark.Data) || Touches(mark.StencilRange)) return Conflict(false);

                bool SameImage(ResourceSlotIdentifier image) => image.IsValid &&
                    (image == change.Image || image == change.Stencil);
                bool Touches(GuestSpan range) => Overlaps(range, change.Data) || Overlaps(range, change.StencilRange);
            }
            return requests.UnrecordedOwnershipAccess ? Conflict(true) : true;

            static bool Conflict(bool fallback)
            {
                _gpuWrittenConflicts.Increment();
                if (fallback) _gpuWrittenUnrecorded.Increment();
                else _gpuWrittenOverlap.Increment();
                return false;
            }
        }
    }

    // Subtraction avoids overflow at the end of a guest range.
    private static bool Overlaps(GuestSpan a, GuestSpan b) => a.Size != 0 && b.Size != 0 &&
        (a.Address <= b.Address ? b.Address - a.Address < a.Size : a.Address - b.Address < b.Size);
}
