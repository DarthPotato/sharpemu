// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

// The guest ranges one resolution done ahead of the render thread read through ICpuMemory (shader
// headers and code, vertex and pixel input tables, ...). A store of GPU data into guest memory that
// overlaps none of them, nor the ranges read through the presenter's own readers, cannot have changed
// what the resolution saw. The base memory records at the entry of every read while a recorder is open
// on the calling thread; other threads pay one thread-static check.
public sealed class ConcurrentReadRecorder
{
    // Ranges kept per resolution; beyond it the resolution counts as having read anything.
    private const int Capacity = 256;

    [ThreadStatic]
    private static ConcurrentReadRecorder? _current;

    private readonly List<(ulong Address, ulong Size)> _ranges = new();

    public IReadOnlyList<(ulong Address, ulong Size)> Ranges => _ranges;

    // Too many separate ranges were read to keep them.
    public bool Overflowed { get; private set; }

    public static void Open(ConcurrentReadRecorder recorder) => _current = recorder;

    public static void Close() => _current = null;

    // Called at the entry of every base-memory read, before any early return.
    public static void Note(ulong address, ulong size)
    {
        if (_current is { } recorder)
        {
            recorder.Add(address, size);
        }
    }

    public bool Overlaps(ulong address, ulong size)
    {
        if (Overflowed)
        {
            return true;
        }

        foreach (var (start, length) in _ranges)
        {
            if (address < start + length && start < address + size)
            {
                return true;
            }
        }

        return false;
    }

    private void Add(ulong address, ulong size)
    {
        if (Overflowed || size == 0)
        {
            return;
        }

        var end = size > ulong.MaxValue - address ? ulong.MaxValue : address + size;
        // Reads come in runs (a table, a code block): widen the last range when they touch.
        if (_ranges.Count != 0)
        {
            var (start, length) = _ranges[^1];
            var lastEnd = start + length;
            if (address <= lastEnd && start <= end)
            {
                var mergedStart = Math.Min(start, address);
                _ranges[^1] = (mergedStart, Math.Max(lastEnd, end) - mergedStart);
                return;
            }
        }

        if (_ranges.Count == Capacity)
        {
            Overflowed = true;
            return;
        }

        _ranges.Add((address, end - address));
    }
}
