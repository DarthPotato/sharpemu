// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public enum SubmitReason
{
    Other, CompletedSlice, SuspendedSlice, DrawCap, IdleKick, RenderLoopDrain,
    RenderLoopDeadline, FrameBoundary, CpuFlip, FlipPacketOrWait, FlipSnapshot,
    Presentation, Readback, WaitCurrentTick, Finish, FlushAndWait,
}

// Count issuance here so direct scheduler consumers are included too.
internal static class SubmitReasonProfile
{
    private static readonly long[] Counts = new long[Enum.GetValues<SubmitReason>().Length];

    internal static void Count(SubmitReason reason)
    {
        if (VideoOut.RenderPhaseProfile.Enabled) Interlocked.Increment(ref Counts[(int)reason]);
    }

    internal static void Report()
    {
        if (!VideoOut.RenderPhaseProfile.Enabled) return;
        Console.Error.WriteLine("[PERF][SUBMIT_REASON] " + string.Join(" ",
            Enum.GetValues<SubmitReason>().Select(reason =>
                $"{reason}={Interlocked.Exchange(ref Counts[(int)reason], 0)}")));
    }
}
