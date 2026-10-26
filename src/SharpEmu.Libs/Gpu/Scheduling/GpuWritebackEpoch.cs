// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// Counts the stores of GPU data into guest memory: buffer and image downloads, and buffer transfers
// the caches complete there. Work done ahead of the render thread from guest memory is still
// current while no store is in progress and the count has not moved since the work began.
//
// A store counts once when it starts and again when it ends. Work that began before a store sees
// the first count; work that began while the bytes were being written sees the second, or the store
// still in progress. Counting only at the start let work that began mid-store read old or partly
// copied bytes and still pass.
internal static class GpuWritebackEpoch
{
    private static long _value;
    private static int _activeWriters;

    public static long Value => Volatile.Read(ref _value);

    // Whether work that recorded `recorded` at its start saw no store since.
    public static bool Unchanged(long recorded) =>
        Volatile.Read(ref _activeWriters) == 0 && Volatile.Read(ref _value) == recorded;

    // Wraps one store of GPU data into guest memory; may run on any thread.
    public static WriteScope BeginWrite(ulong address, ulong size)
    {
        Interlocked.Increment(ref _activeWriters);
        if (!RecordsHistory)
        {
            return new WriteScope(Interlocked.Increment(ref _value));
        }

        // Counted and recorded together, so the floor never misses a store and the history stays in
        // count order.
        lock (HistoryGate)
        {
            var epoch = Interlocked.Increment(ref _value);
            History[_historyCount % HistoryCapacity] = (epoch, address, size);
            _historyCount++;
            ActiveBegins.Add(epoch);
            return new WriteScope(epoch);
        }
    }

    // The ranges of recent stores are kept so a resolution whose epoch moved is still taken when every
    // store since its start was elsewhere. Opt-in (SHARPEMU_PREFETCH_RANGE_AWARE=1) until reads and
    // stores are compared through backing identities: two guest views of one backing make an
    // overlapping store look disjoint. Off, any store rejects (the render profile still keeps the
    // history to report why).
    public static readonly bool RangeAware = Environment.GetEnvironmentVariable("SHARPEMU_PREFETCH_RANGE_AWARE") == "1";
    public static readonly bool RecordsHistory = RangeAware || VideoOut.RenderPhaseProfile.Enabled;

    public static bool HasActiveWriters => Volatile.Read(ref _activeWriters) != 0;
    private const int HistoryCapacity = 4096;
    private static readonly (long Epoch, ulong Address, ulong Size)[] History = new (long, ulong, ulong)[HistoryCapacity];
    private static readonly List<long> ActiveBegins = new();
    private static readonly object HistoryGate = new();
    private static long _historyCount;

    // Taken where work records Value: the start count of the oldest store still writing then, or
    // Value itself. Every store that can overlap the work started at or after it.
    public static long HistoryFloor()
    {
        if (!RecordsHistory)
        {
            return Value;
        }

        lock (HistoryGate)
        {
            var floor = Value;
            foreach (var begin in ActiveBegins)
            {
                floor = Math.Min(floor, begin);
            }

            return floor;
        }
    }

    public enum StaleCause
    {
        // A store since the floor overlapped a range the resolution read.
        Overlapping,
        // Every store since the floor was elsewhere.
        Disjoint,
        // The history no longer reaches back to the floor.
        Unknown,
    }

    public static StaleCause ClassifyStores(long floor, Func<ulong, ulong, bool> wasRead)
    {
        lock (HistoryGate)
        {
            var oldest = Math.Max(0, _historyCount - HistoryCapacity);
            for (var index = _historyCount - 1; index >= oldest; index--)
            {
                var (epoch, address, size) = History[index % HistoryCapacity];
                if (epoch < floor)
                {
                    return StaleCause.Disjoint;
                }

                if (wasRead(address, size))
                {
                    return StaleCause.Overlapping;
                }
            }

            return oldest == 0 ? StaleCause.Disjoint : StaleCause.Unknown;
        }
    }

    public readonly ref struct WriteScope
    {
        // The store's start count; 0 for a scope that is not a store.
        private readonly long _begin;

        internal WriteScope(long begin) => _begin = begin;

        public void Dispose()
        {
            if (_begin == 0)
            {
                return;
            }

            // Counted before the writer leaves: a reader that sees no writer also sees the count move.
            Interlocked.Increment(ref _value);
            if (RecordsHistory)
            {
                lock (HistoryGate)
                {
                    ActiveBegins.Remove(_begin);
                }
            }

            Interlocked.Decrement(ref _activeWriters);
        }
    }
}
