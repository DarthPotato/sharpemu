// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.Gpu.Images;

// Independent of the sampled image diagnostics: only this range and the process-wide line cap apply.
internal static class ImageLifecycleTrace
{
    internal readonly record struct Range(ulong First, ulong Last)
    {
        public bool Overlaps(ulong address, ulong size) =>
            size != 0 && address <= Last && (address >= First || First - address < size);
    }

    private static readonly Range? Filter = Parse(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_IMAGE_LIFECYCLE"));
    internal static readonly bool Enabled = Filter.HasValue;
    private static readonly object Gate = new();
    private const int MaximumLines = 20000;
    private static int _lines;

    internal static bool Matches(ulong address, ulong size) => Filter is { } range && range.Overlaps(address, size);

    // Ranges have an exclusive end, like SHARPEMU_TRACE_IMAGE_RANGE; a single address selects
    // any image containing that byte, including an image beginning before it.
    internal static Range? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('-');
        if (parts.Length > 2 || !TryHex(parts[0], out var first)) return null;
        if (parts.Length == 1) return new Range(first, first);
        return TryHex(parts[1], out var end) && end > first ? new Range(first, end - 1) : null;
    }

    private static bool TryHex(string text, out ulong value)
    {
        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        return ulong.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    internal static bool HasCapacity => Volatile.Read(ref _lines) < MaximumLines;

    internal static void Write(string message)
    {
        lock (Gate)
        {
            if (_lines >= MaximumLines) return;
            var sequence = ++_lines;
            Console.Error.WriteLine($"[GPU][IMAGE_LIFE] sequence={sequence} {message}" +
                (sequence == MaximumLines ? " trace_limit=20000 further_events_suppressed=true" : ""));
        }
    }
}
