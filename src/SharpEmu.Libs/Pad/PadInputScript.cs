// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Pad;

// Time windows are [start,end), in seconds since process start. All-or-nothing parsing
// prevents a typo from running only part of an unattended route.
internal sealed class PadInputScript
{
    internal sealed record Segment(double Start, double End, string Inputs, uint Buttons,
        int LeftX, int LeftY, int RightX, int RightY, int Axes);

    internal IReadOnlyList<Segment> Segments { get; }
    private readonly int[] _logged;
    private readonly long _processStart;

    private PadInputScript(Segment[] segments)
    {
        Segments = segments;
        _logged = new int[segments.Length];
        using var process = Process.GetCurrentProcess();
        _processStart = Stopwatch.GetTimestamp() - (long)((DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds * Stopwatch.Frequency);
    }

    internal static PadInputScript? FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_INPUT_SCRIPT");
        if (string.IsNullOrEmpty(raw)) return null;
        if (TryParse(raw, out var script, out var error)) return script;
        Console.Error.WriteLine($"[INPUT][SCRIPT][ERROR] SHARPEMU_INPUT_SCRIPT ignored in full: {error}");
        return null;
    }

    internal static bool TryParse(string raw, out PadInputScript? script, out string error)
    {
        script = null;
        error = "";
        var segments = new List<Segment>();
        foreach (var entry in raw.Split(';', StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', StringSplitOptions.TrimEntries);
            var range = parts[0].Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || range.Length is < 1 or > 2 ||
                !Seconds(range[0], out var start))
            {
                error = $"Invalid segment '{entry}'; expected start[-end]:input[+input].";
                return false;
            }
            var end = start + 0.150;
            if ((range.Length == 2 && !Seconds(range[1], out end)) || !double.IsFinite(end) || end <= start)
            {
                error = $"Invalid time range '{parts[0]}'; end must be finite and greater than start.";
                return false;
            }

            uint buttons = 0;
            int lx = 0, ly = 0, rx = 0, ry = 0, axes = 0;
            foreach (var token in parts[1].Split('+', StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                switch (token.ToLowerInvariant())
                {
                    case "cross": buttons |= OrbisPadButton.Cross; break;
                    case "circle": buttons |= OrbisPadButton.Circle; break;
                    case "square": buttons |= OrbisPadButton.Square; break;
                    case "triangle": buttons |= OrbisPadButton.Triangle; break;
                    case "l1": buttons |= OrbisPadButton.L1; break;
                    case "r1": buttons |= OrbisPadButton.R1; break;
                    case "l2": buttons |= OrbisPadButton.L2; break;
                    case "r2": buttons |= OrbisPadButton.R2; break;
                    case "options": buttons |= OrbisPadButton.Options; break;
                    case "up": buttons |= OrbisPadButton.Up; break;
                    case "down": buttons |= OrbisPadButton.Down; break;
                    case "left": buttons |= OrbisPadButton.Left; break;
                    case "right": buttons |= OrbisPadButton.Right; break;
                    case "touchpad": buttons |= OrbisPadButton.TouchPad; break;
                    case "ls_left": lx--; axes |= 1; break;
                    case "ls_right": lx++; axes |= 1; break;
                    case "ls_up": ly--; axes |= 2; break;
                    case "ls_down": ly++; axes |= 2; break;
                    case "rs_left": rx--; axes |= 4; break;
                    case "rs_right": rx++; axes |= 4; break;
                    case "rs_up": ry--; axes |= 8; break;
                    case "rs_down": ry++; axes |= 8; break;
                    default:
                        error = $"Unknown input '{token}' in '{entry}'.";
                        return false;
                }
            }
            segments.Add(new(start, end, parts[1], buttons, lx, ly, rx, ry, axes));
        }
        script = new(segments.ToArray());
        return true;
    }

    private static bool Seconds(string value, out double seconds) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) &&
        double.IsFinite(seconds) && seconds >= 0;

    internal PadState Merge(PadState state) => Merge(state,
        (Stopwatch.GetTimestamp() - _processStart) / (double)Stopwatch.Frequency, Console.Error.WriteLine);

    internal PadState Merge(PadState state, double elapsed, Action<string>? log = null)
    {
        uint buttons = 0;
        int lx = 0, ly = 0, rx = 0, ry = 0, axes = 0;
        for (var index = 0; index < Segments.Count; index++)
        {
            var segment = Segments[index];
            if (elapsed < segment.Start) continue;
            if (Interlocked.Exchange(ref _logged[index], 1) == 0)
                log?.Invoke(FormattableString.Invariant($"[INPUT][SCRIPT] segment={index + 1} start={segment.Start:F3} end={segment.End:F3} inputs={segment.Inputs} sampled_at={elapsed:F3} missed={elapsed >= segment.End}"));
            if (elapsed >= segment.End) continue;
            buttons |= segment.Buttons;
            lx += segment.LeftX;
            ly += segment.LeftY;
            rx += segment.RightX;
            ry += segment.RightY;
            axes |= segment.Axes;
        }

        static byte Axis(int direction) => direction < 0 ? (byte)0 : direction > 0 ? (byte)255 : (byte)128;
        return state with
        {
            Buttons = state.Buttons | buttons,
            LeftX = (axes & 1) != 0 ? Axis(lx) : state.LeftX,
            LeftY = (axes & 2) != 0 ? Axis(ly) : state.LeftY,
            RightX = (axes & 4) != 0 ? Axis(rx) : state.RightX,
            RightY = (axes & 8) != 0 ? Axis(ry) : state.RightY,
            L2 = (buttons & OrbisPadButton.L2) != 0 ? (byte)255 : state.L2,
            R2 = (buttons & OrbisPadButton.R2) != 0 ? (byte)255 : state.R2,
        };
    }
}
