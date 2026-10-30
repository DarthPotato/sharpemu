// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

// Observation windows, not ownership of asynchronous compute work: a completed VideoOut flip
// swaps the counters. In-flight scopes are split at the boundary, including nested waits.
internal sealed class FrameTimelineAccumulator
{
    internal enum Metric { GuestWait, Command, Space, Sync, CommandIdle, CommandBlocked, Render, Prefetch, GpuWait, Idle, OtherWait, Count }
    internal enum Event { GuestKick, Admission, FlipRequest, CommandItem, CommandEnd, RenderItem, RenderEnd, GpuSubmit, Present, HostPresent, Count }
    internal sealed record Frame(long Start, long End, long[] Ticks, long[] First, long[] Last,
        long[] Counts, long Draws, long Dispatches, ulong LastTick, object? Timeline);

    private readonly long[] _ticks = new long[(int)Metric.Count];
    private readonly long[] _activeSince = new long[(int)Metric.Count];
    private readonly int[] _active = new int[(int)Metric.Count];
    private readonly long[] _first = new long[(int)Event.Count];
    private readonly long[] _last = new long[(int)Event.Count];
    private readonly long[] _counts = new long[(int)Event.Count];
    private long _start, _draws, _dispatches;
    private ulong _lastTick;
    private object? _timeline;

    internal FrameTimelineAccumulator(long start) => _start = start;
    private void Charge(int metric, long now)
    {
        _ticks[metric] += (now - _activeSince[metric]) * _active[metric];
        _activeSince[metric] = now;
    }
    internal void Change(Metric metric, int delta, long now)
    {
        var index = (int)metric;
        Charge(index, now);
        _active[index] += delta;
    }
    internal void Note(Event kind, long now, ulong tick = 0, object? timeline = null)
    {
        var index = (int)kind;
        if (_counts[index]++ == 0) _first[index] = now;
        _last[index] = now;
        if (kind == Event.GpuSubmit) { _lastTick = tick; _timeline = timeline; }
    }
    internal void Draw(bool dispatch) { if (dispatch) _dispatches++; else _draws++; }
    internal Frame Boundary(long now)
    {
        for (var i = 0; i < _ticks.Length; i++) Charge(i, now);
        var frame = new Frame(_start, now, (long[])_ticks.Clone(), (long[])_first.Clone(),
            (long[])_last.Clone(), (long[])_counts.Clone(), _draws, _dispatches, _lastTick, _timeline);
        Array.Clear(_ticks); Array.Clear(_first); Array.Clear(_last); Array.Clear(_counts);
        _start = now; _draws = _dispatches = 0; _lastTick = 0; _timeline = null;
        return frame;
    }

    internal sealed class Average
    {
        internal long Frames, Elapsed, Draws, Dispatches;
        internal long CompletionSamples, CompletionOffsets;
        internal readonly long[] Ticks = new long[(int)Metric.Count];
        internal readonly long[] First = new long[(int)Event.Count], Last = new long[(int)Event.Count],
            Counts = new long[(int)Event.Count], Samples = new long[(int)Event.Count];
        internal void Add(Frame frame)
        {
            Frames++; Elapsed += frame.End - frame.Start;
            Draws += frame.Draws; Dispatches += frame.Dispatches;
            for (var i = 0; i < Ticks.Length; i++) Ticks[i] += frame.Ticks[i];
            for (var i = 0; i < Counts.Length; i++)
            {
                Counts[i] += frame.Counts[i];
                if (frame.Counts[i] == 0) continue;
                Samples[i]++; First[i] += frame.First[i] - frame.Start; Last[i] += frame.Last[i] - frame.Start;
            }
        }
        internal double Mean(long value) => Frames == 0 ? 0 : (double)value / Frames;
    }

    // First host observation of completion, retained as ranges rather than one entry per tick.
    // A bounded ring avoids allocation in the GPU polling/submission path.
    internal sealed class CompletionHistory
    {
        private readonly (ulong Tick, long Timestamp)[] _entries = new (ulong, long)[4096];
        private int _next, _count;
        internal ulong Completed { get; private set; }
        internal void Observe(ulong tick, long now)
        {
            if (tick <= Completed) return;
            Completed = tick;
            _entries[_next] = (tick, now);
            _next = (_next + 1) % _entries.Length;
            _count = Math.Min(_count + 1, _entries.Length);
        }
        internal long? Timestamp(ulong tick)
        {
            if (tick == 0 || tick > Completed) return null;
            var oldest = (_next - _count + _entries.Length) % _entries.Length;
            // Once history wraps, the first retained range's lower bound is unknown.
            if (_count == _entries.Length && tick <= _entries[oldest].Tick) return null;
            for (var i = 0; i < _count; i++)
            {
                var entry = _entries[(oldest + i) % _entries.Length];
                if (entry.Tick >= tick) return entry.Timestamp;
            }
            return null;
        }
    }
}

internal static class FrameTimeline
{
    [ThreadStatic] private static bool _renderThread;
    [ThreadStatic] private static FrameTimelineAccumulator.Metric? _wait;
    internal readonly struct ThreadScope : IDisposable
    {
        private readonly bool _active, _previous;
        internal ThreadScope(bool active) { _active = active; _previous = _renderThread; if (active) _renderThread = true; }
        public void Dispose() { if (_active) _renderThread = _previous; }
    }
    internal static ThreadScope OnRenderThread() => Enabled ? new(true) : default;
    internal readonly struct PhaseScope : IDisposable
    {
        private readonly bool _active;
        private readonly FrameTimelineAccumulator.Metric? _previous;
        internal PhaseScope(FrameTimelineAccumulator.Metric metric)
        {
            _active = true; _previous = _wait;
            Switch(metric);
        }
        private static void Switch(FrameTimelineAccumulator.Metric? next)
        {
            lock (Storage.Gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_wait is { } old) Storage.Accumulator.Change(old, -1, now);
                if (next is { } metric) Storage.Accumulator.Change(metric, 1, now);
                _wait = next;
            }
        }
        public void Dispose() { if (_active) Switch(_previous); }
    }
    internal static PhaseScope MeasurePhase(RenderPhaseProfile.Phase phase)
    {
        if (!Enabled || !_renderThread) return default;
        FrameTimelineAccumulator.Metric? metric = phase switch
        {
            RenderPhaseProfile.Phase.GpuCompletionWait or RenderPhaseProfile.Phase.FrameSlotWait => FrameTimelineAccumulator.Metric.GpuWait,
            RenderPhaseProfile.Phase.ProgramPrefetchWait or RenderPhaseProfile.Phase.ProgramPrefetchOrderWait or
            RenderPhaseProfile.Phase.ProgramPrefetchDrainWait or RenderPhaseProfile.Phase.ProgramPrefetchPauseWait => FrameTimelineAccumulator.Metric.Prefetch,
            RenderPhaseProfile.Phase.GraphicsResolveLockWait or RenderPhaseProfile.Phase.ComputeResolveLockWait or
            RenderPhaseProfile.Phase.ProgramSharedGateWait or RenderPhaseProfile.Phase.PipelineSharedGateWait or
            RenderPhaseProfile.Phase.ComputePipelineWait => FrameTimelineAccumulator.Metric.OtherWait,
            _ => null,
        };
        return metric is { } value ? new(value) : default;
    }
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_FRAME_TIMELINE") == "1";
    // Lazy storage keeps disabled mode free of bookkeeping initialization.
    private static class Storage
    {
        internal static readonly object Gate = new();
        internal static readonly FrameTimelineAccumulator Accumulator = new(Stopwatch.GetTimestamp());
        internal static readonly Queue<(FrameTimelineAccumulator.Frame Frame, ulong Flip)> Pending = new();
        internal static FrameTimelineAccumulator.Average Average = new();
        internal static readonly Dictionary<object, FrameTimelineAccumulator.CompletionHistory> Completions = new();
    }
    internal readonly struct Scope : IDisposable
    {
        private readonly FrameTimelineAccumulator.Metric _metric;
        private readonly bool _active;
        private readonly FrameTimelineAccumulator.Event? _end;
        internal Scope(FrameTimelineAccumulator.Metric metric, FrameTimelineAccumulator.Event? end = null)
        {
            _metric = metric; _active = true; _end = end;
            lock (Storage.Gate) Storage.Accumulator.Change(metric, 1, Stopwatch.GetTimestamp());
        }
        public void Dispose()
        {
            if (!_active) return;
            lock (Storage.Gate) Storage.Accumulator.Change(_metric, -1, Stopwatch.GetTimestamp());
            if (_end is { } end) Note(end);
        }
    }
    internal static Scope Measure(FrameTimelineAccumulator.Metric metric, FrameTimelineAccumulator.Event? end = null) => Enabled ? new(metric, end) : default;
    internal readonly struct ItemScope : IDisposable
    {
        private readonly bool _active;
        internal ItemScope(bool active) { _active = active; Note(FrameTimelineAccumulator.Event.RenderItem); }
        public void Dispose() { if (_active) Note(FrameTimelineAccumulator.Event.RenderEnd); }
    }
    internal static ItemScope RenderItem() => Enabled ? new(true) : default;
    internal static void Note(FrameTimelineAccumulator.Event kind, ulong tick = 0, object? timeline = null)
    {
        if (!Enabled) return;
        lock (Storage.Gate) Storage.Accumulator.Note(kind, Stopwatch.GetTimestamp(), tick, timeline);
    }
    internal static void Draw(bool dispatch)
    {
        if (!Enabled) return;
        lock (Storage.Gate) Storage.Accumulator.Draw(dispatch);
    }
    internal static void Complete(object timeline, ulong tick)
    {
        if (!Enabled) return;
        lock (Storage.Gate)
        {
            if (!Storage.Completions.TryGetValue(timeline, out var history))
                Storage.Completions[timeline] = history = new();
            history.Observe(tick, Stopwatch.GetTimestamp());
            Drain();
        }
    }
    internal static void Flip(ulong request)
    {
        if (!Enabled) return;
        lock (Storage.Gate)
        {
            Storage.Pending.Enqueue((Storage.Accumulator.Boundary(Stopwatch.GetTimestamp()), request));
            Drain();
        }
    }
    private static string Ms(double ticks) => (ticks * 1000 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
    private static void Drain()
    {
        while (Storage.Pending.TryPeek(out var pending))
        {
            var f = pending.Frame;
            var completion = f.Timeline is { } timeline && Storage.Completions.TryGetValue(timeline, out var observed) ? observed : null;
            var completedAt = completion?.Timestamp(f.LastTick);
            // Bound diagnostic retention on a lost/stalled device. Unknown completion stays explicit.
            if (f.LastTick > (completion?.Completed ?? 0) && Storage.Pending.Count < 256) return;
            Storage.Pending.Dequeue();
            var fields = new System.Text.StringBuilder($"[PERF][FRAME_TIMELINE] flip={pending.Flip} start_tick={f.Start} end_tick={f.End} span_ms={Ms(f.End - f.Start)}");
            for (var i = 0; i < f.Counts.Length; i++)
            {
                var name = ((FrameTimelineAccumulator.Event)i).ToString();
                fields.Append($" {name}_first_ms={(f.Counts[i] == 0 ? "na" : Ms(f.First[i] - f.Start))} {name}_last_ms={(f.Counts[i] == 0 ? "na" : Ms(f.Last[i] - f.Start))} {name}_n={f.Counts[i]}");
            }
            for (var i = 0; i < f.Ticks.Length; i++) fields.Append($" {(FrameTimelineAccumulator.Metric)i}_ms={Ms(f.Ticks[i])}");
            fields.Append($" render_busy_ms={Ms(f.Ticks[(int)FrameTimelineAccumulator.Metric.Render] - f.Ticks[(int)FrameTimelineAccumulator.Metric.Prefetch] - f.Ticks[(int)FrameTimelineAccumulator.Metric.GpuWait] - f.Ticks[(int)FrameTimelineAccumulator.Metric.OtherWait])}");
            fields.Append($" command_busy_ms={Ms(f.Ticks[(int)FrameTimelineAccumulator.Metric.Command] - f.Ticks[(int)FrameTimelineAccumulator.Metric.Space] - f.Ticks[(int)FrameTimelineAccumulator.Metric.Sync])} render_other_ms={Ms(f.End - f.Start - f.Ticks[(int)FrameTimelineAccumulator.Metric.Render] - f.Ticks[(int)FrameTimelineAccumulator.Metric.Idle])}");
            fields.Append($" draws={f.Draws} dispatches={f.Dispatches} gpu_last_tick={f.LastTick} gpu_complete_ms={(completedAt is { } timestamp ? Ms(timestamp - f.Start) : "na")}");
            Console.Error.WriteLine(fields);
            var average = Storage.Average;
            average.Add(f);
            if (completedAt is { } completionTimestamp)
            {
                average.CompletionSamples++; average.CompletionOffsets += completionTimestamp - f.Start;
            }
            if (average.Elapsed < Stopwatch.Frequency * 5) continue;
            var summary = new System.Text.StringBuilder($"[PERF][FRAME_TIMELINE] summary=1 frames={average.Frames} span_ms={Ms(average.Mean(average.Elapsed))}");
            for (var i = 0; i < average.Ticks.Length; i++) summary.Append($" {(FrameTimelineAccumulator.Metric)i}_ms={Ms(average.Mean(average.Ticks[i]))}");
            for (var i = 0; i < average.Counts.Length; i++)
            {
                var name = ((FrameTimelineAccumulator.Event)i).ToString();
                summary.Append($" {name}_n={average.Mean(average.Counts[i]).ToString("F2", CultureInfo.InvariantCulture)} {name}_first_ms={(average.Samples[i] == 0 ? "na" : Ms((double)average.First[i] / average.Samples[i]))} {name}_last_ms={(average.Samples[i] == 0 ? "na" : Ms((double)average.Last[i] / average.Samples[i]))}");
            }
            summary.Append($" render_busy_ms={Ms(average.Mean(average.Ticks[(int)FrameTimelineAccumulator.Metric.Render] - average.Ticks[(int)FrameTimelineAccumulator.Metric.Prefetch] - average.Ticks[(int)FrameTimelineAccumulator.Metric.GpuWait] - average.Ticks[(int)FrameTimelineAccumulator.Metric.OtherWait]))} gpu_complete_ms={(average.CompletionSamples == 0 ? "na" : Ms((double)average.CompletionOffsets / average.CompletionSamples))} gpu_complete_samples={average.CompletionSamples}");
            summary.Append($" command_busy_ms={Ms(average.Mean(average.Ticks[(int)FrameTimelineAccumulator.Metric.Command] - average.Ticks[(int)FrameTimelineAccumulator.Metric.Space] - average.Ticks[(int)FrameTimelineAccumulator.Metric.Sync]))} render_other_ms={Ms(average.Mean(average.Elapsed - average.Ticks[(int)FrameTimelineAccumulator.Metric.Render] - average.Ticks[(int)FrameTimelineAccumulator.Metric.Idle]))}");
            summary.Append($" draws={average.Mean(average.Draws).ToString("F2", CultureInfo.InvariantCulture)} dispatches={average.Mean(average.Dispatches).ToString("F2", CultureInfo.InvariantCulture)}");
            Console.Error.WriteLine(summary);
            Storage.Average = new();
        }
    }
}
