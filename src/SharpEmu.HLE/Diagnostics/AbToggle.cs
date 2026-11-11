// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.Diagnostics;

// SHARPEMU_AB_TOGGLE_S=N (off unless set): a switch that flips every N seconds within one run, so a change can be
// measured against itself in the same scene. Separate runs of one save settle into scenes whose cost differs by up
// to 10% (Ghost of Yotei: 3.4k-4.3k draws per frame), which hides 1-3% effects. Code under test branches on On; the
// videoout fps line reports the state (ab=0/1) so its windows can be split.
public static class AbToggle
{
    private static readonly long PeriodTicks =
        double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_AB_TOGGLE_S"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? (long)(seconds * Stopwatch.Frequency)
            : 0;

    public static readonly bool Enabled = PeriodTicks > 0;
    private static readonly long Start = Stopwatch.GetTimestamp();

    // The second half of each period: the variant under test.
    public static bool On => Enabled && ((Stopwatch.GetTimestamp() - Start) / PeriodTicks & 1) == 1;

    // Index of the current period (0, 1, 2, ...); the odd ones are On. One run can cycle through several variants
    // by giving each odd period its own; the videoout fps line reports it (abn=<index>).
    public static long Period => Enabled ? (Stopwatch.GetTimestamp() - Start) / PeriodTicks : 0;
}
