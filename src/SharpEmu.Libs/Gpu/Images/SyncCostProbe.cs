// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Images;

// TEMPORARY measurement counters for CPU-GPU sync paths; printed every 10 seconds to stderr.
internal static class SyncCostProbe
{
    private const int Count = 9;

    public const int StencilWb = 0;
    public const int StencilWbBytes = 1;
    public const int AwaitWaits = 2;
    public const int AwaitTicks = 3;
    public const int HtileMarked = 4;
    public const int HtileResolves = 5;
    public const int HtileReads = 6;
    public const int HtileReadTicks = 7;
    public const int HtileResolveTicks = 8;

    private static readonly long[] Totals = new long[Count];
    private static readonly long[] Last = new long[Count];
    private static long _nextReport = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
    private static long _lastReportStamp = Stopwatch.GetTimestamp();

    public static long Begin() => Stopwatch.GetTimestamp();

    public static void Add(int slot, long value) => Interlocked.Add(ref Totals[slot], value);

    public static void AddElapsed(int slot, long startStamp) =>
        Interlocked.Add(ref Totals[slot], Stopwatch.GetTimestamp() - startStamp);

    public static void MaybeReport()
    {
        var now = Stopwatch.GetTimestamp();
        var due = Interlocked.Read(ref _nextReport);
        if (now < due || Interlocked.CompareExchange(ref _nextReport, now + Stopwatch.Frequency * 10, due) != due)
        {
            return;
        }

        var window = new long[Count];
        var total = new long[Count];
        lock (Last)
        {
            for (var i = 0; i < Count; i++)
            {
                total[i] = Interlocked.Read(ref Totals[i]);
                window[i] = total[i] - Last[i];
                Last[i] = total[i];
            }
        }

        var seconds = (now - _lastReportStamp) / (double)Stopwatch.Frequency;
        _lastReportStamp = now;
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var head = FormattableString.Invariant(
            $"[SYNCCOST] window={seconds:F1}s stencilWB={window[StencilWb]} stencilWBBytes={window[StencilWbBytes]} awaitWaits={window[AwaitWaits]} awaitMs={Ms(window[AwaitTicks]):F1} htileMarked={window[HtileMarked]} htileResolves={window[HtileResolves]} htileReads={window[HtileReads]} htileReadMs={Ms(window[HtileReadTicks]):F1} htileResolveMs={Ms(window[HtileResolveTicks]):F1} ");
        var tail = FormattableString.Invariant(
            $"tot_stencilWB={total[StencilWb]} tot_stencilWBBytes={total[StencilWbBytes]} tot_awaitWaits={total[AwaitWaits]} tot_awaitMs={Ms(total[AwaitTicks]):F1} tot_htileMarked={total[HtileMarked]} tot_htileResolves={total[HtileResolves]} tot_htileReads={total[HtileReads]} tot_htileReadMs={Ms(total[HtileReadTicks]):F1} tot_htileResolveMs={Ms(total[HtileResolveTicks]):F1}");
        Console.Error.WriteLine(head + tail);
    }
}
