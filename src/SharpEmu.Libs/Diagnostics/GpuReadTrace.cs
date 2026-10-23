// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Diagnostics;

internal static class GpuReadTrace
{
    private const int MaxReports = 64;

    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GPU_READS") == "1";

    [ThreadStatic]
    public static ulong CurrentShader;

    [ThreadStatic]
    public static string? CurrentStage;

    private static readonly HashSet<(ulong Shader, ulong Page, bool Table)> _reported = new();
    private static readonly Dictionary<ulong, (long Reads, long Changes, uint Last, uint First, ulong Shader)> _values = new();
    private static long _nextValueReport = System.Diagnostics.Stopwatch.GetTimestamp();
    // Which marking last made each 4 KB page GPU-dirty: a written binding (its range) or a
    // device-address write report (page granular).
    private static readonly Dictionary<ulong, (bool Device, ulong Address, ulong Size)> _marks = new();
    private static readonly Dictionary<ulong, (bool Device, ulong Address, ulong Size)> _origins = new();

    public static void NoteMark(ulong address, ulong size, bool device)
    {
        lock (_values)
        {
            for (var page = address & ~0xFFFUL; page < address + size; page += 0x1000)
            {
                _marks[page] = (device, address, size);
            }
        }
    }

    // Per 16 KB page: downloads, and which 256-byte granules a download ever changed.
    private static readonly Dictionary<ulong, (long Downloads, ulong Changed, long ChangedDownloads)> _pages = new();
    private static long _nextPageReport = System.Diagnostics.Stopwatch.GetTimestamp();

    public static void NoteDownload(ulong address, ReadOnlySpan<byte> previous, ReadOnlySpan<byte> downloaded)
    {
        lock (_pages)
        {
            for (var offset = 0; offset < downloaded.Length;)
            {
                var at = address + (ulong)offset;
                var page = at & ~0x3FFFUL;
                var length = (int)Math.Min((ulong)(downloaded.Length - offset), page + 0x4000 - at);
                var changed = 0UL;
                for (var index = 0; index < length; index++)
                {
                    if (previous[offset + index] != downloaded[offset + index])
                    {
                        changed |= 1UL << (int)(((at + (ulong)index) & 0x3FFF) >> 8);
                    }
                }

                var entry = _pages.TryGetValue(page, out var found) ? found : default;
                _pages[page] = (entry.Downloads + 1, entry.Changed | changed, entry.ChangedDownloads + (changed != 0 ? 1 : 0));
                offset += length;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _nextPageReport)
            {
                return;
            }

            _nextPageReport = now + System.Diagnostics.Stopwatch.Frequency * 10;
            HashSet<ulong> synced;
            lock (_values)
            {
                synced = _values.OrderByDescending(pair => pair.Value.Reads).Take(24).Select(pair => pair.Key & ~0x3FFFUL).ToHashSet();
            }

            foreach (var (key, value) in _pages.OrderByDescending(pair => pair.Value.Downloads).Take(8)
                         .Concat(_pages.Where(pair => synced.Contains(pair.Key))).DistinctBy(pair => pair.Key))
            {
                Console.Error.WriteLine(
                    $"[GPU][DOWNLOAD_GRANULES] page=0x{key:X16} downloads={value.Downloads} changed_downloads={value.ChangedDownloads} changed_256B_mask=0x{value.Changed:X16}");
            }
        }
    }

    // Per written 16 KB page: the scans that listed it, how many reported which 512-byte granules,
    // and how many had no mask, came after an overflowing scan, or overflowed themselves.
    private sealed class PageMasks
    {
        public long Scans;
        public long ZeroMasks;
        public long Incomplete;
        public long Overflow;
        public readonly long[] Granules = new long[32];
    }

    private static readonly Dictionary<ulong, PageMasks> _writeMasks = new();

    public static void NoteWriteMask(ulong page, uint mask, bool complete, bool overflow)
    {
        lock (_values)
        {
            if (!_writeMasks.TryGetValue(page, out var entry))
            {
                _writeMasks[page] = entry = new PageMasks();
            }

            entry.Scans++;
            entry.ZeroMasks += mask == 0 ? 1 : 0;
            entry.Incomplete += complete ? 0 : 1;
            entry.Overflow += overflow ? 1 : 0;
            for (var bit = 0; bit < 32; bit++)
            {
                if ((mask & (1u << bit)) != 0)
                {
                    entry.Granules[bit]++;
                }
            }
        }
    }

    // How often the scans of the address's page reported its 512-byte granule, and the most any
    // granule of its 4 KB quarter was reported (a lower bound for 4 KB tracking).
    private static string WriteMaskReport(ulong address)
    {
        if (!_writeMasks.TryGetValue(address & ~0x3FFFUL, out var entry))
        {
            return "page_scans=0";
        }

        var granule = (int)((address & 0x3FFF) >> 9);
        var quarter = granule & ~7;
        var quarterMax = 0L;
        for (var index = quarter; index < quarter + 8; index++)
        {
            quarterMax = Math.Max(quarterMax, entry.Granules[index]);
        }

        return $"page_scans={entry.Scans} granule_written={entry.Granules[granule]} quarter_written_max={quarterMax} " +
            $"zero_masks={entry.ZeroMasks} incomplete={entry.Incomplete} overflow={entry.Overflow}";
    }

    private static string Origin(ulong address) =>
        !_origins.TryGetValue(address, out var mark) ? "unknown"
            : mark.Device ? $"device-page:0x{mark.Address:X}+0x{mark.Size:X}"
            : $"binding:0x{mark.Address:X}+0x{mark.Size:X}";

    public static void RecordValue(ulong address, uint word)
    {
        lock (_values)
        {
            _values[address] = _values.TryGetValue(address, out var entry)
                ? (entry.Reads + 1, entry.Changes + (entry.Last != word ? 1 : 0), word, entry.First, entry.Shader)
                : (1, 0, word, word, CurrentShader);
            if (_marks.TryGetValue(address & ~0xFFFUL, out var mark))
            {
                _origins[address] = mark;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _nextValueReport)
            {
                return;
            }

            _nextValueReport = now + System.Diagnostics.Stopwatch.Frequency * 5;
            foreach (var (key, value) in _values.OrderByDescending(pair => pair.Value.Reads).Take(16))
            {
                Console.Error.WriteLine(
                    $"[GPU][SYNC_READ_VALUE] address=0x{key:X16} reads={value.Reads} changes={value.Changes} first=0x{value.First:X8} last=0x{value.Last:X8} shader=0x{value.Shader:X16} origin={Origin(key)} {WriteMaskReport(key)}");
            }
        }
    }

    public static void Record(ulong address, bool table)
    {
        var key = (CurrentShader, address & ~0xFFFUL, table);
        lock (_reported)
        {
            if (_reported.Count >= MaxReports || !_reported.Add(key))
            {
                return;
            }
        }

        Console.Error.WriteLine(
            $"[GPU][SYNC_READ] shader=0x{CurrentShader:X16} stage={CurrentStage} address=0x{address:X16} table={table}");
    }
}
