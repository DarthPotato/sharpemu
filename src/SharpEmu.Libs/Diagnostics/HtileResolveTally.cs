// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Images;

// What CPU resolves of GPU-rewritten HTile find, every five seconds with SHARPEMU_PROFILE_RENDER:
// how many found a cleared slice, how many changed the tracked state, and per address.
internal static class HtileResolveTally
{
    private static long _resolves, _anyCleared, _changed, _next, _predicted;

    public static void NotePredicted() => Interlocked.Increment(ref _predicted);
    private static readonly ConcurrentDictionary<ulong, long[]> _byAddress = new();

    public static void Note(ulong address, uint layers, bool anyCleared, bool changed)
    {
        Interlocked.Increment(ref _resolves);
        if (anyCleared) Interlocked.Increment(ref _anyCleared);
        if (changed) Interlocked.Increment(ref _changed);
        var entry = _byAddress.GetOrAdd(address, _ => new long[4]);
        Interlocked.Increment(ref entry[0]);
        if (anyCleared) Interlocked.Increment(ref entry[1]);
        if (changed) Interlocked.Increment(ref entry[2]);
        entry[3] = layers;
        var now = Stopwatch.GetTimestamp();
        var due = Interlocked.Read(ref _next);
        if (due == 0)
        {
            Interlocked.CompareExchange(ref _next, now + 5 * Stopwatch.Frequency, 0);
            return;
        }

        if (now < due || Interlocked.CompareExchange(ref _next, now + 5 * Stopwatch.Frequency, due) != due)
        {
            return;
        }

        var top = _byAddress.OrderByDescending(pair => pair.Value[0]).Take(6)
            .Select(pair => $"0x{pair.Key:X}:n{pair.Value[0]}/cleared{pair.Value[1]}/changed{pair.Value[2]}/layers{pair.Value[3]}");
        Console.Error.WriteLine(
            $"[PERF][HTILE_RESOLVE] resolves={Interlocked.Exchange(ref _resolves, 0)} predicted={Interlocked.Exchange(ref _predicted, 0)} any_cleared={Interlocked.Exchange(ref _anyCleared, 0)} changed={Interlocked.Exchange(ref _changed, 0)} addresses={_byAddress.Count} top={string.Join(",", top)}");
        _byAddress.Clear();
    }
}
