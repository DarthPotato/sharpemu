// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// Counts the stores of GPU data into guest memory: buffer and image downloads, and buffer transfers
// the caches complete there. Work done ahead of the render thread from guest memory is still
// current while no store is in progress and the count has not moved since the work began.
//
// A store counts once when it starts and again when it ends. Work that began before a store sees
// the first count; work that began while the bytes were being written sees the second, or the store
// still in progress. Counting only at the start let work that began mid-store read old or partly
// copied bytes and still pass.
internal static class GpuWritebackEpoch
{
    private static long _value;
    private static int _activeWriters;

    public static long Value => Volatile.Read(ref _value);

    // Whether work that recorded `recorded` at its start saw no store since.
    public static bool Unchanged(long recorded) =>
        Volatile.Read(ref _activeWriters) == 0 && Volatile.Read(ref _value) == recorded;

    // Wraps one store of GPU data into guest memory; may run on any thread.
    public static WriteScope BeginWrite()
    {
        Interlocked.Increment(ref _activeWriters);
        Interlocked.Increment(ref _value);
        return new WriteScope(true);
    }

    public readonly ref struct WriteScope
    {
        private readonly bool _active;

        internal WriteScope(bool active) => _active = active;

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            // Counted before the writer leaves: a reader that sees no writer also sees the count move.
            Interlocked.Increment(ref _value);
            Interlocked.Decrement(ref _activeWriters);
        }
    }
}
