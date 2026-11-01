// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

internal static class ProducerWaitProfile
{
    [ThreadStatic] private static string? _purpose;
    internal static readonly bool TraceReads = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PRODUCER_READS") == "1";
    private static int _tracedReads;

    // SHARPEMU_TRACE_PRODUCER_READS_AFTER_S=N starts tracing N seconds after launch, so gameplay reads
    // are not crowded out by startup; reads with no pending writer and no GPU-dirty bytes are skipped.
    private static readonly long TraceAfterTicks =
        long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PRODUCER_READS_AFTER_S"), out var after) && after > 0
            ? Stopwatch.GetTimestamp() + after * Stopwatch.Frequency : 0;

    // Bounded per process: stack capture is deliberately excluded from timing A/B runs.
    public static void TraceRead(ulong address, int size, string source, long pendingSequence, bool pendingRange, bool gpuDirty, bool table)
    {
        if (!TraceReads || (pendingSequence == 0 && !pendingRange && !gpuDirty) ||
            (TraceAfterTicks != 0 && Stopwatch.GetTimestamp() < TraceAfterTicks) ||
            Interlocked.Increment(ref _tracedReads) > 512) return;
        Console.Error.WriteLine($"[GPU][PRODUCER_READ] purpose={_purpose ?? "other"} address=0x{address:X} size={size} source={source} pending_sequence={pendingSequence} pending_range={pendingRange} gpu_dirty={gpuDirty} table={table} stack=" +
            string.Join("<-", new StackTrace(1, false).GetFrames().Take(24).Select(f => f.GetMethod()).Where(m => m is not null).Select(m => m!.DeclaringType?.Name + "." + m.Name)));
    }
    private sealed class Counters { public long Count, Ticks, Max; }
    private static readonly ConcurrentDictionary<(string Purpose, string Kind, string Caller), Counters> Waits = new();
    private static readonly ConcurrentDictionary<string, Counters> Paths = new();
    public readonly struct Scope : IDisposable
    {
        private readonly string? _previous;
        public Scope(string purpose) { _previous = _purpose; _purpose = purpose; }
        public void Dispose() => _purpose = _previous;
    }
    public static Scope Begin(string purpose) => new(CommandThreadTally.Enabled || TraceReads ? purpose : _purpose ?? "other");
    public static Scope Packet(uint opcode, uint header) => Begin(CommandThreadTally.Enabled || TraceReads ? PacketHandlerTally.Purpose(opcode, header) : "other");
    public static void Note(string kind, long ticks, string caller = "")
    {
        if (!CommandThreadTally.Enabled) return;
        var key = (_purpose ?? "other", kind, caller);
        var c = Waits.GetOrAdd(key, static _ => new());
        Interlocked.Increment(ref c.Count);
        Interlocked.Add(ref c.Ticks, ticks);
        long old;
        do { old = Volatile.Read(ref c.Max); if (old >= ticks) break; }
        while (Interlocked.CompareExchange(ref c.Max, ticks, old) != old);
    }
    public static void Path(string path)
    {
        if (CommandThreadTally.Enabled) Interlocked.Increment(ref Paths.GetOrAdd(path, static _ => new()).Count);
    }
    public static string TakeReport()
    {
        var parts = new List<string>();
        foreach (var (key, c) in Waits.OrderBy(p => p.Key))
        {
            var count = Interlocked.Exchange(ref c.Count, 0);
            var ticks = Interlocked.Exchange(ref c.Ticks, 0);
            var max = Interlocked.Exchange(ref c.Max, 0);
            if (count != 0) parts.Add(FormattableString.Invariant($"{key.Purpose}/{key.Kind}/{key.Caller}={count}/{ticks * 1000.0 / Stopwatch.Frequency:F2}ms/max{max * 1000.0 / Stopwatch.Frequency:F2}ms"));
        }
        foreach (var (key, c) in Paths.OrderBy(p => p.Key))
        {
            var count = Interlocked.Exchange(ref c.Count, 0);
            if (count != 0) parts.Add($"path.{key}={count}");
        }
        return "[PERF][PRODUCER_WAITS] " + string.Join(" ", parts);
    }
}
