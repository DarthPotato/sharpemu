// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class AsyncPipelineProfile
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<WeakReference<PipelineCompileScheduler>> _schedulers = new();
    internal static void Register(PipelineCompileScheduler scheduler)
    {
        if (RenderPhaseProfile.Enabled) _schedulers.Add(new(scheduler));
    }
    private static readonly long[] _requests = new long[4];
    private static long _dedup, _rejected, _queuePeak, _bytePeak, _active, _activePeak;
    private static readonly object _activeGate = new();
    private static long _activeChanged = RenderPhaseProfile.Enabled ? Stopwatch.GetTimestamp() : 0, _windowStarted = _activeChanged, _activeIntegral;
    private static long _ready, _pending, _wait, _waitMax, _lead, _uses, _mismatches, _failures, _rectangles, _saveDelay;
    private static readonly long[] _driver = new long[3];
    internal static void Request(bool compute, bool speculative)
    {
        if (RenderPhaseProfile.Enabled) Interlocked.Increment(ref _requests[(compute ? 2 : 0) + (speculative ? 0 : 1)]);
    }
    internal static void Dedup() { if (RenderPhaseProfile.Enabled) Interlocked.Increment(ref _dedup); }
    internal static void Reject() { if (RenderPhaseProfile.Enabled) Interlocked.Increment(ref _rejected); }
    internal static void Failure() { if (RenderPhaseProfile.Enabled) Interlocked.Increment(ref _failures); }
    internal static void Mismatch() { if (RenderPhaseProfile.Enabled) Interlocked.Increment(ref _mismatches); }
    internal static void Rectangle() { if (RenderPhaseProfile.Enabled) Interlocked.Increment(ref _rectangles); }
    private static void Max(ref long value, long next)
    {
        long prior;
        do { prior = Volatile.Read(ref value); if (prior >= next) return; }
        while (Interlocked.CompareExchange(ref value, next, prior) != prior);
    }
    internal static void Queue(int count, long bytes)
    {
        if (!RenderPhaseProfile.Enabled) return;
        Max(ref _queuePeak, count); Max(ref _bytePeak, bytes);
    }
    internal static void Start()
    {
        if (!RenderPhaseProfile.Enabled) return;
        lock (_activeGate)
        {
            IntegrateActive();
            _active++;
            _activePeak = Math.Max(_activePeak, _active);
        }
    }
    internal static void Finish()
    {
        if (!RenderPhaseProfile.Enabled) return;
        lock (_activeGate) { IntegrateActive(); _active--; }
    }
    private static void IntegrateActive()
    {
        var now = Stopwatch.GetTimestamp();
        _activeIntegral += _active * (now - _activeChanged);
        _activeChanged = now;
    }
    internal static void Ready() { if (RenderPhaseProfile.Enabled) Interlocked.Increment(ref _ready); }
    internal static void Use(bool pending, long started)
    {
        if (pending)
        {
            Interlocked.Increment(ref _pending);
            var ticks = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _wait, ticks); Max(ref _waitMax, ticks);
        }
        else Ready();
    }
    internal static void Lead(long completed)
    {
        Interlocked.Add(ref _lead, Math.Max(0, Stopwatch.GetTimestamp() - completed));
        Interlocked.Increment(ref _uses);
    }
    internal static void SaveDelay(long started)
    {
        if (started != 0) Interlocked.Add(ref _saveDelay, Stopwatch.GetTimestamp() - started);
    }
    internal static void Driver(long started, bool renderThread, bool commandThread = false)
    {
        if (started != 0) Interlocked.Add(ref _driver[PipelineCompileScheduler.IsWorker ? 2 : renderThread ? 0 : commandThread ? 1 : 2], Stopwatch.GetTimestamp() - started);
    }
    internal static string TakeReport()
    {
        static long Take(ref long value) => Interlocked.Exchange(ref value, 0);
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        double activeAverage;
        long activePeak;
        lock (_activeGate)
        {
            IntegrateActive();
            activeAverage = (double)_activeIntegral / Math.Max(1, _activeChanged - _windowStarted);
            activePeak = _activePeak;
            _activePeak = _active;
            _activeIntegral = 0;
            _windowStarted = _activeChanged;
        }
        var uses = Take(ref _uses);
        var neverConsumed = _schedulers.Sum(reference => reference.TryGetTarget(out var scheduler) ? scheduler.NeverConsumed : 0);
        return FormattableString.Invariant($"[PERF][ASYNC_PIPELINES] graphics_speculative={Take(ref _requests[0])} graphics_demand={Take(ref _requests[1])} compute_speculative={Take(ref _requests[2])} compute_demand={Take(ref _requests[3])} dedup={Take(ref _dedup)} queue_peak={Take(ref _queuePeak)} retained_bytes_peak={Take(ref _bytePeak)} rejected_full={Take(ref _rejected)} active_worker_avg={activeAverage:F2} active_worker_max={activePeak} ready_at_use={Take(ref _ready)} pending_at_use={Take(ref _pending)} wait_at_use_ms={Ms(Take(ref _wait)):F3} wait_at_use_max_ms={Ms(Take(ref _waitMax)):F3} compile_lead_ms_avg={Ms(Take(ref _lead)) / Math.Max(1, uses):F3} key_mismatches={Take(ref _mismatches)} failures={Take(ref _failures)} speculative_never_consumed={neverConsumed} rectangle_variant_compiles={Take(ref _rectangles)} cache_save_delay_ms={Ms(Take(ref _saveDelay)):F3} driver_render_ms={Ms(Take(ref _driver[0])):F3} driver_command_ms={Ms(Take(ref _driver[1])):F3} driver_worker_ms={Ms(Take(ref _driver[2])):F3}");
    }
}
