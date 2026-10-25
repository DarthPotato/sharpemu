// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Diagnostics;

// With the render profile: the time the render thread waited for the GPU because a shader's resource
// planning read guest memory the GPU had written (TryReadGuestWord -> TrySynchronizeCpuRead), by
// shader. Names the shaders whose descriptor tables the GPU produces, the candidates for fetching
// descriptors on the GPU instead. [PERF][SHADER_SYNC] each report, top shaders by wait.
internal static class ShaderSyncProfile
{
    private const int ReportedShaders = 10;

    public static bool Enabled => VideoOut.RenderPhaseProfile.Enabled;

    private static readonly Dictionary<ulong, (long Syncs, long Ticks, string? Stage)> Shaders = new();

    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    // Render thread only.
    public static void Record(long started)
    {
        if (started == 0)
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        var shader = GpuReadTrace.CurrentShader;
        Shaders.TryGetValue(shader, out var entry);
        Shaders[shader] = (entry.Syncs + 1, entry.Ticks + ticks, GpuReadTrace.CurrentStage);
    }

    public static void Report()
    {
        if (!Enabled || Shaders.Count == 0)
        {
            return;
        }

        var total = Shaders.Values.Sum(entry => entry.Ticks);
        var line = string.Join(' ', Shaders.OrderByDescending(pair => pair.Value.Ticks).Take(ReportedShaders)
            .Select(pair => string.Create(CultureInfo.InvariantCulture,
                $"0x{pair.Key:X16}/{pair.Value.Stage}={pair.Value.Ticks * 1000.0 / Stopwatch.Frequency:F1}ms/n{pair.Value.Syncs}")));
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[PERF][SHADER_SYNC] total_ms={total * 1000.0 / Stopwatch.Frequency:F1} shaders={Shaders.Count} {line}"));
        Shaders.Clear();
    }
}
