// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Diagnostics;

// With the render profile: how long a value the command processor stores into guest memory (a
// label, a WRITE_DATA) takes to reach guest memory, where a guest CPU thread polling it can see it.
// An ordinary store waits for every render item queued ahead of it; a deferred end-of-pipe label
// also waits for the GPU and for earlier publications; a submission waits in its queue before its
// first slice (Admit). [PERF][LABEL_LATENCY] each report, per path
// (split by graphics/compute queue): count, mean, p50/p90/p99 bucket bounds, max, items ahead.
internal static class LabelLatencyProfile
{
    public enum Path
    {
        StoreGraphics,
        StoreCompute,
        DeferredGraphics,
        DeferredCompute,
        // A guest submission from its acceptance to the start of its first interpreter slice.
        AdmitGraphics,
        AdmitCompute,
    }

    // Bucket upper bounds in microseconds; the last bucket is everything above.
    private static readonly double[] Bounds = [50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000, 50000, 100000];

    private const int PathCount = 6;

    public static bool Enabled => VideoOut.RenderPhaseProfile.Enabled;

    private static readonly object Gate = new();
    private static readonly long[][] Buckets = Enumerable.Range(0, PathCount).Select(_ => new long[Bounds.Length + 1]).ToArray();
    private static readonly long[] Counts = new long[PathCount];
    private static readonly double[] TotalMicroseconds = new double[PathCount];
    private static readonly double[] MaxMicroseconds = new double[PathCount];
    // Render items queued ahead of each store when it was posted.
    private static readonly long[] Ahead = new long[PathCount];

    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void Record(Path path, long started, int ahead = 0)
    {
        if (started == 0)
        {
            return;
        }

        var microseconds = (Stopwatch.GetTimestamp() - started) * 1_000_000.0 / Stopwatch.Frequency;
        var bucket = 0;
        while (bucket < Bounds.Length && microseconds > Bounds[bucket])
        {
            bucket++;
        }

        lock (Gate)
        {
            var index = (int)path;
            Buckets[index][bucket]++;
            Counts[index]++;
            TotalMicroseconds[index] += microseconds;
            MaxMicroseconds[index] = Math.Max(MaxMicroseconds[index], microseconds);
            Ahead[index] += ahead;
        }
    }

    public static void Report()
    {
        if (!Enabled)
        {
            return;
        }

        string? line = null;
        lock (Gate)
        {
            var parts = new List<string>();
            for (var index = 0; index < Counts.Length; index++)
            {
                if (Counts[index] == 0)
                {
                    continue;
                }

                parts.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{(Path)index}: n={Counts[index]} mean_us={TotalMicroseconds[index] / Counts[index]:F0} " +
                    $"p50<={Percentile(index, 0.5)} p90<={Percentile(index, 0.9)} p99<={Percentile(index, 0.99)} " +
                    $"max_us={MaxMicroseconds[index]:F0} total_ms={TotalMicroseconds[index] / 1000:F0} items_ahead={(double)Ahead[index] / Counts[index]:F1}"));
                Array.Clear(Buckets[index]);
                Counts[index] = 0;
                TotalMicroseconds[index] = 0;
                MaxMicroseconds[index] = 0;
                Ahead[index] = 0;
            }

            if (parts.Count != 0)
            {
                line = "[PERF][LABEL_LATENCY] " + string.Join(" | ", parts);
            }
        }

        if (line is not null)
        {
            Console.Error.WriteLine(line);
        }
    }

    private static string Percentile(int index, double fraction)
    {
        var target = (long)Math.Ceiling(Counts[index] * fraction);
        long seen = 0;
        for (var bucket = 0; bucket < Buckets[index].Length; bucket++)
        {
            seen += Buckets[index][bucket];
            if (seen >= target)
            {
                return bucket < Bounds.Length ? Bounds[bucket].ToString(CultureInfo.InvariantCulture) : "inf";
            }
        }

        return "inf";
    }
}
