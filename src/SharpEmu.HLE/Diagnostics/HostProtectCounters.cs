// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Diagnostics;

// Host page protection changes (VirtualProtect) and GPU-tracking faults: each restriction of a mapped page is
// a system call plus a TLB shootdown on every core the process runs on. Counted only with
// SHARPEMU_TRACE_SYNC_CHAIN, which reports the rates and the busiest 1 MiB granules.
public static class HostProtectCounters
{
    public static readonly bool Enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SYNC_CHAIN"));
    public static long Calls;
    public static long Pages;
    // GPU page guards by direction: pages made writable for the CPU, pages made read-only or inaccessible.
    public static long GuardRelaxPages;
    public static long GuardRestrictPages;
    public static long GuardRelaxCalls;
    public static long GuardRestrictCalls;
    public static long Faults;

    // While a sync-chain capture records: pages restricted and faults by 1 MiB granule and by caller kind.
    public static volatile bool Recording;
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Granule, string Kind), long> Histogram = new();

    public static void NotePageGuard(ulong address, ulong size, bool relax)
    {
        if (relax)
        {
            Interlocked.Increment(ref GuardRelaxCalls);
            Interlocked.Add(ref GuardRelaxPages, (long)(size / 4096));
        }
        else
        {
            Interlocked.Increment(ref GuardRestrictCalls);
            Interlocked.Add(ref GuardRestrictPages, (long)(size / 4096));
        }

        NoteGranule(address, size / 4096, (relax ? "relax:" : "restrict:") + Thread.CurrentThread.Name);
    }

    public static void NoteGranule(ulong address, ulong pages, string kind)
    {
        if (!Recording)
        {
            return;
        }

        Histogram.AddOrUpdate((address >> 20, kind), (long)pages, (_, value) => value + (long)pages);
    }

    public static void Note(ulong size)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref Calls);
        Interlocked.Add(ref Pages, (long)((size + 4095) / 4096));
    }
}
