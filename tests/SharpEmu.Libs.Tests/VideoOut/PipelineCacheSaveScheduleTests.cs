// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PipelineCacheSaveScheduleTests
{
    [Theory]
    [InlineData(null, 2000)]
    [InlineData("", 2000)]
    [InlineData("invalid", 2000)]
    [InlineData("-1", 2000)]
    [InlineData("99999999999999999999", 2000)]
    [InlineData("0", 0)]
    [InlineData("500", 500)]
    public void ThresholdDefaultsOrAcceptsNonnegativeOverride(string? value, long expected) =>
        Assert.Equal(expected, PipelineCacheSaveSchedule.ParseThreshold(value));

    [Theory]
    [InlineData(0, 79_000, false)]
    [InlineData(2000, 1999, false)]
    [InlineData(2000, 2000, false)]
    [InlineData(2000, 2001, true)]
    [InlineData(500, 501, true)]
    public void OnlyCompilesOverTheEnabledThresholdTriggerASave(long threshold, long compile, bool expected)
    {
        var schedule = new PipelineCacheSaveSchedule(threshold);
        schedule.RecordCompile(compile, 100_000);
        Assert.Equal(expected, schedule.TryTakeSave(125_000, 0, out var trigger));
        if (expected) Assert.Equal(compile, trigger);
    }

    [Fact]
    public void BurstWaitsForLastExpensiveCompileAndSavesOnce()
    {
        var schedule = new PipelineCacheSaveSchedule(2000);
        schedule.RecordCompile(79_000, 100_000);
        schedule.RecordCompile(65_000, 120_000);
        // Cheap compiles do not postpone an expensive snapshot.
        schedule.RecordCompile(100, 140_000);
        Assert.False(schedule.IsDue(144_999, 0));
        Assert.False(schedule.TryTakeSave(144_999, 0, out _));
        Assert.True(schedule.IsDue(145_000, 0));
        Assert.True(schedule.TryTakeSave(145_000, 0, out var trigger));
        Assert.Equal(65_000, trigger);
        Assert.False(schedule.TryTakeSave(300_000, 0, out _));
    }

    [Fact]
    public void SteadyStreamOfExpensiveCompilesStillSavesAfterAMinute()
    {
        var schedule = new PipelineCacheSaveSchedule(2000);
        for (long now = 100_000; now <= 150_000; now += 10_000)
            schedule.RecordCompile(3000, now);
        Assert.False(schedule.TryTakeSave(159_999, 0, out _));
        Assert.True(schedule.TryTakeSave(160_000, 0, out _));
        // The next stream starts its own minute.
        for (long now = 170_000; now <= 220_000; now += 10_000)
            schedule.RecordCompile(now == 220_000 ? 5000 : 3000, now);
        Assert.False(schedule.TryTakeSave(229_999, 160_000, out _));
        Assert.True(schedule.TryTakeSave(230_000, 160_000, out var trigger));
        Assert.Equal(5000, trigger);
    }

    [Fact]
    public void AnyRecentSaveDefersTheNextSaveForAMinute()
    {
        var schedule = new PipelineCacheSaveSchedule(2000);
        schedule.RecordCompile(3000, 100_000);
        Assert.False(schedule.IsDue(159_999, 100_000));
        Assert.False(schedule.TryTakeSave(159_999, 100_000, out _));
        Assert.True(schedule.TryTakeSave(160_000, 100_000, out _));
        schedule.RecordCompile(4000, 170_000);
        Assert.False(schedule.TryTakeSave(219_999, 160_000, out _));
        Assert.True(schedule.TryTakeSave(220_000, 160_000, out _));
    }

    [Fact]
    public void NewCompileDuringSaveRemainsPending()
    {
        var schedule = new PipelineCacheSaveSchedule(2000);
        schedule.RecordCompile(3000, 100_000);
        Assert.True(schedule.TryTakeSave(125_000, 0, out _));
        schedule.RecordCompile(4000, 126_000);
        Assert.False(schedule.TryTakeSave(189_999, 130_000, out _));
        Assert.True(schedule.TryTakeSave(190_000, 130_000, out var trigger));
        Assert.Equal(4000, trigger);
    }

    [Theory]
    [InlineData(false, 3000)]
    [InlineData(true, 4000)]
    public void FailedSnapshotRetriesWithoutLosingNewerTrigger(bool newerCompile, long expectedTrigger)
    {
        var schedule = new PipelineCacheSaveSchedule(2000);
        schedule.RecordCompile(3000, 100_000);
        Assert.True(schedule.TryTakeSave(125_000, 0, out var trigger));
        if (newerCompile) schedule.RecordCompile(4000, 126_000);
        schedule.Retry(trigger, 130_000);
        Assert.False(schedule.TryTakeSave(189_999, 130_000, out _));
        Assert.True(schedule.TryTakeSave(190_000, 130_000, out var retryTrigger));
        Assert.Equal(expectedTrigger, retryTrigger);
    }
}
