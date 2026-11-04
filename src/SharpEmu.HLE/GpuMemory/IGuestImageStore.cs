// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGuestImageStore
{
    // Returns true when recovery completes, even if a later operation adds a new watch.
    bool MarkCpuWrite(ulong address, ulong size);

    // Distinguishes a protection fault from managed write notifications for diagnostics.
    bool MarkCpuWriteFault(ulong address, ulong size) => MarkCpuWrite(address, size);

    void Unregister(ulong address, ulong size);
}
