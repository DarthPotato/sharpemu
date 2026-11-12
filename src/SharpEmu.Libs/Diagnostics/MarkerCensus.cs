// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// SHARPEMU_MARKER_CENSUS=<seconds> (off unless set): draws and dispatches per frame under each command-stream marker
// path (the names the game pushes with sceAgcDcbPushMarker), reported every <seconds> as [PERF][MARKER_CENSUS] lines.
// It answers which of the game's render passes the draws of a frame belong to. A row's viewport= is the viewport of
// the last draw under that path, which shows the resolution the pass renders at.
// SHARPEMU_AB_SKIP_MARKER=<text,text> (measurement only): while the in-run A/B toggle is on, draws and dispatches
// under a marker path containing one of the texts are dropped, which gives the frame-time share of that pass.
// "*" matches every path; a "draw:" or "dispatch:" prefix limits a text to that kind of item. Groups separated by
// "|" take turns, one per on period (the fps line's abn=<period>, group = (period / 2) % groups), so one run measures
// several passes. A text "groups:<X>x<Y>x<Z>" matches direct dispatches with those group counts under any path,
// which singles out one compute pass that has no marker of its own. Nothing is dropped in the first SHARPEMU_AB_SKIP_START_S seconds (default 120): dropping
// dispatches while the game loads ended a run.
public static class MarkerCensus
{
    private const int MaximumEntries = 4096;
    private const int ReportedPaths = 70;

    private static readonly long IntervalTicks =
        double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MARKER_CENSUS"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? (long)(seconds * Stopwatch.Frequency)
            : 0;

    private const int MaximumGroups = 64;
    private const string SizePrefix = "groups:";

    private static readonly string[][] SkipGroups =
        (Environment.GetEnvironmentVariable("SHARPEMU_AB_SKIP_MARKER") ?? string.Empty)
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(MaximumGroups)
        .Select(static group => group.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();

    private static readonly long SkipStart = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency *
        (double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_AB_SKIP_START_S"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var start) && start >= 0 ? start : 120));

    // One bit per skip group, as in Entry.
    private static readonly (ulong Groups, uint X, uint Y, uint Z)[] SkipSizes = ReadSkipSizes();

    public static readonly bool Enabled = IntervalTicks > 0 || SkipGroups.Length > 0;

    public sealed class Entry(string path)
    {
        public readonly string Path = path;
        // One bit per skip group.
        public readonly ulong SkipDraws = MatchingGroups(path, false);
        public readonly ulong SkipDispatches = MatchingGroups(path, true);
        public long Draws;
        public long Dispatches;
        public long Skipped;
        public int ViewportWidth;
        public int ViewportHeight;
    }

    public static readonly Entry Root = new("(no marker)");
    private static readonly Entry Overflow = new("(overflow)");
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly object ReportGate = new();
    private static long _lastReport = Stopwatch.GetTimestamp();
    private static long _flips;

    public static Entry Child(Entry parent, string name)
    {
        var path = ReferenceEquals(parent, Root) ? name : parent.Path + "/" + name;
        if (Entries.TryGetValue(path, out var entry))
        {
            return entry;
        }

        return Entries.Count >= MaximumEntries ? Overflow : Entries.GetOrAdd(path, static key => new Entry(key));
    }

    // True when the item must be dropped (skip text matched and the in-run toggle is on).
    public static bool Note(Entry entry, bool dispatch)
    {
        var groups = dispatch ? entry.SkipDispatches : entry.SkipDraws;
        if (groups != 0 && SkipsNow(groups))
        {
            Interlocked.Increment(ref entry.Skipped);
            return true;
        }

        if (dispatch)
        {
            Interlocked.Increment(ref entry.Dispatches);
        }
        else
        {
            Interlocked.Increment(ref entry.Draws);
        }

        return false;
    }

    public static bool NoteDispatch(Entry entry, uint groupsX, uint groupsY, uint groupsZ)
    {
        foreach (var size in SkipSizes)
        {
            if (size.X == groupsX && size.Y == groupsY && size.Z == groupsZ && SkipsNow(size.Groups))
            {
                Interlocked.Increment(ref entry.Skipped);
                return true;
            }
        }

        return Note(entry, true);
    }

    public static bool NoteDraw(Entry entry, SharpEmu.Libs.Gpu.GpuCommands.Registers.ScreenViewportRegisters screen)
    {
        entry.ViewportWidth = (int)(2 * MathF.Abs(screen.Primary.XScale) + 0.5f);
        entry.ViewportHeight = (int)(2 * MathF.Abs(screen.Primary.YScale) + 0.5f);
        return Note(entry, false);
    }

    public static void NoteFlip()
    {
        Interlocked.Increment(ref _flips);
        if (IntervalTicks == 0 || Stopwatch.GetTimestamp() - Volatile.Read(ref _lastReport) < IntervalTicks)
        {
            return;
        }

        lock (ReportGate)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - _lastReport < IntervalTicks)
            {
                return;
            }

            _lastReport = now;
            var frames = Math.Max(Interlocked.Exchange(ref _flips, 0), 1);
            var rows = new List<(string Path, long Draws, long Dispatches, long Skipped, int Width, int Height)>();
            AddRow(rows, Root);
            AddRow(rows, Overflow);
            foreach (var entry in Entries.Values)
            {
                AddRow(rows, entry);
            }

            rows.Sort(static (left, right) => (right.Draws + right.Dispatches + right.Skipped).CompareTo(left.Draws + left.Dispatches + left.Skipped));
            long draws = 0, dispatches = 0, skipped = 0;
            foreach (var row in rows)
            {
                draws += row.Draws;
                dispatches += row.Dispatches;
                skipped += row.Skipped;
            }

            var readFaults = Interlocked.Exchange(ref SharpEmu.HLE.GpuMemory.GuestGpuMemory.ReadFaults, 0);
            var readFaultTicks = Interlocked.Exchange(ref SharpEmu.HLE.GpuMemory.GuestGpuMemory.ReadFaultTicks, 0);
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture,
                $"[PERF][MARKER_CENSUS] frames={frames} draws_per_frame={(double)draws / frames:F1} dispatches_per_frame={(double)dispatches / frames:F1} skipped_per_frame={(double)skipped / frames:F1} read_faults_per_frame={(double)readFaults / frames:F1} read_fault_ms_per_frame={readFaultTicks * 1000.0 / Stopwatch.Frequency / frames:F1} paths={rows.Count}");
            for (var index = 0; index < rows.Count && index < ReportedPaths; index++)
            {
                var row = rows[index];
                text.Append(CultureInfo.InvariantCulture,
                    $"\n[PERF][MARKER_CENSUS]   draws={(double)row.Draws / frames:F1} dispatches={(double)row.Dispatches / frames:F1} skipped={(double)row.Skipped / frames:F1} path=\"{row.Path}\"");
                if (row.Draws > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $" viewport={row.Width}x{row.Height}");
                }
            }

            Console.Error.WriteLine(text.ToString());
        }
    }

    private static void AddRow(List<(string Path, long Draws, long Dispatches, long Skipped, int Width, int Height)> rows, Entry entry)
    {
        var draws = Interlocked.Exchange(ref entry.Draws, 0);
        var dispatches = Interlocked.Exchange(ref entry.Dispatches, 0);
        var skipped = Interlocked.Exchange(ref entry.Skipped, 0);
        if (draws + dispatches + skipped > 0)
        {
            rows.Add((entry.Path, draws, dispatches, skipped, entry.ViewportWidth, entry.ViewportHeight));
        }
    }

    private static bool SkipsNow(ulong groups)
    {
        var period = SharpEmu.HLE.Diagnostics.AbToggle.Period;
        return (period & 1) != 0 &&
            (groups >> (int)((period >> 1) % SkipGroups.Length) & 1) != 0 &&
            Stopwatch.GetTimestamp() >= SkipStart;
    }

    private static (ulong Groups, uint X, uint Y, uint Z)[] ReadSkipSizes()
    {
        var sizes = new List<(ulong Groups, uint X, uint Y, uint Z)>();
        for (var index = 0; index < SkipGroups.Length; index++)
        {
            foreach (var text in SkipGroups[index])
            {
                if (!text.StartsWith(SizePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = text[SizePrefix.Length..].Split('x', 'X');
                if (parts.Length == 3 &&
                    uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var x) &&
                    uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var y) &&
                    uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var z))
                {
                    sizes.Add((1UL << index, x, y, z));
                }
                else
                {
                    Console.Error.WriteLine($"[PERF][MARKER_CENSUS] SHARPEMU_AB_SKIP_MARKER: \"{text}\" is not groups:<X>x<Y>x<Z>; ignored.");
                }
            }
        }

        return [.. sizes];
    }

    private static ulong MatchingGroups(string path, bool dispatch)
    {
        ulong groups = 0;
        for (var index = 0; index < SkipGroups.Length; index++)
        {
            if (MatchesSkip(SkipGroups[index], path, dispatch))
            {
                groups |= 1UL << index;
            }
        }

        return groups;
    }

    private static bool MatchesSkip(string[] texts, string path, bool dispatch)
    {
        foreach (var entry in texts)
        {
            var text = entry;
            if (text.StartsWith(SizePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (text.StartsWith("draw:", StringComparison.OrdinalIgnoreCase))
            {
                if (dispatch)
                {
                    continue;
                }

                text = text[5..];
            }
            else if (text.StartsWith("dispatch:", StringComparison.OrdinalIgnoreCase))
            {
                if (!dispatch)
                {
                    continue;
                }

                text = text[9..];
            }

            if (text == "*" || path.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
