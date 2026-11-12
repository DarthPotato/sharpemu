// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.Libs.Gpu.Scheduling;

// SHARPEMU_EAGER_FLIP=1|2 (off unless set): a flip's completion (flip event, flip interrupt, flip label) no longer
// waits for the GPU to finish the frame that was flipped. A game that starts its next frame on the flip event then
// overlaps that frame's CPU work with the GPU tail of the frame before it. The picture itself still waits for its
// own GPU work before it is presented.
//   1: the completion waits for the frame before it and for the GPU work of every guest interrupt queued earlier,
//      so the guest runs at most one frame ahead of the GPU. An earlier interrupt of the deferred path is published
//      by the queue owner, not by the priority worker, so the flip can still reach the guest just before it.
//   2: the completion waits for the frame before it only (it can overtake an earlier interrupt).
// Measured 2026-11-12 on Ghost of Yotei: the flip completes about 14 ms earlier and the frame rate does not move;
// the same GPU time is then waited for at the first GPU read of the next frame. Kept as an experiment switch.
// SHARPEMU_AB_EAGER_FLIP=1|2 applies the mode only while the in-run A/B toggle is on; 3 alternates the two modes
// over the on periods (abn 1, 5, 9, ... = mode 1; abn 3, 7, 11, ... = mode 2).
internal static class EagerFlip
{
    private static readonly int Always = Parse("SHARPEMU_EAGER_FLIP");
    private static readonly int Toggled = Parse("SHARPEMU_AB_EAGER_FLIP");

    private static long _lagTicks;
    private static long _lagCount;
    private static long _direct;
    private static long _deferred;
    private static long _tickGap;
    private static long _queuedTicks;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> QueuedAt = new();

    internal static int Mode => Toggled switch
    {
        0 => Always,
        3 => AbToggle.On ? ((AbToggle.Period >> 1) & 1) == 0 ? 1 : 2 : Always,
        _ => AbToggle.On ? Toggled : Always,
    };

    internal static void NoteFlip(bool deferred, ulong truthfulTick, ulong completionTick)
    {
        if (deferred) Interlocked.Increment(ref _deferred);
        else Interlocked.Increment(ref _direct);
        Interlocked.Add(ref _tickGap, (long)(truthfulTick - completionTick));
    }

    // The completion of this flip was queued behind its tick (the capture and its submission are done).
    internal static void NoteQueued(ulong requestId) => QueuedAt[requestId] = Stopwatch.GetTimestamp();

    // A cancelled flip never completes; drop its queue time.
    internal static void Forget(ulong requestId) => QueuedAt.TryRemove(requestId, out _);

    internal static void NoteCompletion(ulong requestId, long reservedAt)
    {
        if (reservedAt == 0) return;
        if (QueuedAt.TryRemove(requestId, out var queuedAt)) Interlocked.Add(ref _queuedTicks, queuedAt - reservedAt);
        Interlocked.Add(ref _lagTicks, Stopwatch.GetTimestamp() - reservedAt);
        Interlocked.Increment(ref _lagCount);
    }

    // For the videoout fps line: mean time from a flip's submission to its completion, how many flips took the
    // direct and the deferred end-of-pipe path, and how many submission ticks early their completions were queued.
    internal static string TakeReport()
    {
        var count = Interlocked.Exchange(ref _lagCount, 0);
        var ticks = Interlocked.Exchange(ref _lagTicks, 0);
        var direct = Interlocked.Exchange(ref _direct, 0);
        var deferred = Interlocked.Exchange(ref _deferred, 0);
        var gap = Interlocked.Exchange(ref _tickGap, 0);
        var queued = Interlocked.Exchange(ref _queuedTicks, 0);
        if (count == 0 && direct == 0 && deferred == 0) return "";
        var flips = direct + deferred;
        return FormattableString.Invariant(
            $" flip_lag_ms={(count == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / count):F1} flip_queue_ms={(count == 0 ? 0 : queued * 1000.0 / Stopwatch.Frequency / count):F1} flip_paths={direct}/{deferred} flip_early_ticks={(flips == 0 ? 0 : (double)gap / flips):F1}");
    }

    private static int Parse(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value is >= 1 and <= 3 ? value : 0;
}
