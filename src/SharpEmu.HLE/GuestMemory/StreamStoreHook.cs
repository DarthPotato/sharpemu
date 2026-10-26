// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GuestMemory;

// Guest-memory writes made by the threads that execute the GPU command stream (the command stream
// and render threads) for a stream item: command-processor stores, fills, copies and clears, and
// kernels the emulator runs on the CPU. Work resolved ahead of the stream reads guest memory before
// those items run, so each such write is reported, with its range, as a store it must be checked
// against. The base memory reports at its write entries; other threads pay one thread-static check.
public static class StreamStoreHook
{
    [ThreadStatic]
    private static bool _streamThread;

    private static Func<ulong, ulong, long>? _begin;
    private static Action<long>? _end;

    // Called once on each thread that executes stream items.
    public static void MarkStreamThread() => _streamThread = true;

    // The store reporting, set by the GPU side when work runs ahead of the stream.
    public static void Attach(Func<ulong, ulong, long> begin, Action<long> end)
    {
        _end = end;
        Volatile.Write(ref _begin, begin);
    }

    // Before a write: a token for End, 0 when nothing is reported.
    public static long Begin(ulong address, ulong size) =>
        _streamThread && size != 0 && Volatile.Read(ref _begin) is { } begin ? begin(address, size) : 0;

    public static void End(long token)
    {
        if (token != 0)
        {
            _end!(token);
        }
    }
}
