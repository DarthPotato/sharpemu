// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GpuMemory;

// Spin lock that runs in the fault handler; it must not allocate or block in the kernel.
public sealed class RegionLock
{
    public enum Category
    {
        MemoryTracker,
        ImageCache,
    }

    private readonly Category _category;
    private int _held;
    private int _owner;
    private long _holdStarted;
    private long _pendingWaitTicks;
    private long _protectionAtEnter;
    private GpuMemoryAccessProfile.Operation _holdOperation;

    public RegionLock(Category category = Category.MemoryTracker) => _category = category;

    private static bool Timed => GpuMemoryAccessProfile.Enabled || RegionLockTrace.Enabled;

    public readonly ref struct Held
    {
        private readonly RegionLock _lock;

        public Held(RegionLock regionLock)
        {
            _lock = regionLock;
            regionLock.Enter();
        }

        public void Dispose() => _lock.Exit();
    }

    public Held Hold() => new(this);

    public void Enter()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _owner) == thread)
        {
            PageGuard.OnFatal("Cannot acquire the region lock twice on the same thread.");
            return;
        }

        long waited = 0;
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            var waitStarted = Timed ? Stopwatch.GetTimestamp() : 0;
            do
            {
                Thread.SpinWait(1);
            }
            while (Interlocked.CompareExchange(ref _held, 1, 0) != 0);

            if (Timed)
            {
                waited = Stopwatch.GetTimestamp() - waitStarted;
                if (GpuMemoryAccessProfile.Enabled)
                    GpuMemoryAccessProfile.RecordLockWait(_category, waited);
            }
        }

        Volatile.Write(ref _owner, thread);
        if (Timed)
        {
            _holdOperation = GpuMemoryAccessProfile.GetLockOperation(_category, hold: true);
            // Recorded after release: the trace allocates outside fault handlers.
            _pendingWaitTicks = waited;
            _protectionAtEnter = GpuMemoryAccessProfile.ProtectionTicks;
            GpuMemoryAccessProfile.RegionLocksHeld++;
            _holdStarted = Stopwatch.GetTimestamp();
        }
    }

    public void Exit()
    {
        if (Volatile.Read(ref _owner) != Environment.CurrentManagedThreadId)
        {
            PageGuard.OnFatal("Only the thread that holds the region lock can release it.");
            return;
        }

        // Copy measurements before release; the next owner can then replace the lock's fields.
        var holdOperation = _holdOperation;
        var holdTicks = Timed ? Stopwatch.GetTimestamp() - _holdStarted : 0;
        var waitTicks = _pendingWaitTicks;
        var protectionTicks = Timed ? GpuMemoryAccessProfile.ProtectionTicks - _protectionAtEnter : 0;
        Volatile.Write(ref _owner, 0);
        Volatile.Write(ref _held, 0);
        if (GpuMemoryAccessProfile.Enabled)
            GpuMemoryAccessProfile.RecordLockHold(holdOperation, holdTicks);
        if (Timed)
            GpuMemoryAccessProfile.RegionLocksHeld--;
        if (RegionLockTrace.Enabled)
        {
            RegionLockTrace.Record(hold: false, waitTicks);
            RegionLockTrace.Record(hold: true, holdTicks, protectionTicks);
        }
    }
}
