// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GuestMemory;

// Guest ranges in the form that compares across views: the same backing bytes reached through two
// guest views give the same canonical range (GuestSpaceOwner.ForEachCanonicalRange). Used to decide
// whether a store can have changed what a resolution read. Without backed guest memory every range
// is its own.
public static class GuestRangeCanonicalizer
{
    private static GuestSpaceOwner? _owner;

    // The guest memory with shared backing views, set when it is created.
    public static void Attach(GuestSpaceOwner? owner) => Volatile.Write(ref _owner, owner);

    public static void Detach(GuestSpaceOwner owner) => Interlocked.CompareExchange(ref _owner, null, owner);

    // Moves when views change; ranges canonicalized under another generation may name other bytes.
    public static long ViewGeneration => Volatile.Read(ref _owner)?.ViewGeneration ?? 0;

    public static void ForEach(ulong address, ulong size, Action<ulong, ulong> visit)
    {
        if (Volatile.Read(ref _owner) is { } owner)
        {
            owner.ForEachCanonicalRange(address, size, visit);
        }
        else if (size != 0)
        {
            visit(address, size);
        }
    }
}
