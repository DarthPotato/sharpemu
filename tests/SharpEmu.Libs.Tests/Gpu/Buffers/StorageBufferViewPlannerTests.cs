// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class StorageBufferViewPlannerTests
{
    private const ulong Alignment = 16;
    private const ulong MaxRange = 1UL << 30;
    private const ulong BufferSize = 0x10000;

    private static bool Plan(ulong offset, ulong size, bool dwordAddressed, out StorageBufferView view, out string error) =>
        StorageBufferViewPlanner.TryPlan(offset, size, Alignment, MaxRange, BufferSize, dwordAddressed, out view, out error);

    [Theory]
    [InlineData(0x1000UL, 0u)]
    [InlineData(0x1004UL, 4u)]
    [InlineData(0x1008UL, 8u)]
    public void DwordAlignedBasesBindForEveryAccessKind(ulong offset, uint adjustment)
    {
        foreach (var dwordAddressed in new[] { false, true })
        {
            Assert.True(Plan(offset, 0x100, dwordAddressed, out var view, out var error), error);
            Assert.Equal(0x1000UL, view.AlignedOffset);
            Assert.Equal(adjustment, view.MemoryOffset);
            Assert.Equal(0x100UL + adjustment, view.Range);
        }
    }

    [Theory]
    [InlineData(0x1001UL, 1u)]
    [InlineData(0x1002UL, 2u)]
    [InlineData(0x1006UL, 6u)]
    [InlineData(0x1003UL, 3u)]
    public void ByteAddressedBuffersBindAtAnyByteOffset(ulong offset, uint adjustment)
    {
        Assert.True(Plan(offset, 0x100, dwordAddressed: false, out var view, out var error), error);
        Assert.Equal(0x1000UL, view.AlignedOffset);
        Assert.Equal(adjustment, view.MemoryOffset);
        Assert.Equal(0UL, view.Range % 4);
        Assert.True(view.Range >= 0x100UL + adjustment);
    }

    [Theory]
    [InlineData(0x1001UL)]
    [InlineData(0x1002UL)]
    [InlineData(0x1006UL)]
    public void DwordAddressedBuffersRefuseAMidDwordBase(ulong offset)
    {
        Assert.False(Plan(offset, 0x100, dwordAddressed: true, out _, out var error));
        Assert.Contains("offset adjustment is unsupported", error);
        Assert.Contains("dword_addressed=True", error);
    }

    [Fact]
    public void AViewPastTheAdjustmentByteIsRefused()
    {
        // Alignment 0x1000 leaves an adjustment of 256, which no longer fits one byte.
        Assert.False(StorageBufferViewPlanner.TryPlan(0x1100, 0x100, 0x1000, MaxRange, BufferSize, false, out _, out var error));
        Assert.Contains("unsupported", error);
    }

    [Fact]
    public void ThePartialFinalWordIsPaddedWhenTheCacheBufferHasRoom()
    {
        // 0x1002 + 0x101 ends mid-dword: the range grows to whole words.
        Assert.True(Plan(0x1002, 0x101, dwordAddressed: false, out var view, out var error), error);
        Assert.Equal(0x104UL, view.Range);

        // No room before the end of the cache buffer: the exact range is kept.
        Assert.True(StorageBufferViewPlanner.TryPlan(0x1002, 0x101, Alignment, MaxRange, 0x1000 + 0x103, false, out view, out error), error);
        Assert.Equal(0x103UL, view.Range);
    }

    [Fact]
    public void DwordAddressedViewsKeepTheirExactRange()
    {
        Assert.True(Plan(0x1004, 0x101, dwordAddressed: true, out var view, out var error), error);
        Assert.Equal(0x105UL, view.Range);
    }

    [Fact]
    public void ARangeBeyondTheDeviceLimitIsRefused()
    {
        Assert.False(StorageBufferViewPlanner.TryPlan(0x1000, MaxRange + 1, Alignment, MaxRange, BufferSize, false, out _, out var error));
        Assert.Contains("unsupported", error);
    }
}
