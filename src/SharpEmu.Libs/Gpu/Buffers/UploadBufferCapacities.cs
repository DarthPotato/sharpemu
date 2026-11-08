// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.Gpu.Buffers;

internal static class UploadBufferCapacities
{
    internal const ulong DefaultArenaHalfBytes = 32UL * 1024 * 1024;
    internal const ulong DefaultStreamBytes = 64UL * 1024 * 1024;
    internal const ulong MinimumBytes = 4096;
    internal const ulong MaximumArenaHalfBytes = 256UL * 1024 * 1024;
    internal const ulong MaximumStreamBytes = 512UL * 1024 * 1024;

    // Read at construction, so diagnostics and tests can select capacities without a process restart.
    internal static ulong ArenaHalfBytes => Parse(Environment.GetEnvironmentVariable("SHARPEMU_CONTENT_ARENA_HALF_BYTES"),
        DefaultArenaHalfBytes, MaximumArenaHalfBytes);

    internal static ulong StreamBytes => Parse(Environment.GetEnvironmentVariable("SHARPEMU_STREAM_RING_BYTES"),
        DefaultStreamBytes, MaximumStreamBytes);

    internal static ulong Parse(string? value, ulong defaultBytes, ulong maximumBytes) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes != 0
            ? Math.Clamp(bytes, MinimumBytes, maximumBytes)
            : defaultBytes;
}
