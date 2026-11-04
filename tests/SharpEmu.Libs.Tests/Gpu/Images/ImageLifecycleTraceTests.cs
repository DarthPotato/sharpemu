// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class ImageLifecycleTraceTests
{
    [Theory]
    [InlineData("0x9487580000")]
    [InlineData(" 9487580000 ")]
    public void SingleAddress_SelectsContainingImage(string value)
    {
        var range = ImageLifecycleTrace.Parse(value)!.Value;
        Assert.True(range.Overlaps(0x948757F000, 0x2000));
        Assert.True(range.Overlaps(0x9487580000, 1));
        Assert.False(range.Overlaps(0x948757F000, 0x1000));
        Assert.False(range.Overlaps(0x9487580001, 0x2000));
        Assert.False(range.Overlaps(0x9487580000, 0));
    }

    [Fact]
    public void Range_UsesExclusiveEndAndHandlesMaximumAddress()
    {
        var range = ImageLifecycleTrace.Parse(" 0x1000 - 0X2000 ")!.Value;
        Assert.True(range.Overlaps(0x800, 0x1000));
        Assert.True(range.Overlaps(0x1FFF, 1));
        Assert.False(range.Overlaps(0x2000, 1));
        Assert.False(range.Overlaps(0, 0x1000));
        var lastByte = ImageLifecycleTrace.Parse("FFFFFFFFFFFFFFFF")!.Value;
        Assert.True(lastByte.Overlaps(ulong.MaxValue, 1));
        Assert.False(lastByte.Overlaps(ulong.MaxValue - 1, 1));
        Assert.True(lastByte.Overlaps(ulong.MaxValue - 1, 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1000-1000")]
    [InlineData("2000-1000")]
    [InlineData("1000-")]
    [InlineData("1000-2000-3000")]
    [InlineData("10000000000000000")]
    [InlineData("garbage")]
    public void Parse_DoesNotEnableBroadTracingForInvalidInput(string? value)
    {
        // "1" is a hex byte address, not a boolean switch selecting every image.
        var range = ImageLifecycleTrace.Parse(value);
        if (value == "1") Assert.Equal(new ImageLifecycleTrace.Range(1, 1), range);
        else Assert.Null(range);
    }
}
