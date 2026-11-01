// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    [ThreadStatic] internal static bool PublicationCutReplay;
    [ThreadStatic] internal static bool PublicationReplayPublished;
    private static long _publicationCutAttempts, _publicationCutReal, _publicationCutDownloads;
    private static long _publicationReplayItems, _publicationReplayRealItems;
    internal static void NotePublicationReplay()
    {
        Interlocked.Increment(ref _publicationReplayItems);
        if (PublicationReplayPublished) Interlocked.Increment(ref _publicationReplayRealItems);
    }
    private static string TakePublicationCutReport() => FormattableString.Invariant(
        $"publication_replay_items={Interlocked.Exchange(ref _publicationReplayItems, 0)} publication_replay_real_items={Interlocked.Exchange(ref _publicationReplayRealItems, 0)} publication_cut_attempts={Interlocked.Exchange(ref _publicationCutAttempts, 0)} publication_cut_real={Interlocked.Exchange(ref _publicationCutReal, 0)} publication_cut_downloads={Interlocked.Exchange(ref _publicationCutDownloads, 0)} ");
    private static readonly bool DccNonclearMemo = Environment.GetEnvironmentVariable("SHARPEMU_DCC_NONCLEAR_MEMO") == "1";
    private readonly record struct DccMemoKey(ulong Address, ulong Size, MetadataDescription Metadata, Format Format, bool AllowRegister);
    private sealed class DccMemoEntry(long writeStamp, long metadataGeneration)
    {
        internal readonly long WriteStamp = writeStamp;
        internal readonly long MetadataGeneration = metadataGeneration;
        internal int Invalid;
    }
    // Serial inserts under the image write lock; workers validate under its read lock.
    private readonly Dictionary<DccMemoKey, DccMemoEntry> _dccNonclear = new();
    private const int DccMemoLimit = 4096;

    private static DccMemoKey DccKey(in ImageRequest request, ulong address, ulong size) =>
        new(address, size, request.Description.Metadata, request.View.Format, request.Role == ImageRole.ColorTarget);

    private bool TryDccInspectionStamp(ulong address, ulong size, out long stamp)
    {
        stamp = 0;
        if (_readbackLandings.Overlaps(address, size)) return false;
        lock (_publishingDownloads)
        {
            foreach (var pending in _publishingDownloads)
                if (address < pending.Address + pending.Size && pending.Address < address + size) return false;
        }
        return _bufferCache.TryGetCleanWriteStamp(address, size, out stamp);
    }

    private bool ReuseDccNonclear(in ImageRequest request, ulong address, ulong size)
    {
        if (!DccNonclearMemo || !_dccNonclear.TryGetValue(DccKey(request, address, size), out var entry)) return false;
        if (Volatile.Read(ref entry.Invalid) != 0) return false;
        if (!MetadataUnchangedSince(entry.MetadataGeneration, request.Description.Metadata.Range.Address) ||
            !TryDccInspectionStamp(address, size, out var stamp) || stamp != entry.WriteStamp)
        {
            // The only worker mutation is this atomic, monotonic memo-validity bit.
            if (Interlocked.Exchange(ref entry.Invalid, 1) == 0) Interlocked.Increment(ref _dccMemoInvalidations);
            return false;
        }
        Interlocked.Increment(ref _dccMemoHits);
        return true;
    }

    private void RememberDccNonclear(in ImageRequest request, ulong address, ulong size, long before)
    {
        if (!DccNonclearMemo) return;
        using var held = _lock.Hold();
        if (!TryDccInspectionStamp(address, size, out var after) || before != after) return;
        if (_dccNonclear.Count >= DccMemoLimit) _dccNonclear.Clear();
        _dccNonclear[DccKey(request, address, size)] = new(after, _metadataGeneration);
    }
}
