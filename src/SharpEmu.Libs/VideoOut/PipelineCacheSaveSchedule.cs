// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// Uses monotonic milliseconds supplied by the caller so the policy needs neither Vulkan nor a timer.
internal sealed class PipelineCacheSaveSchedule(long thresholdMilliseconds)
{
    internal const long DebounceMilliseconds = 25_000;
    // A steady stream of expensive compiles would otherwise postpone the save forever.
    internal const long MaximumDelayMilliseconds = 60_000;
    internal const long MinimumSaveIntervalMilliseconds = 60_000;
    private readonly object _gate = new();
    private long? _firstExpensiveCompile;
    private long? _lastExpensiveCompile;
    private long _triggerMilliseconds;

    internal static long ParseThreshold(string? value) =>
        long.TryParse(value, out var milliseconds) && milliseconds >= 0 ? milliseconds : 2_000;

    internal void RecordCompile(long milliseconds, long now)
    {
        if (thresholdMilliseconds == 0 || milliseconds <= thresholdMilliseconds) return;
        lock (_gate)
        {
            _firstExpensiveCompile ??= now;
            _lastExpensiveCompile = now;
            _triggerMilliseconds = milliseconds;
        }
    }

    internal bool TryTakeSave(long now, long lastSave, out long triggerMilliseconds)
    {
        lock (_gate)
        {
            triggerMilliseconds = 0;
            if (!Due(now, lastSave)) return false;
            triggerMilliseconds = _triggerMilliseconds;
            _firstExpensiveCompile = null;
            _lastExpensiveCompile = null;
            return true;
        }
    }

    internal bool IsDue(long now, long lastSave)
    {
        lock (_gate)
            return Due(now, lastSave);
    }

    private bool Due(long now, long lastSave) =>
        _lastExpensiveCompile is { } last && _firstExpensiveCompile is { } first &&
        (now - last >= DebounceMilliseconds || now - first >= MaximumDelayMilliseconds) &&
        now - lastSave >= MinimumSaveIntervalMilliseconds;

    internal void Retry(long triggerMilliseconds, long now)
    {
        lock (_gate)
        {
            // Preserve any newer compile recorded while the save was running.
            if (_lastExpensiveCompile is not null) return;
            _firstExpensiveCompile = now;
            _lastExpensiveCompile = now;
            _triggerMilliseconds = triggerMilliseconds;
        }
    }
}
