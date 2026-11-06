// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.VideoOut;
using Xunit;
using static SharpEmu.Libs.VideoOut.ImageAcquisitionProfile;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ImageAcquisitionProfileTests
{
    private static readonly object Cache = new();
    private static Pair Key(uint generation = 1) => new(Cache, new ResourceSlotIdentifier(1, generation), ImageViewDescription.Default);
    private static Snapshot Clean => new(null!, 42, 1, 1, ResourceSlotIdentifier.Invalid,
        true, false, false, true, false, true, false, new GuestSpan(0x10000, 0x4000), default, default, false);

    private static void Observe(Tracker tracker, Pair? key = null, Snapshot? state = null,
        Caller caller = Caller.Stage, bool ordered = true,
        Dictionary<Pair, Observation>? preparation = null, Dictionary<Pair, Observation>? segment = null)
    {
        var snapshot = state ?? Clean;
        tracker.Complete(key ?? Key(), snapshot, tracker.Revision, snapshot, caller, ordered, preparation, segment);
    }

    private static long Field(string report, string field) => long.Parse(report.Split(' ')
        .Single(part => part.StartsWith(field + "=", StringComparison.Ordinal)).Split('=')[1]);

    [Fact]
    public void ScopesAndFrameRotationHaveIndependentHistories()
    {
        var tracker = new Tracker();
        var preparation = new Dictionary<Pair, Observation>();
        var segment = new Dictionary<Pair, Observation>();
        Observe(tracker, preparation: preparation, segment: segment);
        Observe(tracker, preparation: preparation, segment: segment);
        preparation = new();
        Observe(tracker, preparation: preparation, segment: segment);
        tracker.RecordFrame();
        segment = new();
        preparation = new();
        Observe(tracker, preparation: preparation, segment: segment);
        tracker.RecordFrame();
        var report = tracker.TakeReport(5);
        Assert.Equal(4, Field(report, "sampled"));
        Assert.Equal(1, Field(report, "repeat_preparation"));
        Assert.Equal(2, Field(report, "repeat_segment"));
        Assert.Equal(2, Field(report, "repeat_frame"));
        Assert.Equal(1, Field(report, "repeat_previous_frame"));
        Assert.Equal(1, Field(report, "unchanged_previous_frame"));
        Assert.Equal(2, Field(report, "frame_unique_sum"));
        Assert.Equal(1, Field(report, "frame_unique_max"));
    }

    [Theory]
    [InlineData(0x10000UL, 4UL, 0)]
    [InlineData(0x20000UL, 4UL, 1)]
    [InlineData(0UL, ulong.MaxValue, 0)]
    public void RangeEventsRejectRelevantChangesEvenWhenStateReturnsToClean(ulong address, ulong size, long expected)
    {
        var tracker = new Tracker();
        Observe(tracker);
        tracker.Invalidate(address, size);
        Observe(tracker);
        var report = tracker.TakeReport(5);
        Assert.Equal(1, Field(report, "repeat_frame"));
        Assert.Equal(expected, Field(report, "unchanged_frame"));
    }

    [Fact]
    public void GenerationAndViewArePartOfIdentity()
    {
        var tracker = new Tracker();
        Observe(tracker);
        Observe(tracker, key: Key(2));
        Observe(tracker, key: Key() with { View = ImageViewDescription.Default with { BaseLevel = 1 } });
        tracker.RecordFrame();
        var report = tracker.TakeReport(5);
        Assert.Equal(0, Field(report, "repeat_frame"));
        Assert.Equal(3, Field(report, "frame_unique_sum"));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("write")]
    [InlineData("replacement")]
    [InlineData("ownership")]
    [InlineData("metadata")]
    [InlineData("pending")]
    [InlineData("rebind")]
    public void ChangedOrUncertainImagesAreNotSkipCandidates(string change)
    {
        var tracker = new Tracker();
        Observe(tracker);
        var changed = change switch
        {
            "dirty" => Clean with { Clean = false },
            "write" => Clean with { GpuWrite = 2 },
            "replacement" => Clean with { Handle = 43 },
            "ownership" => Clean with { BufferOwner = true },
            "metadata" => Clean with { HasMetadata = true },
            "pending" => Clean with { PublicationPending = true },
            _ => Clean with { Rebind = true },
        };
        Observe(tracker, state: changed);
        Assert.Equal(0, Field(tracker.TakeReport(5), "unchanged_frame"));
    }

    [Fact]
    public void WorkerAndExistingMemoReuseDoNotInflateFullAcquireOpportunity()
    {
        var tracker = new Tracker();
        Observe(tracker);
        Observe(tracker, caller: Caller.Memo);
        Observe(tracker, caller: Caller.Worker, ordered: false);
        Observe(tracker, caller: Caller.Prologue);
        Observe(tracker, caller: Caller.Other);
        var report = tracker.TakeReport(5);
        Assert.Equal(5, Field(report, "sampled"));
        Assert.Equal(1, Field(report, "memo_hit"));
        Assert.Equal(1, Field(report, "worker_observations"));
        Assert.Equal(2, Field(report, "unchanged_frame"));
        Assert.Equal(1, Field(report, "full_unchanged_frame"));
    }

    [Fact]
    public void ExpiredHistoryCannotCertifyReuseAndWindowReportKeepsFrameHistory()
    {
        var tracker = new Tracker();
        Observe(tracker);
        for (var index = 0; index < 8193; index++) tracker.Invalidate(0x20000, 4);
        Observe(tracker);
        var report = tracker.TakeReport(5);
        Assert.Equal(0, Field(report, "unchanged_frame"));
        Assert.Equal(1, Field(report, "history_lost"));
        Observe(tracker);
        report = tracker.TakeReport(5);
        Assert.Equal(1, Field(report, "sampled"));
        Assert.Equal(1, Field(report, "repeat_frame"));
        Assert.Equal(1, Field(report, "unchanged_frame"));
    }

    [Fact]
    public void InvalidationDuringAnAcquireCannotBecomeACleanBaseline()
    {
        var tracker = new Tracker();
        Observe(tracker);
        var revision = tracker.Revision;
        tracker.Invalidate(0x10000, 4);
        tracker.Complete(Key(), Clean, revision, Clean, Caller.Stage, true, null, null);
        Observe(tracker);
        Assert.Equal(0, Field(tracker.TakeReport(5), "unchanged_frame"));
    }

    [Fact]
    public void PendingPublicationRejectsOtherwiseIdenticalImages()
    {
        var tracker = new Tracker();
        Observe(tracker);
        tracker.BeginPublication();
        Observe(tracker);
        Observe(tracker);
        tracker.EndPublication();
        Observe(tracker);
        Assert.Equal(0, Field(tracker.TakeReport(5), "unchanged_frame"));
    }

    [Fact]
    public void DisabledScopesDoNotAllocateOrInitializeThreadState()
    {
        if (ImageAcquisitionProfile.Enabled) return;
        long allocated = -1;
        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            default(Preparation).Dispose();
            default(Acquisition).Complete();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.False(ImageAcquisitionProfile.InPreparation);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, allocated);
    }
}
