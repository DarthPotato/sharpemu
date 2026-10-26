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
    public bool Overflowed { get; internal set; }

    // The writeback history floor of the resolution the recorder belongs to (GPU side), so work
    // inside the resolution checks stores from its start.
    public long Floor { get; set; }

    // The guest view generation when the resolution began (GuestRangeCanonicalizer): views that change
    // during it may give its addresses other bytes without any store.
    public long ViewGeneration { get; set; }

    public static void Open(ConcurrentReadRecorder recorder) => _current = recorder;

    public static void Close() => _current = null;

    // The recorder open on this thread, if any (for a nested recording that hands its ranges on).
    public static ConcurrentReadRecorder? Current => _current;

    // Records into `nested` instead until the returned recorder (the previous one) is restored with
    // EndNested, which also adds what `nested` recorded to it.
    public static ConcurrentReadRecorder? BeginNested(ConcurrentReadRecorder nested)
    {
        var previous = _current;
        _current = nested;
        return previous;
    }

    public static void EndNested(ConcurrentReadRecorder nested, ConcurrentReadRecorder? previous)
    {
        _current = previous;
        if (previous is null)
        {
            return;
        }

        if (nested.Overflowed)
        {
            previous.Overflowed = true;
            return;
        }

        foreach (var (address, size) in nested._ranges)
        {
            previous.Add(address, size);
        }
    }

    // Called at the entry of every base-memory read, before any early return. The range is kept in
    // canonical pieces (GuestRangeCanonicalizer), as stores are, so views of one backing compare.
    public static void Note(ulong address, ulong size)
    {
        if (_current is not null)
        {
            GuestMemory.GuestRangeCanonicalizer.ForEach(address, size, AddToCurrent);
        }
    }

    private static readonly Action<ulong, ulong> AddToCurrent = static (address, size) => _current!.Add(address, size);

    // A guest range the resolution read some other way (the presenter's readers): in canonical pieces.
    public void AddCanonical(ulong address, ulong size) =>
        GuestMemory.GuestRangeCanonicalizer.ForEach(address, size, (pieceAddress, pieceSize) => Add(pieceAddress, pieceSize));

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
