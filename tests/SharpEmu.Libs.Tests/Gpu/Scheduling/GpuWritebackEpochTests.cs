// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

// Work resolved ahead of the render thread from guest memory stays current only while no store of
// GPU data into guest memory overlapped it.
public sealed class GpuWritebackEpochTests
{
    [Fact]
    public void WorkBeforeAStoreIsStale()
    {
        var recorded = GpuWritebackEpoch.Value;
        using (GpuWritebackEpoch.BeginWrite(0x1000, 4))
        {
        }

        Assert.False(GpuWritebackEpoch.Unchanged(recorded));
    }

    [Fact]
    public void WorkStartedDuringAStoreIsStaleDuringAndAfterIt()
    {
        var scope = GpuWritebackEpoch.BeginWrite(0x1000, 4);
        var recorded = GpuWritebackEpoch.Value;
        Assert.False(GpuWritebackEpoch.Unchanged(recorded));
        scope.Dispose();
        Assert.False(GpuWritebackEpoch.Unchanged(recorded));
    }
}
