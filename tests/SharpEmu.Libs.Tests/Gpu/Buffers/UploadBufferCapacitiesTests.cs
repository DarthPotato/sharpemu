// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class UploadBufferCapacitiesTests
{
    [Theory]
    [InlineData(null, 33554432UL)]
    [InlineData("", 33554432UL)]
    [InlineData("0", 33554432UL)]
    [InlineData("-1", 33554432UL)]
    [InlineData("garbage", 33554432UL)]
    [InlineData("18446744073709551616", 33554432UL)]
    [InlineData("1", 4096UL)]
    [InlineData("4096", 4096UL)]
    [InlineData("1048576", 1048576UL)]
    [InlineData("18446744073709551615", 268435456UL)]
    public void ArenaHalfValidatesAndClamps(string? value, ulong expected) =>
        Assert.Equal(expected, UploadBufferCapacities.Parse(value,
            UploadBufferCapacities.DefaultArenaHalfBytes, UploadBufferCapacities.MaximumArenaHalfBytes));

    [Theory]
    [InlineData("0", 67108864UL)]
    [InlineData("4096", 4096UL)]
    [InlineData("18446744073709551615", 536870912UL)]
    public void StreamUsesItsOwnDefaultAndMaximum(string value, ulong expected) =>
        Assert.Equal(expected, UploadBufferCapacities.Parse(value,
            UploadBufferCapacities.DefaultStreamBytes, UploadBufferCapacities.MaximumStreamBytes));
}
