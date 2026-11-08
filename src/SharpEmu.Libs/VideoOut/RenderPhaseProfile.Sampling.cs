// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.Libs.VideoOut;

// SHARPEMU_PROFILE_SAMPLE=1 (ignored under SHARPEMU_PROFILE_RENDER): the render and command threads only publish
// the phase they are in, and a sampler thread counts it every millisecond. SHARPEMU_PROFILE_RENDER's scopes take two
// timestamps and per-phase accounting each (about 0.3 us; ~200k scopes per Ghost of Yotei frame make a 143 ms frame
// 200 ms), which inflates the frequent phases it is meant to rank. A published phase is one store, so the shares
// match an unprofiled run. Reports [PERF][SAMPLED_MS] per thread every report interval: ms per presented frame.
internal static partial class RenderPhaseProfile
{
    internal static readonly bool SampleEnabled =
        !Enabled && Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SAMPLE") == "1";

    internal enum SampledThread
    {
        Render,
        Command,
        Count,
    }

    internal sealed class SampleSlot
    {
        public volatile int Phase;
        public readonly long[] Counts = new long[(int)RenderPhaseProfile.Phase.Count];
        public long Total;

        public Phase Enter(Phase phase)
        {
            var previous = (Phase)Phase;
            Phase = (int)phase;
            return previous;
        }
    }

    private static readonly SampleSlot?[] _sampleSlots = new SampleSlot?[(int)SampledThread.Count];
    [ThreadStatic] private static SampleSlot? _sampleSlot;
    private static int _samplerStarted;
    private static long _sampleWindowStart;
    private static long _sampleFrames;

    internal static void AttachSampledThread(SampledThread thread)
    {
        if (!SampleEnabled)
        {
            return;
        }

        var slot = new SampleSlot();
        _sampleSlot = slot;
        Volatile.Write(ref _sampleSlots[(int)thread], slot);
        if (Interlocked.Exchange(ref _samplerStarted, 1) == 0)
        {
            _sampleWindowStart = Stopwatch.GetTimestamp();
            new Thread(SampleLoop) { IsBackground = true, Name = "SharpEmu phase sampler", Priority = ThreadPriority.Highest }.Start();
        }
    }

    // A scope that only publishes its phase; disposal restores the phase it replaced.
    private static Scope EnterSampled(SampleSlot slot, Phase phase, FrameTimeline.PhaseScope timeline) =>
        new(slot.Enter(phase), timeline, active: false, sampled: slot);

    private static void SampleLoop()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1);
        }

        while (true)
        {
            Thread.Sleep(1);
            foreach (var slot in _sampleSlots)
            {
                if (slot is null)
                {
                    continue;
                }

                Interlocked.Increment(ref slot.Counts[slot.Phase]);
                Interlocked.Increment(ref slot.Total);
            }
        }
    }

    // Render thread, once per presented frame.
    private static void RecordSampledFrame()
    {
        _sampleFrames++;
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _sampleWindowStart;
        if (elapsed < _reportSeconds * Stopwatch.Frequency || _sampleFrames == 0)
        {
            return;
        }

        var windowMilliseconds = elapsed * 1000.0 / Stopwatch.Frequency;
        var frames = _sampleFrames;
        _sampleWindowStart = now;
        _sampleFrames = 0;
        for (var index = 0; index < (int)SampledThread.Count; index++)
        {
            if (Volatile.Read(ref _sampleSlots[index]) is not { } slot)
            {
                continue;
            }

            var total = Interlocked.Exchange(ref slot.Total, 0);
            var counts = new long[slot.Counts.Length];
            for (var phase = 0; phase < counts.Length; phase++)
            {
                counts[phase] = Interlocked.Exchange(ref slot.Counts[phase], 0);
            }

            if (total == 0)
            {
                continue;
            }

            var text = new StringBuilder(FormattableString.Invariant(
                $"[PERF][SAMPLED_MS] thread={(SampledThread)index} window_s={windowMilliseconds / 1000.0:F1} frames={frames} samples={total} frame_ms={windowMilliseconds / frames:F1}"));
            foreach (var phase in Enumerable.Range(0, counts.Length).Where(phase => counts[phase] != 0).OrderByDescending(phase => counts[phase]))
            {
                var share = counts[phase] / (double)total;
                text.Append(FormattableString.Invariant($" {(Phase)phase}={share * windowMilliseconds / frames:F1}"));
            }

            Console.Error.WriteLine(text.ToString());
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
}
