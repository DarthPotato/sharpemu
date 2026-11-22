// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// Sorted, disjoint spans read without a lock: copied arrays (SpanSet) or an immutable tree root (AvlSpanSet).
public sealed class SpanSnapshot
{
    public static readonly SpanSnapshot Empty = new([], []);

    private readonly ulong[] starts;
    private readonly ulong[] ends;
    private readonly SpanNode? _root;
    // A tree snapshot's spans as arrays, built on first use of Starts/Ends (tests and diagnostics only); one reference so
    // a racing reader sees both arrays or neither.
    private ulong[][]? _treeArrays;

    public SpanSnapshot(ulong[] starts, ulong[] ends)
    {
        this.starts = starts;
        this.ends = ends;
    }

    internal SpanSnapshot(SpanNode root)
    {
        starts = ends = [];
        _root = root;
    }

    internal ReadOnlySpan<ulong> Starts => _root is null ? starts : TreeArrays()[0];
    internal ReadOnlySpan<ulong> Ends => _root is null ? ends : TreeArrays()[1];

    private ulong[][] TreeArrays()
    {
        if (Volatile.Read(ref _treeArrays) is { } arrays) return arrays;
        var treeStarts = new List<ulong>();
        var treeEnds = new List<ulong>();
        SpanNode.ForEachOverlap(_root, 0, ulong.MaxValue, (treeStarts, treeEnds),
            static (lists, start, size) => { lists.treeStarts.Add(start); lists.treeEnds.Add(start + size); });
        arrays = [treeStarts.ToArray(), treeEnds.ToArray()];
        Volatile.Write(ref _treeArrays, arrays);
        return arrays;
    }

    // Contains remembers, per thread, the span of its last complete hit in a snapshot; SHARPEMU_SPAN_CONTAINS_CACHE=0
    // searches every query. The span's bounds are remembered (not an array index) so tree snapshots cache the same way.
    private static readonly bool CacheLastHit = Environment.GetEnvironmentVariable("SHARPEMU_SPAN_CONTAINS_CACHE") != "0";

    [ThreadStatic] private static SpanSnapshot? _lastSnapshot;
    [ThreadStatic] private static ulong _lastStart;
    [ThreadStatic] private static ulong _lastEnd;

    public bool Contains(ulong address, ulong size) => CacheLastHit
        ? ContainsWithLastHit(address, size)
        : ContainsUncached(address, size);

    internal bool ContainsWithLastHit(ulong address, ulong size)
    {
        // Snapshot identity includes mapping changes. Only a complete positive range can hit; a zero-sized or
        // overflowing request gives the answer ContainsUncached gives.
        if (ReferenceEquals(_lastSnapshot, this) && size != 0 &&
            address >= _lastStart && address < _lastEnd && size <= _lastEnd - address)
            return true;

        ulong spanStart, spanEnd;
        if (_root is not null)
        {
            if (SpanNode.FindAtOrBelow(_root, address) is not { } node) return false;
            spanStart = node.Start;
            spanEnd = node.End;
        }
        else
        {
            var index = Array.BinarySearch(starts, address);
            if (index < 0) index = ~index - 1;
            if (index < 0) return false;
            spanStart = starts[index];
            spanEnd = ends[index];
        }

        var contained = spanEnd >= address + size;
        if (contained && size != 0 && address < spanEnd && size <= spanEnd - address)
        {
            _lastStart = spanStart;
            _lastEnd = spanEnd;
            _lastSnapshot = this;
        }
        return contained;
    }

    private bool ContainsUncached(ulong address, ulong size)
    {
        if (_root is not null)
        {
            var node = SpanNode.FindAtOrBelow(_root, address);
            return node is not null && node.End >= address + size;
        }

        var index = Array.BinarySearch(starts, address);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return index >= 0 && ends[index] >= address + size;
    }

    // Immutable snapshot: callbacks may recover GPU data without holding the mapping lock.
    internal void ForEachOverlap(ulong address, ulong size, Action<ulong, ulong> visit)
    {
        if (_root is not null)
        {
            SpanNode.ForEachOverlap(_root, address, address + size, visit, static (callback, part, partSize) => callback(part, partSize));
            return;
        }

        var end = address + size;
        var index = Array.BinarySearch(starts, address);
        if (index < 0) index = Math.Max(0, ~index - 1);
        for (; index < starts.Length && starts[index] < end; index++)
        {
            var first = Math.Max(address, starts[index]);
            var last = Math.Min(end, ends[index]);
            if (first < last) visit(first, last - first);
        }
    }

    // The walk above with its state passed to a static callback; the same overlaps, in the same order.
    internal void ForEachOverlap<TState>(ulong address, ulong size, TState state, Action<TState, ulong, ulong> visit)
    {
        if (_root is not null)
        {
            SpanNode.ForEachOverlap(_root, address, address + size, state, visit);
            return;
        }

        var end = address + size;
        var index = Array.BinarySearch(starts, address);
        if (index < 0) index = Math.Max(0, ~index - 1);
        for (; index < starts.Length && starts[index] < end; index++)
        {
            var first = Math.Max(address, starts[index]);
            var last = Math.Min(end, ends[index]);
            if (first < last) visit(state, first, last - first);
        }
    }

    public bool TryGetContaining(ulong address, out ulong start, out ulong end)
    {
        if (_root is not null)
        {
            if (SpanNode.FindAtOrBelow(_root, address) is { } node && node.End > address)
            {
                start = node.Start;
                end = node.End;
                return true;
            }

            start = 0;
            end = 0;
            return false;
        }

        var index = Array.BinarySearch(starts, address);
        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index >= 0 && ends[index] > address)
        {
            start = starts[index];
            end = ends[index];
            return true;
        }

        start = 0;
        end = 0;
        return false;
    }

    public bool Overlaps(ulong address, ulong size)
    {
        if (_root is not null)
        {
            return SpanNode.Overlaps(_root, address, address + size);
        }

        var index = Array.BinarySearch(starts, address + size);
        if (index < 0)
        {
            index = ~index;
        }

        return index > 0 && ends[index - 1] > address;
    }
}

public sealed class SpanSet
{
    private readonly SortedList<ulong, ulong> _spans = new();

    public bool IsEmpty => _spans.Count == 0;

    public void Add(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var start = address;
        var end = address + size;

        var index = UpperBound(start);
        if (index > 0 && _spans.GetValueAtIndex(index - 1) >= start)
        {
            index--;
        }

        while (index < _spans.Count && _spans.GetKeyAtIndex(index) <= end)
        {
            start = Math.Min(start, _spans.GetKeyAtIndex(index));
            end = Math.Max(end, _spans.GetValueAtIndex(index));
            _spans.RemoveAt(index);
        }

        _spans[start] = end;
    }

    public void Remove(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var start = address;
        var end = address + size;

        var index = UpperBound(start);
        if (index > 0 && _spans.GetValueAtIndex(index - 1) > start)
        {
            index--;
        }

        while (index < _spans.Count && _spans.GetKeyAtIndex(index) < end)
        {
            var spanStart = _spans.GetKeyAtIndex(index);
            var spanEnd = _spans.GetValueAtIndex(index);
            _spans.RemoveAt(index);

            if (spanStart < start)
            {
                _spans[spanStart] = start;
                index++;
            }

            if (spanEnd > end)
            {
                _spans[end] = spanEnd;
                index++;
            }
        }
    }

    public void Clear() => _spans.Clear();

    public bool Contains(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var index = UpperBound(address);
        return index > 0 && _spans.GetValueAtIndex(index - 1) >= address + size;
    }

    public bool Overlaps(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var index = LowerBound(address + size);
        return index > 0 && _spans.GetValueAtIndex(index - 1) > address;
    }

    public SpanSnapshot Snapshot()
    {
        var starts = new ulong[_spans.Count];
        var ends = new ulong[_spans.Count];
        for (var index = 0; index < starts.Length; index++)
        {
            starts[index] = _spans.GetKeyAtIndex(index);
            ends[index] = _spans.GetValueAtIndex(index);
        }

        return new SpanSnapshot(starts, ends);
    }

    public void CopyTo(List<GuestSpan> destination)
    {
        destination.EnsureCapacity(destination.Count + _spans.Count);
        for (var index = 0; index < _spans.Count; index++)
        {
            var start = _spans.GetKeyAtIndex(index);
            destination.Add(new GuestSpan(start, _spans.GetValueAtIndex(index) - start));
        }
    }

    public void ForEach(Action<ulong, ulong> visit)
    {
        foreach (var (start, end) in _spans)
        {
            visit(start, end - start);
        }
    }

    public List<GuestSpan> GetOverlappingRanges(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var result = new List<GuestSpan>();
        var end = address + size;
        var index = UpperBound(address);
        if (index > 0 && _spans.GetValueAtIndex(index - 1) > address)
        {
            index--;
        }

        for (; index < _spans.Count && _spans.GetKeyAtIndex(index) < end; index++)
        {
            var start = Math.Max(address, _spans.GetKeyAtIndex(index));
            var stop = Math.Min(end, _spans.GetValueAtIndex(index));
            result.Add(new GuestSpan(start, stop - start));
        }

        return result;
    }

    private int LowerBound(ulong key)
    {
        var low = 0;
        var high = _spans.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_spans.GetKeyAtIndex(mid) < key)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int UpperBound(ulong key)
    {
        var low = 0;
        var high = _spans.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_spans.GetKeyAtIndex(mid) <= key)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static void ValidateRange(ulong address, ulong size)
    {
        if (size == 0 || address + size < address)
        {
            Environment.FailFast($"The address range is invalid: 0x{address:X16}, 0x{size:X16}.");
        }
    }
}
