// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

internal readonly record struct BufferRetirementPlan(
    ulong LatestEligibleTick, int MaximumBufferCount, bool DownloadDirtyBuffers);

// Collection ticks measure cache activity, not elapsed time.
internal sealed class BufferRetirementPolicy
{
    private readonly Func<long> _clock;
    private readonly Queue<(ulong Tick, long Time)> _ages = new();
    private ulong _timedCutoff;

    public BufferRetirementPolicy(Func<long>? clock = null) => _clock = clock ?? Stopwatch.GetTimestamp;

    private static readonly long GraceTicks = long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_BUFFER_GRACE_MS"), out var ms) && ms >= 0 && ms <= 60000 ? ms * Stopwatch.Frequency / 1000 : 2 * Stopwatch.Frequency;

    private ulong ElapsedCutoff(ulong tick)
    {
        if (GraceTicks == 0) return 0;
        var now = _clock();
        while (_ages.TryPeek(out var age) && now - age.Time >= GraceTicks)
        {
            _timedCutoff = age.Tick;
            _ages.Dequeue();
        }
        // Older entries are already eligible under both logical age policies.
        while (_ages.Count >= 161) _ages.Dequeue();
        _ages.Enqueue((tick, now));
        return _timedCutoff;
    }

    private ulong _collectionThreshold = 1024UL * 1024 * 1024;
    private ulong _criticalThreshold = 2048UL * 1024 * 1024;

    public ulong CurrentTick { get; private set; }

    public void SetThresholds(ulong collectionThreshold, ulong criticalThreshold)
    {
        _collectionThreshold = collectionThreshold;
        _criticalThreshold = criticalThreshold;
    }

    public bool TryBeginCollection(ulong usedMemory, out BufferRetirementPlan plan)
    {
        var tick = CurrentTick++;
        var elapsedCutoff = ElapsedCutoff(tick);
        plan = default;
        if (usedMemory < _collectionThreshold)
        {
            return false;
        }

        var downloadDirtyBuffers = usedMemory >= _criticalThreshold;
        var minimumAge = downloadDirtyBuffers ? 80UL : 160UL;
        plan = new BufferRetirementPlan(
            Math.Max(tick - Math.Min(minimumAge, tick), elapsedCutoff), downloadDirtyBuffers ? 64 : 32, downloadDirtyBuffers);
        return true;
    }
}
