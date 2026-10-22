// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu;

public sealed class GpuCheckpointTests
{
    [Fact]
    public void MarkerRoundTripsAllArgumentsAndStartsAtOne()
    {
        var ring = new CheckpointRing(4);
        var sequence = ring.Add((uint)RecordedOperation.DrawIndex, 123, 1, 2, 3, 4, 0xFEDCBA9876543210);
        Assert.Equal(1UL, sequence);
        Assert.Equal(sequence, ring.NewestSequence);
        Assert.Equal("seq=1 op=DrawIndex submit=123 args=1,2,3,4,0xFEDCBA9876543210", ring.Describe(sequence));
        Assert.Null(ring.Decode(0));
    }

    [Fact]
    public void DrawShadersAppearOnlyWhenRecorded()
    {
        var ring = new CheckpointRing(4);
        var draw = ring.Add((uint)RecordedOperation.DrawIndex, 7, 256, 24576, 0, 1, 0, 0xAD291CED9B8A8CE8, 0x22025DD20C4A8141);
        var dispatch = ring.Add((uint)RecordedOperation.DispatchDirect, 7, 1, 1, 1, 65, 0x801E18200);
        Assert.EndsWith("draw_shaders=0xAD291CED9B8A8CE8,0x22025DD20C4A8141", ring.Describe(draw));
        Assert.DoesNotContain("draw_shaders", ring.Describe(dispatch));
    }

    [Fact]
    public void WraparoundReportsEvictedMarkers()
    {
        var ring = new CheckpointRing(2);
        var first = ring.Add(0, 10, 0, 0, 0, 0, 0);
        var second = ring.Add(1, 20, 0, 0, 0, 0, 0);
        var third = ring.Add(2, 30, 0, 0, 0, 0, 0);
        Assert.Null(ring.Decode(first));
        Assert.Equal("seq=1 evicted", ring.Describe(first));
        Assert.Equal(20UL, ring.Decode(second)!.SubmitId);
        Assert.Equal(30UL, ring.Decode(third)!.SubmitId);
    }

    [Fact]
    public void ConcurrentWritersPreserveTheNewestGenerationOfEverySlot()
    {
        var ring = new CheckpointRing(64);
        Parallel.For(0, 10000, value => ring.Add(1, (ulong)value, (uint)value, 0, 0, 0, (ulong)value));
        Assert.Equal(10000UL, ring.NewestSequence);
        for (var sequence = ring.NewestSequence - 63; sequence <= ring.NewestSequence; sequence++)
        {
            var entry = ring.Decode(sequence);
            Assert.NotNull(entry);
            Assert.Equal(entry.SubmitId, entry.Arg4);
            Assert.Equal(entry.SubmitId, (ulong)entry.Arg0);
        }
    }
}
