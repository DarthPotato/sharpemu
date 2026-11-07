// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;
using static SharpEmu.Libs.VideoOut.RenderPhaseProfile;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class MovableProfileTests
{
    [Fact]
    public void ExclusiveIntervalsReconcileAcrossKindsAndNestedCategories()
    {
        var counters = new MovableAccounting();
        var outside = new OperationWork();
        var draw = new OperationWork { Kind = WorkKind.Draw };
        var dispatch = new OperationWork { Kind = WorkKind.Dispatch };
        counters.Charge(ref outside, Phase.VulkanCommands, 7); // Outside an operation is never claimed movable.
        counters.Charge(ref draw, Phase.DescriptorPreparation, 11);
        counters.Charge(ref draw, Phase.ImmutablePacking, 13);
        counters.Charge(ref draw, Phase.OrderedOwnership, 17); // Ordered child suspends packing.
        counters.Charge(ref draw, Phase.ImmutablePacking, 19); // Packing resumes.
        counters.Charge(ref draw, Phase.GpuCompletionWait, 23); // Wait is not movable CPU work.
        counters.Charge(ref dispatch, Phase.VulkanCommands, 29);
        counters.Finish(in draw);
        counters.Finish(in dispatch);
        Assert.Equal(119, counters.Ticks.Sum());
        Assert.Equal(28, counters.Ticks[(int)WorkKind.Draw * 3 + (int)WorkCategory.Ordered]);
        Assert.Equal(32, counters.Ticks[(int)WorkKind.Draw * 3 + (int)WorkCategory.Movable]);
        Assert.Equal(23, counters.Ticks[(int)WorkKind.Draw * 3 + (int)WorkCategory.Other]);
        Assert.Equal(1, counters.CategoryOperations[(int)WorkKind.Draw * 3 + (int)WorkCategory.Movable]);
        Assert.Equal(29, counters.Ticks[(int)WorkKind.Dispatch * 3 + (int)WorkCategory.Movable]);
        Assert.Equal(7, counters.Ticks[(int)WorkCategory.Other]);
    }

    [Fact]
    public void ReasonsDeduplicateWithinAnOperationAndBlockedUnionDoesNotDoubleCount()
    {
        var counters = new MovableAccounting();
        var draw = new OperationWork { Kind = WorkKind.Draw };
        draw.Blocks |= 1UL << (int)PreparationBlock.StencilOwnership;
        draw.Blocks |= 1UL << (int)PreparationBlock.StencilOwnership;
        draw.Blocks |= 1UL << (int)PreparationBlock.MarkGpuWritten;
        counters.Charge(ref draw, Phase.OrderedOwnership, 10);
        counters.Charge(ref draw, Phase.VulkanCommands, 20);
        counters.Finish(in draw);
        Assert.Equal(1, counters.Blocked[(int)WorkKind.Draw]);
        Assert.Equal(30, counters.BlockedTicks[(int)WorkKind.Draw]);
        foreach (var reason in new[] { PreparationBlock.StencilOwnership, PreparationBlock.MarkGpuWritten })
        {
            var index = (int)WorkKind.Draw * (int)PreparationBlock.Count + (int)reason;
            Assert.Equal(1, counters.ReasonOperations[index]);
            Assert.Equal(30, counters.ReasonTicks[index]);
            Assert.Equal(20, counters.ReasonMovableTicks[index]);
        }
    }

    [Theory]
    [InlineData((int)Phase.DescriptorCommit, (int)WorkCategory.Other)]
    [InlineData((int)Phase.DrawResourcePreparation, (int)WorkCategory.Other)]
    [InlineData((int)Phase.PipelineCreation, (int)WorkCategory.Other)]
    [InlineData((int)Phase.GpuCompletionWait, (int)WorkCategory.Other)]
    [InlineData((int)Phase.ExistingPipeline, (int)WorkCategory.Movable)]
    [InlineData((int)Phase.OrderedBarrierDecision, (int)WorkCategory.Ordered)]
    public void MixedWorkAndWaitsAreNotClaimedMovable(int phase, int category) =>
        Assert.Equal((WorkCategory)category, CategoryOf((Phase)phase));

    [Fact]
    public void OrderedPrerequisiteDoesNotImplyCurrentWorkerRecordingMustFail()
    {
        var counters = new MovableAccounting();
        var dispatch = new OperationWork
        {
            Kind = WorkKind.Dispatch,
            Dependencies = 1UL << (int)PreparationBlock.BufferOwnership,
        };
        counters.Charge(ref dispatch, Phase.BufferAcquisitionChecks, 31);
        counters.Finish(in dispatch);
        var index = (int)WorkKind.Dispatch * (int)PreparationBlock.Count + (int)PreparationBlock.BufferOwnership;
        Assert.Equal(1, counters.DependencyOperations[index]);
        Assert.Equal(0, counters.ReasonOperations[index]);
        Assert.Equal(0, counters.Blocked[(int)WorkKind.Dispatch]);
        Assert.Equal(31, counters.Ticks.Sum());
    }

    [Fact]
    public void DisabledOperationAndDetailDoNotAllocateOnFreshThread()
    {
        if (MovableEnabled) return;
        long allocated = -1;
        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            using (MeasureOperation(WorkKind.Draw))
            using (MeasureMovableDetail(Phase.ImmutablePacking))
                BlockPreparation(PreparationBlock.GuestRead);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, allocated);
    }
}
