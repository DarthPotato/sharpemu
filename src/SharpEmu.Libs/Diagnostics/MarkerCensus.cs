// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// SHARPEMU_MARKER_CENSUS=<seconds> (off unless set): draws and dispatches per frame under each command-stream marker
// path (the names the game pushes with sceAgcDcbPushMarker), reported every <seconds> as [PERF][MARKER_CENSUS] lines.
// It answers which of the game's render passes the draws of a frame belong to.
// SHARPEMU_AB_SKIP_MARKER=<text;text> (measurement only): while the in-run A/B toggle is on, draws and dispatches
// under a marker path containing one of the texts are dropped, which gives the frame-time share of that pass.
public static class MarkerCensus
{
    private const int MaximumEntries = 4096;
    private const int ReportedPaths = 70;

    private static readonly long IntervalTicks =
        double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MARKER_CENSUS"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? (long)(seconds * Stopwatch.Frequency)
            : 0;

    private static readonly string[] SkipTexts =
        (Environment.GetEnvironmentVariable("SHARPEMU_AB_SKIP_MARKER") ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static readonly bool Enabled = IntervalTicks > 0 || SkipTexts.Length > 0;

    public sealed class Entry(string path, bool skip)
    {
        public readonly string Path = path;
        public readonly bool Skip = skip;
        public long Draws;
        public long Dispatches;
        public long Skipped;
    }

    public static readonly Entry Root = new("(no marker)", MatchesSkip("(no marker)"));
    private static readonly Entry Overflow = new("(overflow)", false);
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

        return Entries.Count >= MaximumEntries ? Overflow : Entries.GetOrAdd(path, static key => new Entry(key, MatchesSkip(key)));
    }

    // True when the item must be dropped (skip text matched and the in-run toggle is on).
    public static bool Note(Entry entry, bool dispatch)
    {
        if (entry.Skip && SharpEmu.HLE.Diagnostics.AbToggle.On)
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
            var rows = new List<(string Path, long Draws, long Dispatches, long Skipped)>();
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

            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture,
                $"[PERF][MARKER_CENSUS] frames={frames} draws_per_frame={(double)draws / frames:F1} dispatches_per_frame={(double)dispatches / frames:F1} skipped_per_frame={(double)skipped / frames:F1} paths={rows.Count}");
            for (var index = 0; index < rows.Count && index < ReportedPaths; index++)
            {
                var row = rows[index];
                text.Append(CultureInfo.InvariantCulture,
                    $"\n[PERF][MARKER_CENSUS]   draws={(double)row.Draws / frames:F1} dispatches={(double)row.Dispatches / frames:F1} skipped={(double)row.Skipped / frames:F1} path=\"{row.Path}\"");
            }

            Console.Error.WriteLine(text.ToString());
        }
    }

    private static void AddRow(List<(string Path, long Draws, long Dispatches, long Skipped)> rows, Entry entry)
    {
        var draws = Interlocked.Exchange(ref entry.Draws, 0);
        var dispatches = Interlocked.Exchange(ref entry.Dispatches, 0);
        var skipped = Interlocked.Exchange(ref entry.Skipped, 0);
        if (draws + dispatches + skipped > 0)
        {
            rows.Add((entry.Path, draws, dispatches, skipped));
        }
    }

    private static bool MatchesSkip(string path)
    {
        foreach (var text in SkipTexts)
        {
            if (path.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
