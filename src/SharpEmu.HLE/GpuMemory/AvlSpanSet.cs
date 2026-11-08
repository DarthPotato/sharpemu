// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// One span of a persistent AVL tree. Nodes never change once built: an insertion or deletion copies the path it
// changes, so a root, once published, describes the same spans forever and can be read without a lock.
internal sealed class SpanNode(ulong start, ulong end, SpanNode? left, SpanNode? right)
{
    public readonly ulong Start = start;
    public readonly ulong End = end;
    public readonly SpanNode? Left = left;
    public readonly SpanNode? Right = right;
    public readonly int Height = Math.Max(left?.Height ?? 0, right?.Height ?? 0) + 1;

    public static SpanNode? FindAtOrBelow(SpanNode? root, ulong address)
    {
        SpanNode? result = null;
        for (var node = root; node is not null;)
        {
            if (node.Start <= address) { result = node; node = node.Right; }
            else node = node.Left;
        }
        return result;
    }

    public static SpanNode? FindAtOrAbove(SpanNode? root, ulong address)
    {
        SpanNode? result = null;
        for (var node = root; node is not null;)
        {
            if (node.Start >= address) { result = node; node = node.Left; }
            else node = node.Right;
        }
        return result;
    }

    public static bool Overlaps(SpanNode? root, ulong start, ulong end)
    {
        var node = root;
        while (node is not null)
        {
            if (node.Start >= end) node = node.Left;
            else if (node.End <= start) node = node.Right;
            else return true;
        }
        return false;
    }
}

// Callers serialize changes. The tree does not change range ownership or locking. Snapshot() is O(1): it wraps the
// current root, which no later change can alter (each change used to copy every span into new arrays).
public sealed class AvlSpanSet
{
    private SpanNode? _root;
    private SpanSnapshot? _snapshot = SpanSnapshot.Empty;
    public bool IsEmpty => _root is null;
    public void Clear()
    {
        _root = null;
        _snapshot = SpanSnapshot.Empty;
    }

    public void Add(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var start = address;
        var end = address + size;
        var previous = SpanNode.FindAtOrBelow(_root, start);
        if (previous is not null && previous.End >= start)
        {
            if (previous.End >= end) return;
            start = previous.Start;
            _root = Delete(_root, start);
        }
        _snapshot = null;
        for (var next = SpanNode.FindAtOrAbove(_root, start); next is not null && next.Start <= end; next = SpanNode.FindAtOrAbove(_root, start))
        {
            end = Math.Max(end, next.End);
            _root = Delete(_root, next.Start);
        }
        _root = Insert(_root, start, end);
    }

    public void Remove(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var end = address + size;
        var current = SpanNode.FindAtOrBelow(_root, address);
        if (current is null || current.End <= address) current = SpanNode.FindAtOrAbove(_root, address);
        if (current is null || current.Start >= end) return;
        _snapshot = null;
        while (current is not null && current.Start < end)
        {
            var spanStart = current.Start;
            var spanEnd = current.End;
            _root = Delete(_root, spanStart);
            if (spanStart < address) _root = Insert(_root, spanStart, address);
            if (spanEnd > end)
            {
                _root = Insert(_root, end, spanEnd);
                break;
            }
            current = SpanNode.FindAtOrAbove(_root, address);
        }
    }

    public bool Contains(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var node = SpanNode.FindAtOrBelow(_root, address);
        return node is not null && node.End >= address + size;
    }

    public bool Overlaps(ulong address, ulong size)
    {
        ValidateRange(address, size);
        return SpanNode.Overlaps(_root, address, address + size);
    }

    public SpanSnapshot Snapshot()
    {
        if (_snapshot is { } snapshot) return snapshot;
        return _snapshot = _root is null ? SpanSnapshot.Empty : new SpanSnapshot(_root);
    }

    public List<GuestSpan> GetOverlappingRanges(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var result = new List<GuestSpan>();
        VisitOverlaps(_root, address, address + size, result);
        return result;
    }

    private static void VisitOverlaps(SpanNode? node, ulong start, ulong end, List<GuestSpan> result)
    {
        if (node is null) return;
        if (node.End <= start)
        {
            VisitOverlaps(node.Right, start, end, result);
            return;
        }
        if (node.Start >= end)
        {
            VisitOverlaps(node.Left, start, end, result);
            return;
        }
        VisitOverlaps(node.Left, start, end, result);
        var overlapStart = Math.Max(start, node.Start);
        var size = Math.Min(end, node.End) - overlapStart;
        result.Add(new GuestSpan(overlapStart, size));
        VisitOverlaps(node.Right, start, end, result);
    }

    private static int Height(SpanNode? node) => node?.Height ?? 0;

    // A node over the given children, rotated once or twice when their heights differ by more than one.
    private static SpanNode Balance(ulong start, ulong end, SpanNode? left, SpanNode? right)
    {
        var difference = Height(left) - Height(right);
        if (difference > 1)
        {
            if (Height(left!.Left) >= Height(left.Right))
                return new SpanNode(left.Start, left.End, left.Left, new SpanNode(start, end, left.Right, right));
            var middle = left.Right!;
            return new SpanNode(middle.Start, middle.End,
                new SpanNode(left.Start, left.End, left.Left, middle.Left), new SpanNode(start, end, middle.Right, right));
        }
        if (difference < -1)
        {
            if (Height(right!.Right) >= Height(right.Left))
                return new SpanNode(right.Start, right.End, new SpanNode(start, end, left, right.Left), right.Right);
            var middle = right.Left!;
            return new SpanNode(middle.Start, middle.End,
                new SpanNode(start, end, left, middle.Left), new SpanNode(right.Start, right.End, middle.Right, right.Right));
        }
        return new SpanNode(start, end, left, right);
    }

    private static SpanNode Insert(SpanNode? node, ulong start, ulong end)
    {
        if (node is null) return new SpanNode(start, end, null, null);
        if (start < node.Start) return Balance(node.Start, node.End, Insert(node.Left, start, end), node.Right);
        if (start > node.Start) return Balance(node.Start, node.End, node.Left, Insert(node.Right, start, end));
        return new SpanNode(start, end, node.Left, node.Right);
    }

    private static SpanNode? Delete(SpanNode? node, ulong start)
    {
        if (node is null) return null;
        if (start < node.Start) return Balance(node.Start, node.End, Delete(node.Left, start), node.Right);
        if (start > node.Start) return Balance(node.Start, node.End, node.Left, Delete(node.Right, start));
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        var successor = node.Right;
        while (successor.Left is not null) successor = successor.Left;
        return Balance(successor.Start, successor.End, node.Left, Delete(node.Right, successor.Start));
    }

    private static void ValidateRange(ulong address, ulong size)
    {
        if (size == 0 || address + size < address)
            Environment.FailFast($"The address range is invalid: 0x{address:X16}, 0x{size:X16}.");
    }
}
