// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class WriteTickMapTests
{
    [Fact]
    public void ARangeReportsOnlyTheWritesThatReachIt()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x100, 5);
        map.Assign(0x800, 0x100, 9);

        Assert.Equal(5UL, map.MaxTick(0x000, 0x100));
        Assert.Equal(9UL, map.MaxTick(0x880, 0x10));
        Assert.Equal(0UL, map.MaxTick(0x200, 0x400));
        Assert.Equal(9UL, map.MaxTick(0x000, 0x1000));
    }

    [Fact]
    public void ANewerWriteSplitsTheOlderRange()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x300, 3);
        map.Assign(0x100, 0x100, 7);

        Assert.Equal(3, map.Count);
        Assert.Equal(3UL, map.MaxTick(0x000, 0x100));
        Assert.Equal(7UL, map.MaxTick(0x180, 0x8));
        Assert.Equal(3UL, map.MaxTick(0x200, 0x100));
    }

    [Fact]
    public void AdjacentWritesOfOneTickMerge()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x100, 4);
        map.Assign(0x200, 0x100, 4);
        map.Assign(0x100, 0x100, 4);

        Assert.Equal(1, map.Count);
        Assert.Equal(4UL, map.MaxTick(0x2FF, 1));
    }

    [Fact]
    public void PrunedWritesReadAsTheFloor()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x100, 2);
        map.Assign(0x100, 0x100, 6);
        map.Prune(5);

        Assert.Equal(1, map.Count);
        Assert.Equal(2UL, map.Floor);
        Assert.Equal(2UL, map.MaxTick(0x400, 0x100));
        Assert.Equal(6UL, map.MaxTick(0x100, 0x100));
    }

    // Random overlapping writes against a per-byte model: every range query must see the latest
    // tick of every byte it covers, whatever splits and merges the assignments caused.
    [Fact]
    public void RandomWritesMatchAPerByteModel()
    {
        const int Bytes = 512;
        var random = new System.Random(5);
        for (var trial = 0; trial < 50; trial++)
        {
            var map = new WriteTickMap();
            var model = new ulong[Bytes];
            for (var step = 1; step <= 200; step++)
            {
                var begin = random.Next(Bytes);
                var size = 1 + random.Next(Math.Min(64, Bytes - begin));
                // Ticks repeat sometimes, so neighbours with equal ticks merge.
                var tick = (ulong)(step / (1 + random.Next(3)));
                map.Assign((ulong)begin, (ulong)size, tick);
                Array.Fill(model, tick, begin, size);

                var queryBegin = random.Next(Bytes);
                var querySize = 1 + random.Next(Bytes - queryBegin);
                var expected = 0UL;
                for (var at = queryBegin; at < queryBegin + querySize; at++)
                {
                    expected = Math.Max(expected, model[at]);
                }

                Assert.Equal(expected, map.MaxTick((ulong)queryBegin, (ulong)querySize));
            }

            Assert.True(map.Count <= Bytes);
        }
    }
}
