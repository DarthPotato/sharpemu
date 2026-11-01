// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Libs.Gpu.Buffers;

public sealed unsafe partial class GuestBufferCache
{
    private Dictionary<GpuBuffer, string>? _allocationSources;
    private Dictionary<string, (long Count, ulong Requested, ulong Allocated, long Merged)>? _allocationProfile;

    private void NoteBufferCreation(GpuBuffer buffer, string caller, ulong requested, ulong allocated, int merged)
    {
        if (!GpuDeviceInfo.VramTrace && !VideoOut.RenderPhaseProfile.Enabled) return;
        var sizeClass = allocated <= 65536 ? "le64K" : allocated <= 1048576 ? "le1M" : "large";
        var key = caller + "/" + sizeClass;
        (_allocationSources ??= new())[buffer] = key;
        var profile = _allocationProfile ??= new();
        profile.TryGetValue(key, out var value);
        profile[key] = (value.Count + 1, value.Requested + requested, value.Allocated + allocated, value.Merged + merged);
    }

    private string DescribeBufferSizes()
    {
        ulong smallBytes = 0, largeBytes = 0;
        var small = 0;
        for (var index = 0; index < _registry.RegisteredCount; index++)
        {
            var buffer = _registry.GetBuffer(_registry.GetRegisteredIdentifier(index));
            if (buffer.Size <= 1048576) { small++; smallBytes += buffer.Size; }
            else largeBytes += buffer.Size;
        }
        return $"live_small={small} live_small_mb={smallBytes >> 20} live_large_mb={largeBytes >> 20}";
    }

    private void ReportBufferCreations()
    {
        if (_allocationSources is { } sources)
        {
            foreach (var group in sources.GroupBy(pair => pair.Value).OrderByDescending(group => group.Sum(pair => (long)pair.Key.Size)))
                Console.Error.WriteLine($"[PERF][BUFFER_LIVE] source={group.Key} count={group.Count()} bytes={group.Sum(pair => (long)pair.Key.Size)}");
        }
        if (_allocationProfile is not { Count: > 0 } profile) return;
        foreach (var (key, value) in profile.OrderByDescending(pair => pair.Value.Allocated))
            Console.Error.WriteLine($"[PERF][BUFFER_CREATE] source={key} count={value.Count} requested_bytes={value.Requested} allocated_bytes={value.Allocated} merged={value.Merged}");
        profile.Clear();
    }
}
