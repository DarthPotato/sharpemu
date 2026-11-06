// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using DeviceAddressPaging = SharpEmu.ShaderCompiler.Resources.DeviceAddressPaging;

namespace SharpEmu.Libs.Gpu.Buffers;

public sealed unsafe partial class GuestBufferCache
{
    private static long _reportedDeviceWriteConflicts;

    // Pages that shaders wrote through device addresses (runtime V#s, global and FLAT stores,
    // atomics) are reported by the GPU after the fact; no binding announced them. They become
    // GPU-owned like any written binding, so CPU reads download them and a CPU write to the page
    // flushes them first instead of a later upload overwriting them with stale guest bytes.
    // A device page reported with the mask of the granules it wrote (DeviceAddressPaging) marks
    // only those: a tracker page without one was not written, and the bytes of a written tracker
    // page outside them stay guest-owned, so reading them waits for nothing.
    internal void NoteDeviceAddressWrites(ulong guestAddress, ulong size, uint granuleMask = uint.MaxValue)
    {
        if (!IsValidRange(guestAddress, size))
        {
            return;
        }

        const ulong granuleBytes = 1UL << DeviceAddressPaging.WriteGranuleBits;
        const int granulesPerTrackerPage = (int)(TrackerLayout.PageBytes / granuleBytes);
        var byGranule = granuleMask != uint.MaxValue && size == DeviceAddressPaging.PageSize &&
            (guestAddress & DeviceAddressPaging.PageOffsetMask) == 0;
        var end = guestAddress + size;
        var marked = false;
        for (var page = guestAddress & ~(TrackerLayout.PageBytes - 1); page < end; page += TrackerLayout.PageBytes)
        {
            var pageGranules = byGranule
                ? (granuleMask >> (int)((page - guestAddress) / granuleBytes)) & ((1u << granulesPerTrackerPage) - 1)
                : (1u << granulesPerTrackerPage) - 1;
            if (pageGranules == 0)
            {
                continue;
            }

            if (FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
            {
                continue;
            }

            var conflicts = _tracker.MarkGpuWrittenPages(page, TrackerLayout.PageBytes);
            if (conflicts != 0)
            {
                // The CPU dirtied the page before the write was reported: the page stays
                // CPU-owned and the shader's bytes are merged into guest memory instead: now when the
                // owner's copy is mapped, otherwise once the render thread has downloaded the page.
                if (_sharedPages.Contains(page))
                {
                    marked |= PullSharedPage(page, owner);
                }
                else
                {
                    AdoptLateSharedPage(page, owner);
                }

                continue;
            }

            // The first CPU access downloads the page; the shadow starts from the downloaded bytes.
            if (!_sharedPages.Contains(page))
            {
                lock (_shaderWrittenPages)
                {
                    if (_shaderWrittenPages.Count < MaxShaderWrittenPages)
                    {
                        _shaderWrittenPages[page] = owner;
                        Volatile.Write(ref _shaderWrittenPageCount, _shaderWrittenPages.Count);
                    }
                }
            }

            for (var first = 0; first < granulesPerTrackerPage;)
            {
                if ((pageGranules & (1u << first)) == 0)
                {
                    first++;
                    continue;
                }

                var last = first;
                while (last + 1 < granulesPerTrackerPage && (pageGranules & (1u << (last + 1))) != 0)
                {
                    last++;
                }

                var runAddress = page + (ulong)first * granuleBytes;
                var runSize = (ulong)(last - first + 1) * granuleBytes;
                if (Diagnostics.GpuReadTrace.Enabled) Diagnostics.GpuReadTrace.NoteMark(runAddress, runSize, device: true);
                using var modified = WriteGpuModified();
                if (!_gpuModifiedRanges.Contains(runAddress, runSize))
                {
                    _gpuModifiedRanges.Add(runAddress, runSize);
                    BumpGpuModifiedVersion();
                }

                first = last + 1;
            }

            owner.NoteGpuWrite();
            marked = true;
            if (GuestGpuMemoryHook.Traces(page, TrackerLayout.PageBytes))
                GuestGpuMemoryHook.Trace(page, TrackerLayout.PageBytes, $"device-address-write-reported submission_tick={_scheduler.CurrentTick}");
        }

        if (marked)
        {
            RequireImageCache().InvalidateMemoryCopiesFromGpu(guestAddress, size);
        }
    }

    // Pages shaders write through device addresses; see SharedPageShadows. 64 MiB of shadows at most.
    private const int SharedPageCapacity = 16384;
    private readonly SharedPageShadows _sharedPages = new(TrackerLayout.PageBytes, SharedPageCapacity);

    // The GPU pass that writes the changed bytes of shared-page uploads (GpuTiler.MergeChangedBytes),
    // set by the image cache that owns it; without it, uploads copy each changed run.
    internal Action<TilerBufferSpan, TilerBufferSpan, uint, uint>? ChangedByteMerge { get; set; }

    // SHARPEMU_SHARED_PAGE_MERGE=0 keeps copying each changed run of a shared page (for comparison).
    private static readonly bool SharedPageMergeEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_SHARED_PAGE_MERGE") != "0";
    private static long _reportedLateSharedPages;
    private static long _reportedSharedPageMerges;
    private static int _reportedSharedPageCapacity;

    // Pages shaders wrote that have no shadow yet, with their owner at the time of the write.
    private const int MaxShaderWrittenPages = 1 << 20;
    private readonly Dictionary<ulong, GpuBuffer> _shaderWrittenPages = new();
    private int _shaderWrittenPageCount;

    // A page the CPU dirtied before its first shader write was reported: shadowed from the GPU copy.
    private void AdoptLateSharedPage(ulong page, GpuBuffer owner)
    {
        var adopted = owner.MappedPointer != null && owner.IsCoherent &&
            _sharedPages.Adopt(page, owner, new ReadOnlySpan<byte>(owner.MappedPointer + owner.Offset(page), (int)TrackerLayout.PageBytes),
                _scheduler.CurrentTick);
        if (adopted) _sharedPages.NoteCpuWrite(page, TrackerLayout.PageBytes);
        if (!adopted && _sharedPages.Count >= SharedPageCapacity && Interlocked.Exchange(ref _reportedSharedPageCapacity, 1) == 0)
        {
            Console.Error.WriteLine("[GPU][WARN] The shared page shadows are full; CPU writes to further shader-written pages may lose shader bytes.");
        }

        var reported = Interlocked.Increment(ref adopted ? ref _reportedLateSharedPages : ref _reportedDeviceWriteConflicts);
        if (reported <= 16 || (reported & (reported - 1)) == 0)
        {
            Console.Error.WriteLine(adopted
                ? FormattableString.Invariant(
                    $"[GPU][SHARED_PAGE] The CPU dirtied page=0x{page:X16} before its first shader write was reported; shader bytes an upload already overwrote are lost once shared={_sharedPages.Count} n={reported}")
                : FormattableString.Invariant(
                    $"[GPU][WARN] A device-address write reached a page the CPU dirtied and the page has no shadow: page=0x{page:X16} count={reported}"));
        }
    }

    internal GuestPageTracker Tracker => _tracker;

    // Writes downloaded GPU bytes into guest memory; shadows of shared pages take them too.
    internal bool WriteDownloaded(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (Diagnostics.GpuReadTrace.Enabled)
        {
            var previous = new byte[bytes.Length];
            if (_backing.TryReadBacking(address, previous))
            {
                Diagnostics.GpuReadTrace.NoteDownload(address, previous, bytes);
            }
        }

        if (!_backing.TryWriteBacking(address, bytes))
        {
            return false;
        }

        _sharedPages.NoteDownloaded(address, bytes);
        AdoptDownloadedPages(address, (ulong)bytes.Length);
        return true;
    }

    // Downloaded shader-written pages: the GPU-written bytes are in guest memory now, but guest
    // memory says nothing about the bytes the owner's GPU copy holds elsewhere on the page. Those may
    // never have received the guest's CPU writes (they can reach the GPU through the stream ring, or
    // the owner can be newer than its last full upload), so a shadow taken from guest memory would
    // filter out every later CPU write that repeats the guest's value and leave the stale GPU bytes.
    // A readable owner is shadowed from its own GPU copy: the download made the GPU-written bytes
    // equal in guest memory, and any other difference is a CPU change the next upload must carry.
    // Otherwise the page has no baseline: its next upload copies all of it into the owner. The
    // CPU's write to a page marks it dirty, so that upload happens before the GPU reads the CPU's
    // value; the whole page is guest memory at that point, with the downloaded GPU bytes in it.
    private void AdoptDownloadedPages(ulong address, ulong size)
    {
        if (Volatile.Read(ref _shaderWrittenPageCount) == 0)
        {
            return;
        }

        var end = address + size;
        for (var page = address & ~(TrackerLayout.PageBytes - 1); page < end; page += TrackerLayout.PageBytes)
        {
            GpuBuffer? owner;
            lock (_shaderWrittenPages)
            {
                if (!_shaderWrittenPages.Remove(page, out owner))
                {
                    continue;
                }

                Volatile.Write(ref _shaderWrittenPageCount, _shaderWrittenPages.Count);
            }

            // A retired owner may be unmapped: only the buffer that holds the page now is read.
            var adopted = owner!.MappedPointer != null && owner.IsCoherent && ReferenceEquals(FindOwner(page, TrackerLayout.PageBytes), owner)
                ? _sharedPages.Adopt(page, owner,
                    new ReadOnlySpan<byte>(owner.MappedPointer + owner.Offset(page), (int)TrackerLayout.PageBytes), _scheduler.CurrentTick)
                : _sharedPages.AdoptUnsynchronized(page);
            if (!adopted && Interlocked.Exchange(ref _reportedSharedPageCapacity, 1) == 0)
            {
                Console.Error.WriteLine("[GPU][WARN] The shared page shadows are full; CPU writes to further shader-written pages may lose shader bytes.");
            }
        }
    }

    // Merges what shaders wrote on a shared page into guest memory; true when bytes were merged. An
    // owner the host cannot read through a coherent mapping is merged later, from a download.
    private bool PullSharedPage(ulong page, GpuBuffer owner)
    {
        if (_sharedPages.Count == 0 || !_sharedPages.Contains(page))
        {
            return false;
        }

        if (owner.MappedPointer == null || !owner.IsCoherent)
        {
            DeferSharedPageMerge(page);
            return false;
        }

        // Read the completed tick first: a tick completed after the snapshot cannot prove
        // that the snapshot includes its uploads.
        var completedTick = _scheduler.Timeline.CompletedTick;
        // The GPU may still write the page: merge one snapshot of it.
        Span<byte> gpu = stackalloc byte[(int)TrackerLayout.PageBytes];
        new ReadOnlySpan<byte>(owner.MappedPointer + owner.Offset(page), gpu.Length).CopyTo(gpu);
        return MergeSharedPage(page, owner, gpu, completedTick);
    }

    private bool IsCpuWriteHotPage(ulong page) => _tracker.IsCpuWriteHotRange(page, TrackerLayout.PageBytes);

    // Query tracker locks before entering the shadow lock: downloads can take them in that order.
    private HashSet<ulong>? FindCpuWriteHotSharedPages(List<BufferCopy> copies, ulong address)
    {
        HashSet<ulong>? hot = null;
        foreach (var copy in copies)
        {
            var begin = address + copy.DstOffset;
            for (var page = begin & ~(TrackerLayout.PageBytes - 1); page < begin + copy.Size; page += TrackerLayout.PageBytes)
            {
                if (_sharedPages.Contains(page) && IsCpuWriteHotPage(page)) (hot ??= new()).Add(page);
            }
        }
        return hot;
    }

    // Merges a snapshot of the owner's copy of a shadowed page into guest memory and the shadow: the
    // bytes shaders changed reach guest memory where the CPU left them alone, and the shadow takes the
    // GPU state, so a later CPU write is compared with what the GPU holds. completedTick is the newest
    // tick whose effects the snapshot is known to include. True when bytes were merged.
    private bool MergeSharedPage(ulong page, GpuBuffer owner, ReadOnlySpan<byte> gpu, ulong completedTick)
    {
        if (IsCpuWriteHotPage(page)) _sharedPages.NoteCpuWrite(page, TrackerLayout.PageBytes);
        var guest = new byte[TrackerLayout.PageBytes];
        if (!_backing.TryReadBacking(page, guest))
        {
            return false;
        }

        var count = _sharedPages.PullGpuWrites(page, owner, gpu, guest, completedTick, (offset, length) =>
        {
            if (!_backing.TryWriteBacking(page + (ulong)offset, guest.AsSpan(offset, length)))
            {
                throw SubmissionScheduler.Fatal($"Could not merge shader writes into guest memory: addr=0x{page + (ulong)offset:X16} size=0x{length:X}");
            }
        });
        if (count == 0)
        {
            return false;
        }

        var reported = Interlocked.Increment(ref _reportedSharedPageMerges);
        if (reported <= 16 || (reported & (reported - 1)) == 0)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[GPU][SHARED_PAGE] merged shader bytes into a CPU-dirty page=0x{page:X16} bytes={count} shared={_sharedPages.Count} merged_bytes={_sharedPages.MergedBytes} skipped_upload_bytes={_sharedPages.SkippedUploadBytes} n={reported}"));
        }

        if (GuestGpuMemoryHook.Traces(page, TrackerLayout.PageBytes))
            GuestGpuMemoryHook.Trace(page, TrackerLayout.PageBytes, $"shared-page-merged bytes={count} submission_tick={_scheduler.CurrentTick}");
        return true;
    }

    // Shadowed pages whose owner the host cannot read through a coherent mapping (device-local memory
    // behind a non-coherent or unmapped heap) and that a shader wrote after the CPU dirtied them.
    // The completion action that reports the write may not wait for the GPU, so it cannot download
    // the page. Until the render thread does, the shader's bytes exist only in the GPU copy: the
    // shadow does not know them, so an upload would filter out any CPU write that repeats the
    // shadow's value and leave the shader's byte in the GPU copy for good.
    // Each page waits here with the completed tick at the report, which the shader's writes precede.
    // The render thread reads such a page back on the async readback queue as soon as the tick it
    // depends on (that report and the last upload staged for the page) is submitted, without waiting:
    // the page is then in flight until the copy completes and its snapshot is merged. Merging happens
    // before an upload reads the page (SynchronizeBuffer), before a synchronized CPU read of it, and
    // opportunistically after each batch submission and fault scan. Only a caller that needs a page
    // whose copy is still in flight waits, for that copy alone. The owner is looked up again at the
    // merge: a replaced owner holds the same bytes, and a retired one needs no merge.
    // A notified CPU write supersedes the pending snapshot for the entire page, even if it repeats
    // the shadow's value. The next full upload carries the CPU's page and reestablishes the baseline.
    private readonly Dictionary<ulong, ulong> _pendingSharedMerges = new();
    private readonly Dictionary<ulong, SharedMergeFlight> _inflightSharedMerges = new();
    private readonly List<SharedMergeBatch> _sharedMergeBatches = new();
    private int _pendingSharedMergeCount;
    private bool _resolvingSharedMerges;
    private const int SharedMergeChunkPages = 256;

    // One readback ticket covering several pages. SnapshotTick is the newest main-queue tick the copy
    // waits for: every upload staged up to it and every shader write reported by it is in the bytes.
    private sealed class SharedMergeBatch(VulkanAsyncReadback.Ticket ticket, ulong[] pages, ulong[] reported, GpuBuffer[] owners, ulong snapshotTick)
    {
        public VulkanAsyncReadback.Ticket Ticket { get; } = ticket;
        public ulong[] Pages { get; } = pages;
        public ulong[] Reported { get; } = reported;
        public GpuBuffer[] Owners { get; } = owners;
        public ulong SnapshotTick { get; } = snapshotTick;
    }

    private readonly record struct SharedMergeFlight(SharedMergeBatch Batch, int Index);

    private readonly record struct SharedMergeCandidate(ulong Page, ulong Reported, GpuBuffer Owner, ulong WaitTick);

    private enum SharedMergeCounter
    {
        Issued,
        MergedWithoutWait,
        WaitedInFlight,
        IssuedAndWaited,
        ForcedFlush,
        Reissued,
        Count,
    }

    // How a batch is consumed: whether its ticket had already completed, was in flight when a caller
    // needed it, or was issued by that very call.
    private enum SharedMergeArrival
    {
        Completed,
        WaitedInFlight,
        IssuedAndWaited,
    }

    private readonly long[] _sharedMergeCounts = new long[(int)SharedMergeCounter.Count];
    private static readonly long[] ReportedSharedMergeCounts = new long[(int)SharedMergeCounter.Count];

    private void CountSharedMerge(SharedMergeCounter counter, int pages)
    {
        if (pages == 0)
        {
            return;
        }

        Interlocked.Add(ref _sharedMergeCounts[(int)counter], pages);
        Interlocked.Add(ref ReportedSharedMergeCounts[(int)counter], pages);
    }

    // Pages handed to the async readback queue (a page issued again counts again).
    internal long SharedMergesIssued => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.Issued]);

    // Pages merged from a copy that had already completed when it was looked at.
    internal long SharedMergesMergedWithoutWait => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.MergedWithoutWait]);

    // Pages merged after blocking on a copy that was issued earlier, which waits for that copy only.
    internal long SharedMergesWaitedInFlight => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.WaitedInFlight]);

    // Pages issued by the call that needed them at once (their tick was already submitted) and waited for.
    internal long SharedMergesIssuedAndWaited => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.IssuedAndWaited]);

    // Pages whose tick was still in the open command buffer or that found no free readback slot, so
    // the call submitted the buffer and waited: the blocking path.
    internal long SharedMergesForcedFlush => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.ForcedFlush]);

    // Pages whose snapshot was dropped because an upload was staged after it was issued.
    internal long SharedMergesReissued => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.Reissued]);

    // True when every shared page readback in flight has completed, so merging them would not wait.
    internal bool SharedMergeReadsComplete()
    {
        SharedMergeBatch[] batches;
        lock (_pendingSharedMerges)
        {
            batches = _sharedMergeBatches.ToArray();
        }

        if (AsyncReadback is not { } readback)
        {
            return batches.Length == 0;
        }

        foreach (var batch in batches)
        {
            if (!readback.IsComplete(batch.Ticket))
            {
                return false;
            }
        }

        return true;
    }

    public static string TakeSharedMergeReport() => FormattableString.Invariant(
        $"[PERF][SHARED_MERGE] issued={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.Issued], 0)} merged_nowait={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.MergedWithoutWait], 0)} waited_inflight={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.WaitedInFlight], 0)} issued_waited={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.IssuedAndWaited], 0)} forced_flush={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.ForcedFlush], 0)} reissued={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.Reissued], 0)}");

    private void DeferSharedPageMerge(ulong page) =>
        DeferSharedPageMerge(page, _scheduler.Timeline.CompletedTick);

    private void DeferSharedPageMerge(ulong page, ulong reportedTick)
    {
        lock (_pendingSharedMerges)
        {
            _pendingSharedMerges[page] = _pendingSharedMerges.TryGetValue(page, out var earlier)
                ? Math.Max(earlier, reportedTick)
                : reportedTick;
            PublishSharedMergeCount();
        }
    }

    // Everything the resolve entry points still have to look at: pending pages, pages in flight and
    // the batches that hold a readback slot (a batch whose pages were all superseded still does).
    // Callers hold the _pendingSharedMerges lock.
    private void PublishSharedMergeCount() =>
        Volatile.Write(ref _pendingSharedMergeCount,
            _pendingSharedMerges.Count + _inflightSharedMerges.Count + _sharedMergeBatches.Count);

    private bool HasPendingSharedMerges(ulong guestAddress, ulong size)
    {
        if (Volatile.Read(ref _pendingSharedMergeCount) == 0)
        {
            return false;
        }

        lock (_pendingSharedMerges)
        {
            return TakePendingSharedMerges(guestAddress, size, null) || OverlapsSharedPages(_inflightSharedMerges, guestAddress, size);
        }
    }

    private static bool OverlapsSharedPages<T>(Dictionary<ulong, T> pages, ulong guestAddress, ulong size)
    {
        const ulong pageBytes = TrackerLayout.PageBytes;
        if (pages.Count == 0)
        {
            return false;
        }

        var first = guestAddress & ~(pageBytes - 1);
        var end = guestAddress + size;
        if ((end - first) / pageBytes <= (ulong)pages.Count)
        {
            for (var page = first; page < end; page += pageBytes)
            {
                if (pages.ContainsKey(page))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var page in pages.Keys)
        {
            if (page < end && page + pageBytes > guestAddress)
            {
                return true;
            }
        }

        return false;
    }

    // The pending pages overlapping the range, removed from the table when a list takes them.
    private bool TakePendingSharedMerges(ulong guestAddress, ulong size, List<KeyValuePair<ulong, ulong>>? taken)
    {
        const ulong pageBytes = TrackerLayout.PageBytes;
        var first = guestAddress & ~(pageBytes - 1);
        var end = guestAddress + size;
        var any = false;
        if ((end - first) / pageBytes <= (ulong)_pendingSharedMerges.Count)
        {
            for (var page = first; page < end; page += pageBytes)
            {
                if (!_pendingSharedMerges.TryGetValue(page, out var tick))
                {
                    continue;
                }

                any = true;
                if (taken is null)
                {
                    return true;
                }

                taken.Add(new KeyValuePair<ulong, ulong>(page, tick));
            }
        }
        else
        {
            foreach (var pair in _pendingSharedMerges)
            {
                if (pair.Key < end && pair.Key + pageBytes > guestAddress)
                {
                    any = true;
                    if (taken is null)
                    {
                        return true;
                    }

                    taken.Add(pair);
                }
            }
        }

        if (taken is not null)
        {
            foreach (var pair in taken)
            {
                _pendingSharedMerges.Remove(pair.Key);
            }

            PublishSharedMergeCount();
        }

        return any;
    }

    // Brings the pending and in-flight pages in the range to guest memory and the shadows. With wait,
    // every one is: a copy already in flight is waited for on its own, and a page not yet read is
    // read at once (submitting its tick first when that is still being recorded). Without it, the
    // call merges the copies that have completed, starts a copy for every page whose tick is
    // submitted, and leaves the rest pending; it never waits for the GPU.
    // Render thread only, never inside a completion action.
    internal void ResolvePendingSharedMerges(ulong guestAddress, ulong size, bool wait)
    {
        using var sourceScope = ReadbackSourceProfile.Begin("SharedPageMerge");
        if (Volatile.Read(ref _pendingSharedMergeCount) == 0 || _resolvingSharedMerges ||
            !_scheduler.Active || _scheduler.InsideTickCallback)
        {
            return;
        }

        _resolvingSharedMerges = true;
        try
        {
            ConsumeSharedMergeBatches(guestAddress, size, wait);
            List<KeyValuePair<ulong, ulong>> pages;
            lock (_pendingSharedMerges)
            {
                if (_pendingSharedMerges.Count == 0)
                {
                    return;
                }

                pages = new List<KeyValuePair<ulong, ulong>>();
                if (!TakePendingSharedMerges(guestAddress, size, pages))
                {
                    return;
                }
            }

            ResolveSharedMerges(pages, wait);
        }
        finally
        {
            _resolvingSharedMerges = false;
        }
    }

    private void ResolveSharedMerges(List<KeyValuePair<ulong, ulong>> pages, bool wait)
    {
        var ready = new List<SharedMergeCandidate>(pages.Count);
        foreach (var (page, reported) in pages)
        {
            // A page without a shadow or an owner has nothing to merge into or from.
            if (!_sharedPages.TryGetStagedTick(page, out var staged) || FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
            {
                continue;
            }

            ready.Add(new SharedMergeCandidate(page, reported, owner, Math.Max(reported, staged)));
        }

        if (AsyncReadback is not { } readback)
        {
            // Without the readback queue the copy goes through the download ring and drains the scheduler.
            if (wait)
            {
                CountSharedMerge(SharedMergeCounter.ForcedFlush, ready.Count);
                MergeSharedPagesSynchronously(ready);
            }
            else
            {
                foreach (var candidate in ready)
                {
                    DeferSharedPageMerge(candidate.Page, candidate.Reported);
                }
            }

            return;
        }

        var unissued = new List<SharedMergeCandidate>();
        var issued = IssueSharedMergeReads(ready, unissued);
        if (!wait)
        {
            // The pages whose tick is still being recorded are issued after the next submission.
            foreach (var candidate in unissued)
            {
                DeferSharedPageMerge(candidate.Page, candidate.Reported);
            }

            return;
        }

        if (unissued.Count != 0)
        {
            // The newest tick the pages wait for is still in the open command buffer: submit it, then read.
            CountSharedMerge(SharedMergeCounter.ForcedFlush, unissued.Count);
            _scheduler.Flush(SubmitReason.Readback);
            var stuck = new List<SharedMergeCandidate>();
            issued.AddRange(IssueSharedMergeReads(unissued, stuck));
            // No free readback slot: the synchronous slot or the download ring reads them.
            MergeSharedPagesSynchronously(stuck);
        }

        foreach (var batch in issued)
        {
            readback.Wait(batch.Ticket);
            ConsumeSharedMergeBatch(batch, SharedMergeArrival.IssuedAndWaited);
        }
    }

    // Starts a copy on the async readback queue for every candidate whose wait tick is already
    // submitted, in batches that share one ticket; the others go to unissued. Pages leave the pending
    // table and become in flight. Never waits for the GPU and never submits.
    private List<SharedMergeBatch> IssueSharedMergeReads(List<SharedMergeCandidate> candidates, List<SharedMergeCandidate> unissued)
    {
        using var sourceScope = ReadbackSourceProfile.Begin("SharedPageMerge");
        const ulong pageBytes = TrackerLayout.PageBytes;
        var issued = new List<SharedMergeBatch>();
        var readback = AsyncReadback;
        var submittedBelow = _scheduler.CurrentTick;
        var eligible = new List<SharedMergeCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (readback is null || candidate.WaitTick >= submittedBelow)
            {
                unissued.Add(candidate);
            }
            else
            {
                eligible.Add(candidate);
            }
        }

        for (var first = 0; first < eligible.Count; first += SharedMergeChunkPages)
        {
            var count = Math.Min(SharedMergeChunkPages, eligible.Count - first);
            var pieces = new ReadbackPiece[count];
            var waitTick = 0UL;
            for (var index = 0; index < count; index++)
            {
                var candidate = eligible[first + index];
                pieces[index] = new ReadbackPiece(candidate.Owner, candidate.Owner.Offset(candidate.Page), pageBytes);
                waitTick = Math.Max(waitTick, candidate.WaitTick);
            }

            VulkanAsyncReadback.Ticket? ticket;
            using (_device.Slabs.BeginForeignRead())
            {
                if (!readback!.TryBegin(pieces, waitTick, out ticket) || ticket is null)
                {
                    unissued.AddRange(eligible.GetRange(first, eligible.Count - first));
                    break;
                }

                // The owner may be retired while the copy is in flight; its memory stays until the merge.
                foreach (var piece in pieces)
                {
                    piece.Source.RetainForeignRead();
                }
            }

            var batchPages = new ulong[count];
            var batchReported = new ulong[count];
            var batchOwners = new GpuBuffer[count];
            for (var index = 0; index < count; index++)
            {
                var candidate = eligible[first + index];
                batchPages[index] = candidate.Page;
                batchReported[index] = candidate.Reported;
                batchOwners[index] = candidate.Owner;
            }

            var batch = new SharedMergeBatch(ticket, batchPages, batchReported, batchOwners, waitTick);
            lock (_pendingSharedMerges)
            {
                for (var index = 0; index < count; index++)
                {
                    // A page read again supersedes its earlier copy, which is then only reaped.
                    _inflightSharedMerges[batchPages[index]] = new SharedMergeFlight(batch, index);
                }

                _sharedMergeBatches.Add(batch);
                PublishSharedMergeCount();
            }

            CountSharedMerge(SharedMergeCounter.Issued, count);
            issued.Add(batch);
        }

        return issued;
    }

    // Merges the batches of in-flight copies that overlap the range and have completed, and, with
    // wait, waits for the ones a page of the range still depends on. A copy outside the range is
    // left alone unless the range covers everything.
    private void ConsumeSharedMergeBatches(ulong guestAddress, ulong size, bool wait)
    {
        SharedMergeBatch[] batches;
        lock (_pendingSharedMerges)
        {
            if (_sharedMergeBatches.Count == 0)
            {
                return;
            }

            batches = _sharedMergeBatches.ToArray();
        }

        if (AsyncReadback is not { } readback)
        {
            return;
        }

        const ulong pageBytes = TrackerLayout.PageBytes;
        var end = guestAddress + size;
        foreach (var batch in batches)
        {
            var overlaps = false;
            var needed = false;
            lock (_pendingSharedMerges)
            {
                for (var index = 0; index < batch.Pages.Length; index++)
                {
                    var page = batch.Pages[index];
                    if (page >= end || page + pageBytes <= guestAddress)
                    {
                        continue;
                    }

                    overlaps = true;
                    if (_inflightSharedMerges.TryGetValue(page, out var flight) && ReferenceEquals(flight.Batch, batch))
                    {
                        needed = true;
                        break;
                    }
                }
            }

            if (!overlaps)
            {
                continue;
            }

            if (readback.IsComplete(batch.Ticket))
            {
                ConsumeSharedMergeBatch(batch, SharedMergeArrival.Completed);
            }
            else if (wait && needed)
            {
                readback.Wait(batch.Ticket);
                ConsumeSharedMergeBatch(batch, SharedMergeArrival.WaitedInFlight);
            }
        }
    }

    // Merges the snapshot of a completed copy page by page and frees its readback slot. A page that
    // was read again since is left to its newer copy; one whose shadow got an upload staged after the
    // copy's tick is not merged from this snapshot and goes back to the pending table.
    private void ConsumeSharedMergeBatch(SharedMergeBatch batch, SharedMergeArrival arrival)
    {
        const int pageBytes = (int)TrackerLayout.PageBytes;
        var readback = AsyncReadback ?? throw SubmissionScheduler.Fatal("The shared page readback lost its queue.");
        lock (_pendingSharedMerges)
        {
            if (!_sharedMergeBatches.Remove(batch))
            {
                return;
            }
        }

        var snapshot = new byte[batch.Pages.Length * pageBytes];
        var copied = 0;
        try
        {
            readback.Complete(batch.Ticket, (index, bytes) =>
            {
                bytes.CopyTo(snapshot.AsSpan(index * pageBytes, pageBytes));
                copied++;
            });
        }
        finally
        {
            foreach (var owner in batch.Owners)
            {
                owner.ReleaseForeignRead();
            }
        }

        var current = new bool[batch.Pages.Length];
        lock (_pendingSharedMerges)
        {
            for (var index = 0; index < batch.Pages.Length; index++)
            {
                if (_inflightSharedMerges.TryGetValue(batch.Pages[index], out var flight) && ReferenceEquals(flight.Batch, batch))
                {
                    current[index] = true;
                    _inflightSharedMerges.Remove(batch.Pages[index]);
                }
            }

            PublishSharedMergeCount();
        }

        var merged = 0;
        var stale = 0;
        for (var index = 0; index < batch.Pages.Length; index++)
        {
            if (!current[index])
            {
                continue;
            }

            var page = batch.Pages[index];
            if (copied != batch.Pages.Length)
            {
                // The queue closed before the copy was read: nothing was snapshotted.
                DeferSharedPageMerge(page, batch.Reported[index]);
                continue;
            }

            // The owner may have been replaced meanwhile; its copy of the page holds the same bytes.
            if (!_sharedPages.TryGetStagedTick(page, out var staged) || FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
            {
                continue;
            }

            if (staged > batch.SnapshotTick)
            {
                // An upload or buffer copy was staged after the copy's tick; the snapshot lacks those bytes.
                DeferSharedPageMerge(page, batch.Reported[index]);
                stale++;
                continue;
            }

            merged++;
            var slice = snapshot.AsSpan(index * pageBytes, pageBytes);
            if (MergeSharedPage(page, owner, slice, batch.SnapshotTick))
            {
                RequireImageCache().InvalidateMemoryCopiesFromGpu(page, TrackerLayout.PageBytes);
            }

            // A later report the snapshot already covers needs no copy of its own.
            lock (_pendingSharedMerges)
            {
                if (_pendingSharedMerges.TryGetValue(page, out var pendingTick) && pendingTick <= batch.SnapshotTick)
                {
                    _pendingSharedMerges.Remove(page);
                    PublishSharedMergeCount();
                }
            }
        }

        CountSharedMerge(arrival switch
        {
            SharedMergeArrival.Completed => SharedMergeCounter.MergedWithoutWait,
            SharedMergeArrival.WaitedInFlight => SharedMergeCounter.WaitedInFlight,
            _ => SharedMergeCounter.IssuedAndWaited,
        }, merged);
        CountSharedMerge(SharedMergeCounter.Reissued, stale);
    }

    // Reads the candidates through the synchronous readback slot, or the download ring without a
    // readback queue, and merges them. Submits and waits for the GPU as the read needs.
    private void MergeSharedPagesSynchronously(List<SharedMergeCandidate> ready)
    {
        for (var first = 0; first < ready.Count; first += SharedMergeChunkPages)
        {
            var count = Math.Min(SharedMergeChunkPages, ready.Count - first);
            var snapshot = new byte[count * (int)TrackerLayout.PageBytes];
            var snapshotTick = ReadOwnerPages(ready, first, count, snapshot);
            for (var index = 0; index < count; index++)
            {
                var (page, reported, _, _) = ready[first + index];
                // The owner may have been replaced meanwhile; its copy of the page holds the same bytes.
                if (!_sharedPages.TryGetStagedTick(page, out var staged) || FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
                {
                    continue;
                }

                if (staged > snapshotTick)
                {
                    // An upload or buffer copy was staged after the snapshot; it lacks those bytes.
                    DeferSharedPageMerge(page, reported);
                    continue;
                }

                var slice = snapshot.AsSpan(index * (int)TrackerLayout.PageBytes, (int)TrackerLayout.PageBytes);
                if (MergeSharedPage(page, owner, slice, snapshotTick))
                {
                    RequireImageCache().InvalidateMemoryCopiesFromGpu(page, TrackerLayout.PageBytes);
                }
            }
        }
    }

    // Teardown: waits for the copies in flight and drops them; their pages need no merge any more.
    private void AbandonSharedMergeBatches()
    {
        SharedMergeBatch[] batches;
        lock (_pendingSharedMerges)
        {
            batches = _sharedMergeBatches.ToArray();
            _sharedMergeBatches.Clear();
            _inflightSharedMerges.Clear();
            PublishSharedMergeCount();
        }

        var readback = AsyncReadback;
        foreach (var batch in batches)
        {
            if (readback is not null)
            {
                readback.Wait(batch.Ticket);
                readback.Complete(batch.Ticket, null);
            }

            foreach (var owner in batch.Owners)
            {
                owner.ReleaseForeignRead();
            }
        }
    }

    // Copies each page out of its owner after the ticks it waits for, and returns the newest tick the
    // snapshot is known to include. The async readback queue waits for only those ticks; without it the
    // copy goes through the download ring and the call drains the scheduler.
    private ulong ReadOwnerPages(List<SharedMergeCandidate> pages, int first, int count, byte[] snapshot)
    {
        using var sourceScope = ReadbackSourceProfile.Begin("SharedPageMerge");
        const ulong pageBytes = TrackerLayout.PageBytes;
        var waitTick = 0UL;
        for (var index = 0; index < count; index++)
        {
            waitTick = Math.Max(waitTick, pages[first + index].WaitTick);
        }

        if (AsyncReadback is { } readback)
        {
            using var foreignRead = _device.Slabs.BeginForeignRead();
            // The newest tick may still be recording: the readback queue needs it submitted.
            if (waitTick >= _scheduler.CurrentTick)
            {
                _scheduler.Flush(SubmitReason.Readback);
            }

            var pieces = new ReadbackPiece[count];
            for (var index = 0; index < count; index++)
            {
                var (page, _, owner, _) = pages[first + index];
                pieces[index] = new ReadbackPiece(owner, owner.Offset(page), pageBytes);
            }

            readback.Read(pieces, waitTick, (index, bytes) => bytes.CopyTo(snapshot.AsSpan((int)((uint)index * pageBytes), (int)pageBytes)));
            return waitTick;
        }

        var total = (ulong)count * pageBytes;
        if (!_download.TryMap(total, out var baseOffset, BufferDownloadBatchPlanner.Alignment))
        {
            throw SubmissionScheduler.Fatal("The download ring could not map the shared page snapshot.");
        }

        for (var index = 0; index < count; index++)
        {
            var (page, _, owner, _) = pages[first + index];
            _download.CopyFrom(
                _scheduler.Current, owner, owner.Offset(page), baseOffset + (ulong)index * pageBytes, pageBytes,
                AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
        }

        _download.Commit();
        _scheduler.ProfileReadback(total);
        var copyTick = _scheduler.CurrentTick;
        _scheduler.Finish();
        _scheduler.WaitForPriorityOperations(copyTick);
        _download.Invalidate(baseOffset, total);
        _download.Mapped.Slice((int)baseOffset, (int)total).CopyTo(snapshot);
        return copyTick;
    }

    // The bytes a storage binding may add around a span: the descriptor offset rounds down to the
    // device's storage offset alignment (at most 256) and the range up to a word.
    private const ulong StorageBindingSlack = 256 + 4;

    // Records the GPU pass that writes the bytes that differ into the buffer, in chunks that one
    // storage descriptor binds on each side.
    private void RecordSharedPageMerge(GpuBuffer buffer, SharedPageMergePlan plan)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferUploadRecording);
        var maxBytes = (ulong)_device.MaxStorageBufferRange - StorageBindingSlack;
        foreach (var (start, count) in plan.Chunks(maxBytes))
        {
            RecordSharedPageMergeChunk(buffer, plan, start, count);
        }
    }

    // Stages one chunk's records, then each segment's new and old bytes, in one upload allocation.
    private void RecordSharedPageMergeChunk(GpuBuffer buffer, SharedPageMergePlan plan, int start, int count)
    {
        var segments = plan.Segments;
        var minimum = segments[start].DestinationOffset;
        var maximum = 0UL;
        var largest = 0u;
        var dataBytes = 0UL;
        for (var index = start; index < start + count; index++)
        {
            var segment = segments[index];
            maximum = Math.Max(maximum, segment.DestinationOffset + segment.Bytes);
            largest = Math.Max(largest, segment.Bytes);
            dataBytes += 2UL * segment.Bytes;
        }

        var recordBytes = (ulong)count * SharedPageMergePlan.RecordBytes;
        var total = recordBytes + dataBytes;
        GpuBuffer input = _staging;
        var temporary = false;
        if (!_staging.TryMap(total, out var offset, sizeof(uint)))
        {
            input = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Upload, 0, BufferUsageFlags.StorageBufferBit, total);
            offset = 0;
            temporary = true;
        }

        var mapped = input.Mapped.Slice(checked((int)offset), checked((int)total));
        var records = MemoryMarshal.Cast<byte, uint>(mapped[..(int)recordBytes]);
        var data = plan.Data;
        var position = recordBytes;
        for (var index = 0; index < count; index++)
        {
            var segment = segments[start + index];
            var record = records.Slice(index * (int)SharpEmu.ShaderCompiler.Vulkan.TilerShaders.MergeSegmentWords,
                (int)SharpEmu.ShaderCompiler.Vulkan.TilerShaders.MergeSegmentWords);
            var bytes = (int)segment.Bytes;
            data.Slice(segment.NewIndex, bytes).CopyTo(mapped[(int)position..]);
            data.Slice(segment.OldIndex, bytes).CopyTo(mapped[(int)(position + (ulong)bytes)..]);
            record[0] = checked((uint)position);
            record[1] = checked((uint)(position + (ulong)bytes));
            record[2] = checked((uint)(segment.DestinationOffset - minimum));
            record[3] = segment.Bytes;
            position += 2UL * segment.Bytes;
            buffer.NoteGpuWrite(segment.DestinationOffset, segment.Bytes);
        }

        if (temporary)
        {
            input.Flush(0, total);
            _scheduler.QueueCompletionAction(input.Dispose);
        }
        else
        {
            _staging.Commit();
        }

        ChangedByteMerge!(
            new TilerBufferSpan(input.Handle, offset, total),
            new TilerBufferSpan(buffer.Handle, minimum, maximum - minimum),
            (uint)count,
            largest);
    }
}
