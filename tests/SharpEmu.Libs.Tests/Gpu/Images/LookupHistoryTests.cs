// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class LookupHistoryTests
{
    [Theory]
    [InlineData(null, 1024)]
    [InlineData("", 1024)]
    [InlineData("abc", 1024)]
    [InlineData("8", 1024)]
    [InlineData("100", 1024)]
    [InlineData("131072", 1024)]
    [InlineData("16", 16)]
    [InlineData("64", 64)]
    [InlineData("4096", 4096)]
    [InlineData("65536", 65536)]
    public void HistoryDefaultsOrAcceptsPowerOfTwoOverride(string? value, int expected) =>
        Assert.Equal(expected, GuestImageCache.ParseLookupHistory(value));
}
