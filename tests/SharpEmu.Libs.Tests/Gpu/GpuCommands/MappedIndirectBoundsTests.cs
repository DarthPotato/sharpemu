// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class MappedIndirectBoundsTests
{
    [Theory]
    [InlineData(2ul, 8193ul, 4096u)]
    [InlineData(4ul, 8193ul, 2048u)]
    [InlineData(4ul, 0ul, 0u)]
    [InlineData(4ul, 67108864ul, 16777216u)]
    [InlineData(4ul, 67108868ul, 0u)]
    public void UsesOnlyTheCompleteMappedPrefix(ulong element, ulong mapped, uint expected)
    {
        var calls = 0;
        var count = GpuCommandInterpreter.MappedIndexCount(0x10000, element, (address, requested) =>
        {
            calls++;
            Assert.Equal(0x10000ul, address);
            Assert.Equal((64ul << 20) + element, requested);
            return mapped;
        });
        Assert.Equal(expected, count);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(0x10001ul)]
    [InlineData(18446744073709551612ul)]
    public void InvalidRangesDoNotQueryMappings(ulong address)
    {
        Assert.Equal(0u, GpuCommandInterpreter.MappedIndexCount(address, 4, (_, _) => throw new Exception("Unexpected query")));
    }
}
