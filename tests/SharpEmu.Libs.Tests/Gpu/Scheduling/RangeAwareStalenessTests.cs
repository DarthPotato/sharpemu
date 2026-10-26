// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

// A resolution done ahead of the render thread stays usable across stores of GPU data into guest
// memory that touched none of the ranges it read.
public sealed class RangeAwareStalenessTests
{
    // Far from anything other tests store to.
    private const ulong Base = 0x7F00_0000_0000;

    [Fact]
    public void RecorderKeepsOnlyReadsMadeWhileOpen()
    {
        var recorder = new ConcurrentReadRecorder();
        ConcurrentReadRecorder.Note(Base, 4);
        ConcurrentReadRecorder.Open(recorder);
        try
        {
            ConcurrentReadRecorder.Note(Base + 0x100, 4);
            ConcurrentReadRecorder.Note(Base + 0x104, 8);
        }
        finally
        {
            ConcurrentReadRecorder.Close();
        }

        ConcurrentReadRecorder.Note(Base + 0x200, 4);
        Assert.Equal([(Base + 0x100, 12UL)], recorder.Ranges);
        Assert.False(recorder.Overlaps(Base, 4));
        Assert.True(recorder.Overlaps(Base + 0x108, 1));
        Assert.False(recorder.Overlaps(Base + 0x200, 4));
    }

    [Fact]
    public void RecorderThatOverflowsCountsAsHavingReadEverything()
    {
        var recorder = new ConcurrentReadRecorder();
        ConcurrentReadRecorder.Open(recorder);
        try
        {
            for (ulong index = 0; index < 1000; index++)
            {
                ConcurrentReadRecorder.Note(Base + index * 0x1000, 4);
            }
        }
        finally
        {
            ConcurrentReadRecorder.Close();
        }

        Assert.True(recorder.Overflowed);
        Assert.True(recorder.Overlaps(0x10, 1));
    }

    [Fact]
    public void StoresElsewhereAreDisjointAndOverlappingStoresAreNot()
    {
        if (!GpuWritebackEpoch.RecordsHistory)
        {
            return;
        }

        var floor = GpuWritebackEpoch.HistoryFloor();
        using (GpuWritebackEpoch.BeginWrite(Base + 0x10_0000, 0x100))
        {
        }

        Assert.Equal(GpuWritebackEpoch.StaleCause.Disjoint,
            GpuWritebackEpoch.ClassifyStores(floor, (address, size) => address < Base + 0x1000 && Base < address + size));
        Assert.Equal(GpuWritebackEpoch.StaleCause.Overlapping,
            GpuWritebackEpoch.ClassifyStores(floor, (address, size) => address < Base + 0x10_0010 && Base + 0x10_0000 < address + size));
    }
}
