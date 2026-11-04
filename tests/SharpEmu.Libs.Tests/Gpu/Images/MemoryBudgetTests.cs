// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class MemoryBudgetTests
{
    private const ulong MiB = 1UL << 20;
    private const ulong GiB = 1UL << 30;

    [Fact]
    public void AvailableMemoryUsesBudgetMinusCurrentUsage()
    {
        Assert.Equal(8 * GiB, GpuDeviceInfo.CalculateAvailableBytes(12 * GiB, 4 * GiB));
        Assert.Equal(0UL, GpuDeviceInfo.CalculateAvailableBytes(4 * GiB, 5 * GiB));
        Assert.Equal(0UL, GpuDeviceInfo.CalculateAvailableBytes(4 * GiB, 4 * GiB));
    }

    [Fact]
    public void ImageCacheLeavesAnEighthOfWhatIsFree()
    {
        Assert.Equal(7 * GiB, GuestImageCache.ComputeImageCacheBudget(8 * GiB, hasMemoryBudget: true));
        Assert.Equal(21 * GiB / 2, GuestImageCache.ComputeImageCacheBudget(12 * GiB, hasMemoryBudget: true));
    }

    [Fact]
    public void CollectionThresholdsParseOrFallBack()
    {
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds(null));
        Assert.Equal((50UL, 60UL, 70UL), GuestImageCache.ParseThresholds("50,60,70"));
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds("70,60,80"));
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds("50,60,101"));
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds("50,60"));
    }

    [Fact]
    public void ImageCacheMarginHasAFloor()
    {
        // An eighth of 1 GiB is below the 512 MiB floor.
        Assert.Equal(512 * MiB, GuestImageCache.ComputeImageCacheBudget(GiB, hasMemoryBudget: true));
        // Less free memory than the floor keeps half of it, never less than 1 MiB.
        Assert.Equal(200 * MiB, GuestImageCache.ComputeImageCacheBudget(400 * MiB, hasMemoryBudget: true));
        Assert.Equal(MiB, GuestImageCache.ComputeImageCacheBudget(0, hasMemoryBudget: true));
    }

    [Fact]
    public void WithoutAMemoryBudgetTheHeapSizeIsCappedConservatively()
    {
        Assert.Equal(4 * GiB, GuestImageCache.ComputeImageCacheBudget(12 * GiB, hasMemoryBudget: false));
        Assert.Equal(7 * GiB / 2, GuestImageCache.ComputeImageCacheBudget(4 * GiB, hasMemoryBudget: false));
    }
}
