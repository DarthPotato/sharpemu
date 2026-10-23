// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

public sealed class WriteTickMap
{
    private readonly List<Entry> _entries = [];
    private ulong _floor;

    private readonly record struct Entry(ulong Begin, ulong End, ulong Tick);

    public int Count => _entries.Count;

    public ulong Floor => _floor;

    public void Assign(ulong begin, ulong size, ulong tick)
    {
        if (size == 0)
        {
            return;
        }

        var end = begin + size;
        var index = FirstEndingAfter(begin);
        var removed = 0;
        while (index + removed < _entries.Count && _entries[index + removed].Begin < end)
        {
            removed++;
        }

        // The removed entries give way to the kept piece before the range, the range itself and the
        // kept piece after it. Overwriting in place and inserting or removing only the difference
        // keeps the common rewrite of an existing range from shifting the rest of the list.
        Span<Entry> replacement = stackalloc Entry[3];
        var count = 0;
        if (removed != 0)
        {
            var first = _entries[index];
            if (first.Begin < begin)
            {
                replacement[count++] = first with { End = begin };
            }
        }

        var middle = index + count;
        replacement[count++] = new Entry(begin, end, tick);
        if (removed != 0)
        {
            var last = _entries[index + removed - 1];
            if (last.End > end)
            {
                replacement[count++] = last with { Begin = end };
            }
        }

        var shared = Math.Min(count, removed);
        for (var offset = 0; offset < shared; offset++)
        {
            _entries[index + offset] = replacement[offset];
        }

        if (count > removed)
        {
            for (var offset = removed; offset < count; offset++)
            {
                _entries.Insert(index + offset, replacement[offset]);
            }
        }
        else if (removed > count)
        {
            _entries.RemoveRange(index + count, removed - count);
        }

        if (middle + 1 < _entries.Count)
        {
            _ = TryMerge(middle, middle + 1);
        }

        if (middle > 0)
        {
            _ = TryMerge(middle - 1, middle);
        }
    }

    public ulong MaxTick(ulong begin, ulong size)
    {
        var tick = _floor;
        if (size == 0)
        {
            return tick;
        }

        var end = begin + size;
        for (var index = FirstEndingAfter(begin); index < _entries.Count && _entries[index].Begin < end; index++)
        {
            tick = Math.Max(tick, _entries[index].Tick);
        }

        return tick;
    }

    public void Prune(ulong below)
    {
        var kept = 0;
        for (var index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            if (entry.Tick < below)
            {
                _floor = Math.Max(_floor, entry.Tick);
                continue;
            }

            _entries[kept++] = entry;
        }

        _entries.RemoveRange(kept, _entries.Count - kept);
    }

    private bool TryMerge(int left, int right)
    {
        var a = _entries[left];
        var b = _entries[right];
        if (a.End != b.Begin || a.Tick != b.Tick)
        {
            return false;
        }

        _entries[left] = a with { End = b.End };
        _entries.RemoveAt(right);
        return true;
    }

    private int FirstEndingAfter(ulong address)
    {
        var low = 0;
        var high = _entries.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_entries[middle].End <= address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
