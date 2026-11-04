// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private static int _nextLifecycleCacheId;
    private readonly int _lifecycleCacheId = ImageLifecycleTrace.Enabled ? Interlocked.Increment(ref _nextLifecycleCacheId) : 0;

    internal void TraceLifecycle(CachedImage image, string operation, string detail)
    {
        if (!ImageLifecycleTrace.Enabled || !image.LifecycleIdentifier.IsValid || !ImageLifecycleTrace.HasCapacity) return;
        var id = image.LifecycleIdentifier;
        var description = image.Description;
        ImageLifecycleTrace.Write($"event={operation} cache={_lifecycleCacheId} id={id.Index} generation={id.Generation} " +
            $"frame={Volatile.Read(ref _collectionTick)} tick={_scheduler.CurrentTick} " +
            $"address=0x{description.Data.Address:X} size=0x{description.Data.Size:X} " +
            $"format={description.PixelFormat} guest_format={description.GuestFormat} tile={description.TileMode} " +
            $"extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} " +
            $"mips={description.Resources.Levels} layers={description.Resources.Layers} " +
            $"cpu_dirty={image.IsDefinitelyCpuDirty} maybe_cpu_dirty={image.IsMaybeCpuDirty} buffer_dirty={image.IsBufferModified} " +
            $"gpu_modified={image.IsGpuModified} buffer_holds_gpu={image.BufferHoldsGpuContents} " +
            $"registered={image.Registered} parked={image.Parked} last_access_tick={image.LastAccessTick} {detail}");
    }

    private void TraceLifecycleRetirement(CachedImage image, string operation, string reason)
    {
        if (!ImageLifecycleTrace.Enabled || image.LifecycleCache == null || !ImageLifecycleTrace.HasCapacity) return;
        if (image.Registered) image.LifecycleLastUseFrame = _recencyQueue.GetLastUseTick(image.RecencyEntryIndex);
        TraceLifecycle(image, operation, $"reason={reason} last_use_frame={image.LifecycleLastUseFrame} " +
            $"live_bytes={_totalUsedMemory} pending_destroy_bytes={Interlocked.Read(ref _pendingDestroyBytes)} " +
            $"thresholds={_collectionStartBytes}/{_memoryPressureBytes}/{_criticalMemoryBytes}/{_hardLimitBytes} " +
            $"allocation_collection_blocked={_allocationCollectionBlocked}");
    }

    private void TraceLifecycleReplacement(CachedImage old, ResourceSlotIdentifier previous, ResourceSlotIdentifier replacement, string reason)
    {
        if (!ImageLifecycleTrace.Enabled || previous == replacement || !ImageLifecycleTrace.HasCapacity) return;
        var next = _slots.TryGet(replacement);
        if (next == null || (old.LifecycleCache == null && next.LifecycleCache == null) ||
            !old.Overlaps(next.Description.Data.Address, next.Description.Data.Size)) return;
        // A replacement can contain the selected byte while the old image covers another part.
        var detail = $"reason={reason} previous_id={previous.Index} previous_generation={previous.Generation} " +
            $"previous_address=0x{old.Description.Data.Address:X} replacement_id={replacement.Index} " +
            $"replacement_generation={replacement.Generation} replacement_address=0x{next.Description.Data.Address:X} " +
            $"previous_registered={old.Registered} previous_parked={old.Parked}";
        if (old.LifecycleCache != null) TraceLifecycle(old, "replacement", detail);
        else if (next.LifecycleCache != null) TraceLifecycle(next, "replacement", detail);
    }

    private void TraceLifecycleMetadataChange(ulong address, string reason)
    {
        if (!ImageLifecycleTrace.HasCapacity) return;
        _slots.ForEach((_, image) =>
        {
            if (image.LifecycleCache != null && image.Description.Metadata.Range.Address == address)
                TraceLifecycle(image, "metadata-change", $"reason={reason} metadata_address=0x{address:X}");
        });
    }
}
