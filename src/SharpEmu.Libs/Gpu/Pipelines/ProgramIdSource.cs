// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Pipelines;

// Program ids for the program caches of one pipeline cache, which may compile on several threads.
public sealed class ProgramIdSource
{
    private long _last;

    public ulong Next() => (ulong)Interlocked.Increment(ref _last);

    // The last id given out, for diagnostics.
    public ulong Last => (ulong)Volatile.Read(ref _last);
}
