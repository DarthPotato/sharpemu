// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;
using static SharpEmu.Libs.VideoOut.FrameTimelineAccumulator;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class FrameTimelineTests
{
    [Fact]
    public void NestedWaitCategoriesSplitWithoutChargingTheOuterCategoryTwice()
    {
        var accumulator = new FrameTimelineAccumulator(0);
        accumulator.Change(Metric.Render, 1, 0);
        accumulator.Change(Metric.Prefetch, 1, 2);
        accumulator.Change(Metric.Prefetch, -1, 4);
        accumulator.Change(Metric.GpuWait, 1, 4);
        var first = accumulator.Boundary(6);
        accumulator.Change(Metric.GpuWait, -1, 8);
        accumulator.Change(Metric.Prefetch, 1, 8);
        accumulator.Change(Metric.Prefetch, -1, 9);
        accumulator.Change(Metric.Render, -1, 10);
        var second = accumulator.Boundary(12);
        Assert.Equal(2, first.Ticks[(int)Metric.Prefetch]);
        Assert.Equal(2, first.Ticks[(int)Metric.GpuWait]);
        Assert.Equal(1, second.Ticks[(int)Metric.Prefetch]);
        Assert.Equal(2, second.Ticks[(int)Metric.GpuWait]);
        Assert.Equal(4, second.Ticks[(int)Metric.Render]);
    }

    [Fact]
    public void CompletionRetainsFirstObservationAcrossLaterPollsAndCoalescedTicks()
    {
        var history = new CompletionHistory();
        Assert.Null(history.Timestamp(1));
        history.Observe(3, 100);
        history.Observe(3, 110);
        history.Observe(8, 120);
        Assert.Equal(100L, history.Timestamp(1));
        Assert.Equal(100L, history.Timestamp(3));
        Assert.Equal(120L, history.Timestamp(4));
        Assert.Null(history.Timestamp(9));
        Assert.Null(history.Timestamp(0));
    }

    [Fact]
    public void CompletionHistoryWrapReportsUnknownRatherThanAnInventedTimestamp()
    {
        var history = new CompletionHistory();
        for (ulong i = 1; i <= 4100; i++) history.Observe(i, (long)i * 10);
        Assert.Null(history.Timestamp(1));
        Assert.Equal(41000L, history.Timestamp(4100));
    }

    [Fact]
    public void BoundarySplitsActiveWaitAndResetsEventsAndCounts()
    {
        var accumulator = new FrameTimelineAccumulator(100);
        accumulator.Change(Metric.Space, 1, 110);
        accumulator.Note(Event.Admission, 112);
        accumulator.Note(Event.Admission, 115);
        accumulator.Note(Event.GpuSubmit, 118, 9);
        accumulator.Draw(false);
        var first = accumulator.Boundary(120);
        accumulator.Change(Metric.Space, -1, 135);
        accumulator.Draw(true);
        var second = accumulator.Boundary(140);
        Assert.Equal(10, first.Ticks[(int)Metric.Space]);
        Assert.Equal(15, second.Ticks[(int)Metric.Space]);
        Assert.Equal(112, first.First[(int)Event.Admission]);
        Assert.Equal(115, first.Last[(int)Event.Admission]);
        Assert.Equal(2, first.Counts[(int)Event.Admission]);
        Assert.Equal(0, second.Counts[(int)Event.Admission]);
        Assert.Equal(9UL, first.LastTick);
        Assert.Equal(0UL, second.LastTick);
        Assert.Equal(1, first.Draws);
        Assert.Equal(1, second.Dispatches);
        Assert.Equal(120, second.Start);
        Assert.Equal(140, second.End);
    }

    [Fact]
    public void ConcurrentGuestWaitsAccumulateThreadTimeAcrossBoundary()
    {
        var accumulator = new FrameTimelineAccumulator(0);
        accumulator.Change(Metric.GuestWait, 1, 2);
        accumulator.Change(Metric.GuestWait, 1, 4);
        Assert.Equal(10, accumulator.Boundary(8).Ticks[(int)Metric.GuestWait]);
        accumulator.Change(Metric.GuestWait, -1, 10);
        accumulator.Change(Metric.GuestWait, -1, 11);
        Assert.Equal(5, accumulator.Boundary(12).Ticks[(int)Metric.GuestWait]);
    }

    [Fact]
    public void EmptyAndSameTimestampBoundariesDoNotLeakPreviousFrame()
    {
        var accumulator = new FrameTimelineAccumulator(10);
        accumulator.Note(Event.FlipRequest, 10);
        var first = accumulator.Boundary(10);
        var empty = accumulator.Boundary(10);
        Assert.Equal(1, first.Counts[(int)Event.FlipRequest]);
        Assert.All(empty.Counts, value => Assert.Equal(0, value));
        Assert.All(empty.Ticks, value => Assert.Equal(0, value));
    }

    [Fact]
    public void AverageWeightsFramesAndExcludesMissingTimestampSamples()
    {
        var accumulator = new FrameTimelineAccumulator(100);
        var average = new Average();
        Assert.Equal(0, average.Mean(12));
        accumulator.Note(Event.Admission, 105);
        accumulator.Change(Metric.Command, 1, 110);
        accumulator.Draw(false);
        average.Add(accumulator.Boundary(120));
        accumulator.Change(Metric.Command, -1, 130);
        average.Add(accumulator.Boundary(160));
        Assert.Equal(2, average.Frames);
        Assert.Equal(30, average.Mean(average.Elapsed));
        Assert.Equal(10, average.Mean(average.Ticks[(int)Metric.Command]));
        Assert.Equal(0.5, average.Mean(average.Draws));
        Assert.Equal(1, average.Samples[(int)Event.Admission]);
        Assert.Equal(5, average.First[(int)Event.Admission]);
    }
}
