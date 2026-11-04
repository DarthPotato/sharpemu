// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private long _nextMemoryBudgetSample;

    private static readonly bool BudgetCountsPool = Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_BUDGET_COUNT_POOL") != "0";

    // Smaller heaps cannot afford hundreds of MiB stranded in every partially used block.
    internal static ulong ComputeImagePoolBlockSize(ulong heapBytes)
    {
        var target = Math.Clamp(heapBytes / 128, 16UL << 20, 256UL << 20);
        return 1UL << System.Numerics.BitOperations.Log2(target);
    }

    // Driver usage includes guest buffers, scratch, pools and other applications. Add back
    // the live image working set so its own allocations do not repeatedly shrink its budget.
    // Sampling is bounded even when a loading screen submits without presenting frames.
    private void RefreshMemoryBudgetAndProfile()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextMemoryBudgetSample) return;
        _nextMemoryBudgetSample = now + 2 * Stopwatch.Frequency;
        var memory = _device.ReadDeviceLocalMemory();
        if (_device.HasMemoryBudget && GpuMemoryPolicyOptions.DynamicImageBudget)
        {
            // Free space in the image pool's blocks and pooled backings are device memory only images reuse: the
            // driver counts them as used, and leaving them out cost Ghost of Yotei ~1.1 GB of image budget, so its
            // working set fell back past the critical threshold as other usage grew (SHARPEMU_IMAGE_BUDGET_COUNT_POOL=0
            // leaves them out).
            var reusable = BudgetCountsPool ? _imageMemoryPool.UnusedBytes + (_backingPool?.PooledBytes ?? 0) : 0;
            var availableForImages = memory.Available + _totalUsedMemory + reusable;
            var budget = Math.Min(_initialImageBudget, ComputeImageCacheBudget(availableForImages, true));
            SetCollectionThresholds(budget);
        }
        if (SharpEmu.Libs.Gpu.Buffers.GpuDeviceInfo.VramTrace)
            Console.Error.WriteLine($"[PERF][VRAM_SITES] {_device.DescribeTracedMemory()}");
        if (!RenderPhaseProfile.Enabled) return;
        ulong parked = 0;
        foreach (var id in _parked)
            if (_slots.TryGet(id) is { Registered: true, Parked: true } image) parked += image.AccountedSize;
        Console.Error.WriteLine($"[PERF][GPU_MEMORY] live_image_bytes={_totalUsedMemory} parked_image_bytes={parked} " +
            $"pooled_backing_bytes={_backingPool?.PooledBytes ?? 0} image_pool_allocated_bytes={_imageMemoryPool.AllocatedBytes} " +
            $"image_pool_unused_bytes={_imageMemoryPool.UnusedBytes} large_in_blocks={SharpEmu.Libs.Gpu.Buffers.OptimalImageMemoryPool.LargePlacementsInBlocks} pending_destroy_bytes={Interlocked.Read(ref _pendingDestroyBytes)} " +
            $"rebar_buffer_bytes={_bufferCache.PromotedBytes} registered_buffer_bytes={_bufferCache.TotalUsedMemory} scratch_pooled_bytes={_tiler.ScratchPooledBytes} heap_budget_bytes={memory.Budget} heap_usage_bytes={memory.Usage} " +
            $"heap_available_bytes={memory.Available} image_budget_bytes={_hardLimitBytes}");
    }
}
