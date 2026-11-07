// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;
using static SharpEmu.Libs.VideoOut.RenderPhaseProfile;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class DescriptorPreparationProfileTests
{
    [Fact]
    public void RealScopesReconcileClassifyCompletedCallsAndUnwindAfterException()
    {
        if (!MovableEnabled) return;
        var counters = (DescriptorAccounting)typeof(RenderPhaseProfile).GetNestedType("DescriptorStorage", BindingFlags.NonPublic)!
            .GetField("Counters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var phaseTicks = (long[])typeof(RenderPhaseProfile).GetField("_ticks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        counters.TakeReport(0, 0, 0, 0, 0);
        var before = phaseTicks[(int)Phase.DescriptorPreparation];
        using (Measure(Phase.CommandStream))
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var operation = MeasureDescriptor();
                using var phase = MeasureDetail(Phase.DescriptorPreparation);
                using var step = MeasureDescriptorStep(DescriptorStep.ImageResolution);
                using (MeasureDetail(Phase.ImageAcquire)) Thread.SpinWait(1000);
                DescriptorMemo(DescriptorMemoKind.Reference);
                throw new InvalidOperationException();
            }));
            using (MeasureDescriptor(DescriptorMemoKind.Content, bind: true))
            using (MeasureDetail(Phase.DescriptorPreparation))
            using (MeasureDescriptorStep(DescriptorStep.BufferDiscovery)) Thread.SpinWait(1000);
        }
        Assert.Equal(1, counters.PrepareCalls[(int)DescriptorMemoKind.Reference]);
        Assert.Equal(1, counters.BindCalls[(int)DescriptorMemoKind.Content]);
        Assert.Equal(phaseTicks[(int)Phase.DescriptorPreparation] - before, counters.SelfTicks.Sum());
        Assert.True(counters.InclusiveTicks.Sum() > counters.SelfTicks.Sum());
        var worker = new Thread(() =>
        {
            AttachResolveWorker();
            using var operation = MeasureDescriptor();
            using var phase = Measure(Phase.DescriptorPreparation);
            using var step = MeasureDescriptorStep(DescriptorStep.ShaderData);
            DescriptorMemo(DescriptorMemoKind.Content);
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, counters.PrepareCalls.Sum());
        Assert.Equal(1, counters.BindCalls.Sum());
        counters.TakeReport(0, 0, 0, 0, 0);
    }

    [Fact]
    public void ExclusiveTicksReconcileAndInclusiveChildrenAreNotDoubleCounted()
    {
        var counters = new DescriptorAccounting();
        counters.Charge(DescriptorStep.Rest, Phase.DescriptorPreparation, 11);
        counters.Charge(DescriptorStep.ImageResolution, Phase.DescriptorPreparation, 13);
        counters.Charge(DescriptorStep.PublicationMetadata, Phase.OrderedMetadata, 17);
        counters.Charge(DescriptorStep.ImageResolution, Phase.ImageAcquire, 19);
        counters.Finish(DescriptorMemoKind.Reference, bind: false);
        counters.Charge(DescriptorStep.BufferDiscovery, Phase.DescriptorPreparation, 23);
        counters.Finish(DescriptorMemoKind.Content, bind: true);
        counters.Charge(DescriptorStep.Samplers, Phase.DescriptorPreparation, 29);
        counters.Finish(DescriptorMemoKind.None, bind: false, DescriptorMissReason.LookupHistoryOverflow);
        Assert.Equal(76, counters.SelfTicks.Sum());
        Assert.Equal(112, counters.InclusiveTicks.Sum());
        Assert.Equal(1, counters.PrepareCalls[(int)DescriptorMemoKind.Reference]);
        Assert.Equal(1, counters.BindCalls[(int)DescriptorMemoKind.Content]);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var report = counters.TakeReport(5.1, 31, 76, 101, 3);
            Assert.StartsWith("[PERF][DESC_PREP] window_s=5.1 frames=31", report);
            Assert.Contains("reconcile_ms=0.000", report);
            Assert.Contains("reference_prepare_n=1", report);
            Assert.Contains("content_bind_n=1", report);
            Assert.Contains("materialization_n=3", report);
            Assert.Contains("miss_LookupHistoryOverflow_n=1", report);
            Assert.DoesNotContain(',', report);
            Assert.Equal(0, counters.SelfTicks.Sum());
            Assert.Equal(0, counters.InclusiveTicks.Sum());
            Assert.Equal(0, counters.PrepareCalls.Sum());
            Assert.Equal(0, counters.BindCalls.Sum());
            Assert.Equal(0, counters.MissPrepareCalls.Sum());
            Assert.Equal(0, counters.MissPrepareTicks.Sum());
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void DisabledDescriptorScopesDoNotAllocateOnFreshThread()
    {
        if (MovableEnabled) return;
        long allocated = -1;
        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 1000; index++)
            {
                using var operation = MeasureDescriptor();
                using var step = MeasureDescriptorStep(DescriptorStep.PublicationMetadata);
                DescriptorMemo(DescriptorMemoKind.Reference);
                DescriptorMiss(DescriptorMissReason.ImageWordsChanged);
                using var worker = MeasureDescriptor(worker: true);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void MissReasonsPartitionPreparationAndBindingTimeAndReset()
    {
        var counters = new DescriptorAccounting();
        foreach (var reason in Enum.GetValues<DescriptorMissReason>().Where(value => value != DescriptorMissReason.Count))
        {
            counters.Charge(DescriptorStep.MemoValidation, Phase.DescriptorPreparation, 11);
            counters.Charge(DescriptorStep.ImageResolution, Phase.ImageAcquire, 13);
            counters.Finish(DescriptorMemoKind.None, false, reason);
            counters.Charge(DescriptorStep.BufferDiscovery, Phase.DescriptorPreparation, 17);
            counters.Finish(DescriptorMemoKind.None, true, reason);
        }
        Assert.Equal(counters.PrepareCalls[(int)DescriptorMemoKind.None], counters.MissPrepareCalls.Sum());
        Assert.Equal(counters.BindCalls[(int)DescriptorMemoKind.None], counters.MissBindCalls.Sum());
        Assert.Equal(counters.PrepareTicks[(int)DescriptorMemoKind.None], counters.MissPrepareTicks.Sum());
        Assert.Equal(counters.BindTicks[(int)DescriptorMemoKind.None], counters.MissBindTicks.Sum());
        Assert.All(counters.MissPrepareCalls, calls => Assert.Equal(1, calls));
        Assert.All(counters.MissPrepareTicks, ticks => Assert.Equal(11, ticks));
        Assert.All(counters.MissBindTicks, ticks => Assert.Equal(17, ticks));
        Assert.All(counters.MissInclusiveTicks, ticks => Assert.Equal(41, ticks));
        counters.TakeReport(1, 1, counters.SelfTicks.Sum(), 0, 0);
        Assert.DoesNotContain("miss_", counters.TakeReport(1, 1, 0, 0, 0));
    }

    [Fact]
    public void WorkerMissWallTimeIsSeparateAndExceptionRestoresTheScope()
    {
        if (!MovableEnabled) return;
        var counters = (DescriptorAccounting)typeof(RenderPhaseProfile).GetNestedType("DescriptorStorage", BindingFlags.NonPublic)!
            .GetField("Counters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        counters.TakeReport(0, 0, 0, 0, 0);
        var worker = new Thread(() =>
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var scope = MeasureDescriptor(worker: true);
                DescriptorMiss(DescriptorMissReason.WorkerOrderedPreparation);
                Thread.SpinWait(1000);
                throw new InvalidOperationException();
            }));
            using var next = MeasureDescriptor(DescriptorMemoKind.Content, worker: true);
            Thread.SpinWait(1000);
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, counters.SelfTicks.Sum());
        Assert.Equal(0, counters.PrepareCalls.Sum());
        Assert.Equal(1, counters.WorkerMissCalls[(int)DescriptorMissReason.WorkerOrderedPreparation]);
        Assert.Equal(1, counters.WorkerCalls[(int)DescriptorMemoKind.Content]);
        Assert.True(counters.WorkerMissTicks.Sum() > 0);
        Assert.Contains("worker_miss_WorkerOrderedPreparation_n=1", counters.TakeReport(1, 1, 0, 0, 0));
        Assert.Equal(0, counters.WorkerMissCalls.Sum());
        Assert.Equal(0, counters.WorkerTicks.Sum());
    }
}
