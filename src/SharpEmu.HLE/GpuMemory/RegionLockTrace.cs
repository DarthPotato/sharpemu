// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.HLE.GpuMemory;

// SHARPEMU_TRACE_REGION_LOCK_US=<n>: region lock holds and waits longer than n microseconds are
// grouped by the call stack that held or waited, so the report names who keeps a 4 MiB region
// locked while other threads spin. Inside a fault handler nothing may allocate, so those are only
// counted.
public static class RegionLockTrace
{
    private const int StackFrames = 12;
    private const int ReportedSites = 12;

    public static readonly long ThresholdTicks = ParseThreshold(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_REGION_LOCK_US"));

    public static bool Enabled => ThresholdTicks > 0;

    private sealed class Site
    {
        public long Count;
        public long Ticks;
        public long MaximumTicks;
        public long ProtectionTicks;
    }

    private static readonly ConcurrentDictionary<string, Site> Sites = new();
    private static long _faultHolds;
    private static long _faultHoldTicks;
    private static long _faultHoldProtectionTicks;
    private static long _faultWaits;
    private static long _faultWaitTicks;

    private static long ParseThreshold(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds > 0
            ? microseconds * Stopwatch.Frequency / 1_000_000
            : 0;

    // protectionTicks: host protection calls made during the hold, to tell stalled protection apart from a
    // holder that was not running.
    internal static void Record(bool hold, long ticks, long protectionTicks = 0)
    {
        var significant = ticks >= ThresholdTicks;
        if (GpuMemoryAccessProfile.InFault)
        {
            if (!significant)
            {
                return;
            }

            if (hold)
            {
                Interlocked.Increment(ref _faultHolds);
                Interlocked.Add(ref _faultHoldTicks, ticks);
                Interlocked.Add(ref _faultHoldProtectionTicks, protectionTicks);
            }
            else
            {
                Interlocked.Increment(ref _faultWaits);
                Interlocked.Add(ref _faultWaitTicks, ticks);
            }

            return;
        }

        // A thread that still holds other region locks would hold them through the stack capture:
        // its records wait until it releases the last one.
        if (GpuMemoryAccessProfile.RegionLocksHeld != 0)
        {
            if (significant)
            {
                (_deferred ??= new()).Add((hold, ticks, protectionTicks));
            }

            return;
        }

        if (!significant && _deferred is not { Count: > 0 })
        {
            return;
        }

        var stack = Thread.CurrentThread.Name + " | " + DescribeStack();
        if (_deferred is { Count: > 0 } deferred)
        {
            foreach (var (deferredHold, deferredTicks, deferredProtection) in deferred)
            {
                Add(deferredHold, deferredTicks, deferredProtection, stack);
            }

            deferred.Clear();
        }

        if (significant)
        {
            Add(hold, ticks, protectionTicks, stack);
        }
    }

    [ThreadStatic] private static List<(bool Hold, long Ticks, long Protection)>? _deferred;

    private static void Add(bool hold, long ticks, long protectionTicks, string stack)
    {
        var key = (hold ? "hold " : "wait ") + stack;
        var site = Sites.GetOrAdd(key, static _ => new Site());
        Interlocked.Increment(ref site.Count);
        Interlocked.Add(ref site.Ticks, ticks);
        Interlocked.Add(ref site.ProtectionTicks, protectionTicks);
        long maximum;
        while (ticks > (maximum = Volatile.Read(ref site.MaximumTicks)) &&
               Interlocked.CompareExchange(ref site.MaximumTicks, ticks, maximum) != maximum)
        {
        }
    }

    private static string DescribeStack()
    {
        var frames = new StackTrace(3, false).GetFrames();
        var names = new List<string>(StackFrames);
        foreach (var frame in frames)
        {
            if (names.Count == StackFrames)
            {
                break;
            }

            var method = frame.GetMethod();
            if (method is null)
            {
                continue;
            }

            names.Add($"{method.DeclaringType?.Name}.{method.Name}");
        }

        return string.Join(" < ", names);
    }

    public static void WriteReport()
    {
        if (!Enabled)
        {
            return;
        }

        static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[PERF][REGION_LOCK_LONG] fault_holds={Volatile.Read(ref _faultHolds)} fault_hold_ms={Milliseconds(Volatile.Read(ref _faultHoldTicks)):F1} fault_hold_protection_ms={Milliseconds(Volatile.Read(ref _faultHoldProtectionTicks)):F1} fault_waits={Volatile.Read(ref _faultWaits)} fault_wait_ms={Milliseconds(Volatile.Read(ref _faultWaitTicks)):F1}"));
        foreach (var (key, site) in Sites.ToArray().OrderByDescending(pair => Volatile.Read(ref pair.Value.Ticks)).Take(ReportedSites))
        {
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[PERF][REGION_LOCK_LONG] n={Volatile.Read(ref site.Count)} ms={Milliseconds(Volatile.Read(ref site.Ticks)):F1} max_ms={Milliseconds(Volatile.Read(ref site.MaximumTicks)):F1} protection_ms={Milliseconds(Volatile.Read(ref site.ProtectionTicks)):F1} {key}"));
        }
    }
}
