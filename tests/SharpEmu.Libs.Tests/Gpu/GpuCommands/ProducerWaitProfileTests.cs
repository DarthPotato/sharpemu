// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class ProducerWaitProfileTests
{
    [Fact]
    public void NestedPurposesRestoreAndReportsDrainCounts()
    {
        if (!CommandThreadTally.Enabled) return; // Also run explicitly with SHARPEMU_PROFILE_RENDER=1.
        var millisecond = Stopwatch.Frequency / 1000;
        using (ProducerWaitProfile.Begin("test_outer"))
        {
            ProducerWaitProfile.Note("pending", millisecond);
            using (ProducerWaitProfile.Begin("test_inner"))
                ProducerWaitProfile.Note("run", 3 * millisecond, "reader");
            ProducerWaitProfile.Note("pending", 2 * millisecond);
        }
        var report = ProducerWaitProfile.TakeReport();
        Assert.Contains("test_outer/pending/=2/3.00ms/max2.00ms", report);
        Assert.Contains("test_inner/run/reader=1/3.00ms/max3.00ms", report);
        Assert.DoesNotContain("test_outer", ProducerWaitProfile.TakeReport());
    }
}
