// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The page granularity and address width every emitter uses for device addresses.
public static class DeviceAddressPaging
{
    // Guest pages are mapped to device buffers at this granularity; the host's cache uses the same.
    public const int PageBits = 14;
    public const ulong PageSize = 1UL << PageBits;
    public const ulong PageOffsetMask = PageSize - 1;

    // Address handles carry 48 bits; the upper bits are ignored.
    public const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    // The fault buffer holds, in order: the bitmap of pages a shader needed but found unmapped,
    // the bitmap of pages a shader wrote through a device address (one bit per page each), the
    // written-granule masks, and a 64-byte diagnostic record at the end. A written page also sets
    // the bit of each 512-byte granule it wrote in the mask of slot (page index % slots): pages
    // that share a slot share their bits, so a page's mask holds at least the granules it wrote.
    public const int WriteGranuleBits = 9;
    public const int WriteGranulesPerPage = 1 << (PageBits - WriteGranuleBits);
    public const uint WriteGranuleSlots = 1u << 16;
    public const ulong WriteGranuleTableBytes = WriteGranuleSlots * sizeof(uint);
}
