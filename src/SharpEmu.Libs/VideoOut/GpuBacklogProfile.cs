// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// What a CPU wait for a main-queue tick waited behind (SHARPEMU_PROFILE_RENDER). Each
// submission records when it went out, how long its batch was open (time since the
// previous submission) and whether the GPU had finished everything before it. A wait for
// a tick submitted moments earlier into an idle GPU waited for work that could have
// started while the batch was still being recorded; a wait behind unfinished submissions
// waited for a GPU that was already busy.
internal static class GpuBacklogProfile
{
    private const int Slots = 8192;
    private const double FreshMilliseconds = 2;

    private static readonly object _gate = new();
    private static readonly long[] _submitted = new long[Slots];
    private static readonly long[] _open = new long[Slots];
    private static readonly bool[] _idle = new bool[Slots];
    private static readonly ulong[] _ticks = new ulong[Slots];
    private static long _lastSubmit;

    // [fresh, idle] buckets: n, wait ticks, open ticks (capped at the wait), outstanding ticks.
    private static readonly long[] _count = new long[4];
    private static readonly long[] _wait = new long[4];
    private static readonly long[] _hidden = new long[4];
    private static readonly long[] _outstanding = new long[4];
    private static readonly long[] _age = new long[4];
    private static long _unknown;
    private static long _unknownWait;

    public static bool Enabled => RenderPhaseProfile.Enabled;

    public static void NoteSubmit(ulong tick, ulong completedBefore)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            var slot = (int)(tick % Slots);
            _ticks[slot] = tick;
            _submitted[slot] = now;
            _open[slot] = _lastSubmit == 0 ? 0 : now - _lastSubmit;
            _idle[slot] = completedBefore + 1 >= tick;
            _lastSubmit = now;
        }
    }

    public readonly record struct Token(ulong Tick, ulong Completed, long Start);

    public static Token Begin(ulong tick, ulong completed) =>
        Enabled ? new Token(tick, completed, Stopwatch.GetTimestamp()) : default;

    public static void End(in Token token)
    {
        if (token.Start == 0)
        {
            return;
        }

        var waited = Stopwatch.GetTimestamp() - token.Start;
        lock (_gate)
        {
            var slot = (int)(token.Tick % Slots);
            if (_ticks[slot] != token.Tick)
            {
                _unknown++;
                _unknownWait += waited;
                return;
            }

            var age = token.Start - _submitted[slot];
            var fresh = age * 1000.0 / Stopwatch.Frequency < FreshMilliseconds;
            var bucket = (fresh ? 2 : 0) + (_idle[slot] ? 1 : 0);
            _count[bucket]++;
            _wait[bucket] += waited;
            _hidden[bucket] += Math.Min(waited, _open[slot]);
            _outstanding[bucket] += (long)(token.Tick - Math.Min(token.Tick, token.Completed));
            _age[bucket] += age;
        }
    }

    public static void Report()
    {
        string[] lines;
        lock (_gate)
        {
            var total = 0L;
            foreach (var count in _count)
            {
                total += count;
            }

            if (total == 0 && _unknown == 0)
            {
                return;
            }

            lines = new string[5];
            for (var bucket = 0; bucket < 4; bucket++)
            {
                var n = Math.Max(1, _count[bucket]);
                lines[bucket] = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[PERF][GPU_BACKLOG] fresh={(bucket & 2) != 0} gpu_idle_at_submit={(bucket & 1) != 0} n={_count[bucket]} " +
                    $"wait_ms={Ms(_wait[bucket]):F1} avg_wait_ms={Ms(_wait[bucket]) / n:F2} " +
                    $"open_capped_ms={Ms(_hidden[bucket]):F1} avg_outstanding={_outstanding[bucket] / (double)n:F2} " +
                    $"avg_age_ms={Ms(_age[bucket]) / n:F2}");
                _count[bucket] = _wait[bucket] = _hidden[bucket] = _outstanding[bucket] = _age[bucket] = 0;
            }

            lines[4] = FormattableString.Invariant($"[PERF][GPU_BACKLOG] untracked n={_unknown} wait_ms={Ms(_unknownWait):F1}");
            _unknown = _unknownWait = 0;
        }

        foreach (var line in lines)
        {
            Console.Error.WriteLine(line);
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
