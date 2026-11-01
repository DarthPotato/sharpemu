// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class RuntimeResolveJoinTests
{
    [Fact]
    public void ChangedRecordedBytesRejectRuntimeResultAtJoin()
    {
        using var harness = new DeferredSignalHarness();
        var cache = DeferredSignalHarness.EmptyBufferCache();
        typeof(SharpEmu.Libs.Gpu.Buffers.GuestBufferCache)
            .GetField("_gpuModifiedSnapshot", PresenterUnderTest.InstanceMembers)!
            .SetValue(cache, SharpEmu.HLE.GpuMemory.SpanSnapshot.Empty);
        harness.Set("_bufferCache", cache);
        var host = (ICommandStreamHost)harness.Presenter;
        var deferred = new AgcExports.DeferredDraw { RuntimeResolved = true };
        deferred.Reads.Add(StreamRunner.DataAddress, 0u);
        Assert.True(deferred.ValidateReads(0, host, atJoin: true));
        harness.Runner.Host.WriteWords(StreamRunner.DataAddress, [1u]);
        // A publication ordinal change forces the existing byte validation path;
        // this unrelated pending range does not itself reject the logged read.
        harness.Pending.AddWrite(0, StreamRunner.DataAddress + 4096, 4);
        Assert.False(deferred.ValidateReads(0, host, atJoin: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeResultIsRejectedAtJoinWhenFrontendOrPublicationChanges(bool frontend)
    {
        using var harness = new DeferredSignalHarness();
        var host = (ICommandStreamHost)harness.Presenter;
        var deferred = new AgcExports.DeferredDraw { RuntimeResolved = true, FrontendImageVersion = 7 };
        deferred.Reads.Add(StreamRunner.DataAddress, 0u);
        AgcExports.TakeResolvePoolReport();
        Assert.True(deferred.ValidateReads(7, host, atJoin: true));
        if (!frontend) harness.Pending.AddWrite(0, StreamRunner.DataAddress, 4);
        var version = frontend ? 8 : 7;
        // The recording worker may discover it first; only ordered join counts a rejection.
        Assert.False(deferred.ValidateReads(version, host, atJoin: false));
        Assert.Contains("runtime_rejected=0 ", AgcExports.TakeResolvePoolReport());
        Assert.False(deferred.ValidateReads(version, host, atJoin: true));
        Assert.False(deferred.ValidateReads(version, host, atJoin: true));
        var report = AgcExports.TakeResolvePoolReport();
        Assert.Contains("runtime_rejected=1 ", report);
        Assert.Contains(frontend ? "runtime_reject=frontend:1,reads:0" : "runtime_reject=frontend:0,reads:1", report);
        deferred.Reset();
        Assert.False(deferred.RuntimeResolved);
    }
}
