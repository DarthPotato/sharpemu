// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// Splits command-thread reads that run on the render thread into time queued before the render
// thread starts them, time executing them there (including any GPU wait) and time to wake the
// producer afterwards. Reported every five seconds with SHARPEMU_PROFILE_RENDER.
internal static class RoundTripSplit
{
    private static long _count, _queued, _executed, _wake, _maxQueued, _maxExecuted, _next;

    public static void Note(long queued, long executed, long wake)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _queued, queued);
        Interlocked.Add(ref _executed, executed);
        Interlocked.Add(ref _wake, wake);
        Max(ref _maxQueued, queued);
        Max(ref _maxExecuted, executed);
        var now = Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref _next);
        if (next == 0)
        {
            Interlocked.CompareExchange(ref _next, now + 5 * Stopwatch.Frequency, 0);
            return;
        }

        if (now < next || Interlocked.CompareExchange(ref _next, now + 5 * Stopwatch.Frequency, next) != next)
        {
            return;
        }

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[PERF][ROUND_TRIP_SPLIT] reads={Interlocked.Exchange(ref _count, 0)} queued_ms={Ms(Interlocked.Exchange(ref _queued, 0)):F1} executed_ms={Ms(Interlocked.Exchange(ref _executed, 0)):F1} wake_ms={Ms(Interlocked.Exchange(ref _wake, 0)):F1} max_queued_ms={Ms(Interlocked.Exchange(ref _maxQueued, 0)):F1} max_executed_ms={Ms(Interlocked.Exchange(ref _maxExecuted, 0)):F1}"));
    }

    private static void Max(ref long field, long value)
    {
        long current;
        while ((current = Volatile.Read(ref field)) < value && Interlocked.CompareExchange(ref field, value, current) != current)
        {
        }
    }
}
