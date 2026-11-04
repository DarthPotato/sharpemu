// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class CachedImage
{
    internal GuestImageCache? LifecycleCache;
    internal ResourceSlotIdentifier LifecycleIdentifier;
    internal ulong LifecycleLastUseFrame;
    internal string? LifecycleRetirementReason;
    internal string? LifecycleUploadReason;
    internal string? LifecycleUploadPath;

    private void TraceLifecycleState(string operation, string reason)
    {
        if (!ImageLifecycleTrace.HasCapacity) return;
        LifecycleCache?.TraceLifecycle(this, operation, $"reason={reason}");
    }

    // Payload bytes, excluding row pitch and scratch padding; span size is logged separately.
    private ulong LifecycleUploadBytes(ReadOnlySpan<BufferImageCopy> copies)
    {
        var block = Description.IsBlock ? 4UL : 1UL;
        ulong total = 0;
        foreach (var copy in copies)
        {
            var bytes = copy.ImageSubresource.AspectMask == ImageAspectFlags.StencilBit ? 1u :
                copy.ImageSubresource.AspectMask == ImageAspectFlags.DepthBit ? DepthFormatRule.AspectTransferBytes(Backing.Format) :
                ViewFormatRules.BlockBytes(Backing.Format);
            if (bytes == 0) bytes = Description.BytesPerBlock;
            total += ((copy.ImageExtent.Width + block - 1) / block) * ((copy.ImageExtent.Height + block - 1) / block) *
                copy.ImageExtent.Depth * copy.ImageSubresource.LayerCount * bytes;
        }
        return total;
    }

    // Keeps the logical upload cause on each actual transfer, including changed-piece runs.
    internal readonly struct LifecycleUploadScope : IDisposable
    {
        private readonly CachedImage? _image;
        private readonly string? _previousReason;
        private readonly string? _previousPath;

        internal LifecycleUploadScope(CachedImage image, string reason, string path)
        {
            _image = image;
            _previousReason = image.LifecycleUploadReason;
            _previousPath = image.LifecycleUploadPath;
            image.LifecycleUploadReason = reason;
            image.LifecycleUploadPath = path;
        }

        public void Dispose()
        {
            if (_image == null) return;
            _image.LifecycleUploadReason = _previousReason;
            _image.LifecycleUploadPath = _previousPath;
        }
    }
}
