// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class AutoStickTests
{
    [Fact]
    public void Parse_ReadsWindowsAndDirections()
    {
        var windows = PadExports.ParseAutoStick("400-460=up; 470.5-500=up+right ;505-507=look-left");

        Assert.Equal(3, windows.Length);
        Assert.Equal(new PadExports.AutoStickWindow(400, 460, 0, -1, 0, 0), windows[0]);
        Assert.Equal(new PadExports.AutoStickWindow(470.5, 500, 1, -1, 0, 0), windows[1]);
        Assert.Equal(new PadExports.AutoStickWindow(505, 507, 0, 0, -1, 0), windows[2]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("400=up")]
    [InlineData("460-400=up")]
    [InlineData("400-460=forward")]
    [InlineData("a-b=up")]
    public void Parse_SkipsMalformedEntries(string? raw)
    {
        Assert.Empty(PadExports.ParseAutoStick(raw));
    }

    [Fact]
    public void Apply_MovesOnlyTheAxesOfOpenWindows()
    {
        var windows = PadExports.ParseAutoStick("10-20=up;15-25=right;30-40=look-down");
        byte leftX = 128, leftY = 128, rightX = 128, rightY = 128;

        PadExports.ApplyAutoStick(windows, 5, ref leftX, ref leftY, ref rightX, ref rightY);
        Assert.Equal((128, 128, 128, 128), (leftX, leftY, rightX, rightY));

        PadExports.ApplyAutoStick(windows, 16, ref leftX, ref leftY, ref rightX, ref rightY);
        Assert.Equal((255, 0, 128, 128), (leftX, leftY, rightX, rightY));

        leftX = leftY = 128;
        PadExports.ApplyAutoStick(windows, 35, ref leftX, ref leftY, ref rightX, ref rightY);
        Assert.Equal((128, 128, 128, 255), (leftX, leftY, rightX, rightY));
    }

    [Fact]
    public void Apply_OpposingWindowsCancelAndLeaveTheAxis()
    {
        var windows = PadExports.ParseAutoStick("0-10=up;0-10=down");
        byte leftX = 128, leftY = 77, rightX = 128, rightY = 128;

        PadExports.ApplyAutoStick(windows, 5, ref leftX, ref leftY, ref rightX, ref rightY);

        Assert.Equal(77, leftY);
    }

    [Fact]
    public void Apply_EndIsExclusive()
    {
        var windows = PadExports.ParseAutoStick("10-20=left");
        byte leftX = 128, leftY = 128, rightX = 128, rightY = 128;

        PadExports.ApplyAutoStick(windows, 20, ref leftX, ref leftY, ref rightX, ref rightY);

        Assert.Equal(128, leftX);
    }
}
