// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

// The stream threads report their guest writes as stores; other threads report nothing. A decode
// records its reads into its own recorder and hands them to the resolution's.
[Collection(GuestMemoryStateCollection.Name)]
public sealed class StreamStoreHookTests
{
    [Fact]
    public void OnlyAStreamThreadReportsItsWrites()
    {
        var stores = new List<(ulong Address, ulong Size)>();
        var ended = new List<long>();
        StreamStoreHook.Attach((address, size) =>
        {
            stores.Add((address, size));
            return stores.Count;
        }, ended.Add);
        try
        {
            // Not a stream thread: nothing.
            var other = Task.Run(() => StreamStoreHook.Begin(0x1000, 8)).Result;
            Assert.Equal(0, other);
            Assert.Empty(stores);

            var token = Task.Run(() =>
            {
                StreamStoreHook.MarkStreamThread();
                var begun = StreamStoreHook.Begin(0x2000, 16);
                StreamStoreHook.End(begun);
                // An empty write is no store.
                return (begun, StreamStoreHook.Begin(0x3000, 0));
            }).Result;

            Assert.Equal([(0x2000UL, 16UL)], stores);
            Assert.Equal(1, token.begun);
            Assert.Equal(0, token.Item2);
            Assert.Equal([1L], ended);
        }
        finally
        {
            StreamStoreHook.Attach(static (_, _) => 0, static _ => { });
        }
    }

    [Fact]
    public void ANestedRecorderHandsItsRangesToTheOuterOne()
    {
        var outer = new ConcurrentReadRecorder();
        var nested = new ConcurrentReadRecorder();
        ConcurrentReadRecorder.Open(outer);
        try
        {
            var previous = ConcurrentReadRecorder.BeginNested(nested);
            Assert.Same(outer, previous);
            ConcurrentReadRecorder.Note(0x4000, 0x100);
            ConcurrentReadRecorder.EndNested(nested, previous);
            Assert.Same(outer, ConcurrentReadRecorder.Current);

            Assert.True(nested.Overlaps(0x40F0, 4));
            Assert.True(outer.Overlaps(0x40F0, 4));
            Assert.False(outer.Overlaps(0x5000, 4));
        }
        finally
        {
            ConcurrentReadRecorder.Close();
        }
    }

    [Fact]
    public void ANestedRecorderWithoutAnOuterOneRecordsOnlyItself()
    {
        var nested = new ConcurrentReadRecorder();
        var previous = ConcurrentReadRecorder.BeginNested(nested);
        Assert.Null(previous);
        ConcurrentReadRecorder.Note(0x6000, 0x10);
        ConcurrentReadRecorder.EndNested(nested, previous);

        Assert.Null(ConcurrentReadRecorder.Current);
        Assert.True(nested.Overlaps(0x6008, 4));
    }
}
