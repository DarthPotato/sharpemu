// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

public readonly record struct BufferSliceUse(ulong Address, ulong Size, bool Written, bool Texel, bool InvalidateImages, GpuBuffer Buffer,
    bool RequiresDeviceAddress = false);

public readonly record struct BufferDeviceAddressLease(ulong Address, ulong Size, GpuBuffer Buffer,
    ResourceSlotIdentifier Identifier, ulong DeviceAddress);

// A frozen view of the registered buffers for recording threads that run ahead of the stream order,
// rebuilt by the render thread whenever a registration changed.
public sealed class BufferSnapshot
{
    public static readonly BufferSnapshot Empty = new([], [], [], [], -1);

    private readonly ulong[] _addresses;
    private readonly ulong[] _ends;
    private readonly GpuBuffer[] _buffers;
    private readonly ResourceSlotIdentifier[] _identifiers;

    internal BufferSnapshot(ulong[] addresses, ulong[] ends, GpuBuffer[] buffers, ResourceSlotIdentifier[] identifiers, long serial)
    {
        _addresses = addresses;
        _ends = ends;
        _buffers = buffers;
        _identifiers = identifiers;
        Serial = serial;
    }

    public long Serial { get; }

    public int Count => _addresses.Length;

    public string MissingRangeReason(ulong address, ulong size)
    {
        if (address == 0 || size == 0 || size > ulong.MaxValue - address) return "other";
        var overlaps = 0;
        for (var i = 0; i < Count; i++)
            if (_addresses[i] < address + size && address < _ends[i]) overlaps++;
        return overlaps == 0 ? "allocate" : overlaps == 1 ? "grow" : "merge";
    }

    // Match serial preparation's page rounding and adjacent-range coalescing.
    // A union spanning owners would change earlier physical addresses.
    public bool StableAllocationRanges(ReadOnlySpan<SharpEmu.HLE.GpuMemory.GuestSpan> ranges,
        out SharpEmu.HLE.GpuMemory.GuestSpan[] pages, out string reason)
    {
        pages = new SharpEmu.HLE.GpuMemory.GuestSpan[ranges.Length];
        reason = "other";
        var mask = GuestBufferCache.CachingPageSize - 1;
        for (var i = 0; i < ranges.Length; i++)
        {
            var r = ranges[i];
            if (r.Address == 0 || r.Size == 0 || r.Address > ulong.MaxValue - mask ||
                r.Size > ulong.MaxValue - mask - r.Address) return false;
            var start = r.Address & ~mask;
            pages[i] = new(start, ((r.End + mask) & ~mask) - start);
        }
        Array.Sort(pages, static (a, b) => a.Address.CompareTo(b.Address));
        for (var i = 0; i < pages.Length;)
        {
            var r = pages[i];
            var next = i + 1;
            while (next < pages.Length && pages[next].Address <= r.End)
            {
                r = new(r.Address, Math.Max(r.End, pages[next].End) - r.Address);
                next++;
            }
            if (!TryFind(r.Address, r.Size, out _, out _))
            {
                reason = MissingRangeReason(r.Address, r.Size);
                return false;
            }
            i = next;
        }
        return true;
    }

    public bool TryFind(ulong address, ulong size, out GpuBuffer buffer, out ResourceSlotIdentifier identifier)
    {
        buffer = null!;
        identifier = ResourceSlotIdentifier.Invalid;
        var low = 0;
        var high = _addresses.Length - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (_addresses[middle] <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0 || address + size > _ends[found] || address + size < address)
        {
            return false;
        }

        buffer = _buffers[found];
        identifier = _identifiers[found];
        return true;
    }
}

public sealed unsafe partial class GuestBufferCache
{
    private BufferSnapshot _snapshot = BufferSnapshot.Empty;

    public BufferSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public BufferSnapshot RefreshSnapshot()
    {
        var serial = _registry.RegistrationSerial;
        var current = _snapshot;
        if (current.Serial == serial)
        {
            return current;
        }

        var count = _registry.RegisteredCount;
        var addresses = new ulong[count];
        var ends = new ulong[count];
        var buffers = new GpuBuffer[count];
        var identifiers = new ResourceSlotIdentifier[count];
        for (var index = 0; index < count; index++)
        {
            var identifier = _registry.GetRegisteredIdentifier(index);
            var buffer = _registry.GetBuffer(identifier);
            addresses[index] = buffer.CpuAddress;
            ends[index] = buffer.CpuAddress + buffer.Size;
            buffers[index] = buffer;
            identifiers[index] = identifier;
        }

        var snapshot = new BufferSnapshot(addresses, ends, buffers, identifiers, serial);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    // Whether today's obtain would copy the range into the stream ring instead of binding the cache.
    public bool PrefersStreamCopy(ulong guestAddress, ulong size, bool isTexelBuffer) =>
        _tracker.HasCpuDirtyPages(guestAddress, size) &&
        !(_tracker.MayHaveGpuDirtyPages(guestAddress, size) && _tracker.HasGpuDirtyPages(guestAddress, size)) &&
        (size <= CachingPageSize || (!isTexelBuffer && _tracker.IsCpuWriteHotRange(guestAddress, size)));

    public bool MayHaveImageBytes(ulong guestAddress, ulong size) => RequireImageCache().MayHaveImageBytes(guestAddress, size);

    public bool MayHaveCpuDirtyPages(ulong guestAddress, ulong size) => _tracker.MayHaveCpuDirtyPages(guestAddress, size);

    public bool IsDeviceAddressRangeResident(ulong address, ulong size) =>
        IsValidRange(address, size) && _backing.IsBackedRange(address, size);

    public long RegionStamp(ulong guestAddress, ulong size) => _registry.RegionStamp(guestAddress, size);

    public bool TryGetCleanWriteStamp(ulong address, ulong size, out long stamp) =>
        _tracker.TryGetCleanWriteStamp(address, size, out stamp);

    internal void NoteExternalWriteForInspection(ulong address, ulong size) =>
        _tracker.NoteExternalWriteForInspection(address, size);

    // Whether TryCopyWordsOnHost would copy, without copying.
    public bool WouldCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words)
    {
        if (destination == 0 || source == 0 || sourceWords == 0 || words == 0 || ((destination | source) & 3) != 0 ||
            words > MaxHostCopyWords || sourceWords > MaxHostCopyWords)
        {
            return false;
        }

        var size = words * sizeof(uint);
        var sourceSize = Math.Min(sourceWords, words) * sizeof(uint);
        if (size > ulong.MaxValue - destination || sourceSize > ulong.MaxValue - source ||
            (source < destination + size && destination < source + sourceSize))
        {
            return false;
        }

        var images = RequireImageCache();
        return !HasGpuDirtyBytes(source, sourceSize) && !HasGpuDirtyBytes(destination, size) &&
               !images.QueryRegion(source, sourceSize).GpuImageBytes && !images.QueryRegion(destination, size).GpuImageBytes;
    }

    public bool ValidateSliceBuffers(List<BufferSliceUse> uses)
    {
        foreach (var use in uses)
        {
            var owner = _registry.TryGetRegisteredBuffer(_registry.FindContainingBuffer(use.Address, use.Size));
            if (!ReferenceEquals(owner, use.Buffer) || !use.Buffer.IsInBounds(use.Address, use.Size))
            {
                return false;
            }
        }

        return true;
    }

    public bool ValidateDeviceAddressLease(in BufferDeviceAddressLease lease) =>
        ReferenceEquals(_registry.TryGetRegisteredBuffer(lease.Identifier), lease.Buffer) &&
        ReferenceEquals(_registry.TryGetRegisteredBuffer(_registry.FindContainingBuffer(lease.Address, lease.Size)), lease.Buffer) &&
        lease.Buffer.IsInBounds(lease.Address, lease.Size) && lease.Buffer.DeviceAddress == lease.DeviceAddress;

    public void ApplySliceUse(in BufferSliceUse use)
    {
        _ = ObtainBuffer(use.Address, use.Size, use.Written, use.Texel, allowStream: false, requiresDeviceAddress: use.RequiresDeviceAddress);
        if (use.InvalidateImages)
        {
            RequireImageCache().InvalidateMemoryFromGpu(use.Address, use.Size);
        }
        else if (use.Written)
        {
            RequireImageCache().InvalidateMemoryCopiesFromGpu(use.Address, use.Size);
        }
    }
}
