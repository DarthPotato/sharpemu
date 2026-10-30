// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

internal static class ReadbackSourceProfile
{
    [ThreadStatic] private static string? _source;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Main, long Transfer, ulong Bytes)> Counts = new();
    internal static string Source => _source ?? "Other";

    internal readonly struct Scope : IDisposable
    {
        private readonly string? _previous;
        internal Scope(string source) { _previous = _source; _source = source; }
        public void Dispose() => _source = _previous;
    }

    internal static Scope Begin(string source) => new(source);
    internal static void Count(bool transfer = false, ulong bytes = 0, string? source = null, bool submission = true)
    {
        if (!VideoOut.RenderPhaseProfile.Enabled) return;
        lock (Gate)
        {
            source ??= Source;
            Counts.TryGetValue(source, out var old);
            Counts[source] = (old.Main + (!transfer && submission ? 1 : 0),
                old.Transfer + (transfer && submission ? 1 : 0), old.Bytes + bytes);
        }
    }

    internal static void Report()
    {
        if (!VideoOut.RenderPhaseProfile.Enabled) return;
        lock (Gate)
        {
            Console.Error.WriteLine("[PERF][READBACK_SOURCE] " + string.Join(" ", Counts.OrderBy(p => p.Key).Select(p =>
                $"{p.Key}:main={p.Value.Main},transfer={p.Value.Transfer},bytes={p.Value.Bytes}")));
            Counts.Clear();
        }
    }
}
