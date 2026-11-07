// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadInputScriptTests
{
    private static readonly PadState Neutral = new(true, 0, 128, 128, 128, 128, 0, 0);
    private static PadInputScript Parse(string text)
    {
        Assert.True(PadInputScript.TryParse(text, out var script, out var error), error);
        return script!;
    }

    [Theory]
    [InlineData(59.999, 0u, 128, 128)]
    [InlineData(60, OrbisPadButton.Cross, 128, 128)]
    [InlineData(60.149, OrbisPadButton.Cross, 128, 128)]
    [InlineData(60.15, 0u, 128, 128)]
    [InlineData(90, 0u, 0, 128)]
    [InlineData(150, 0u, 0, 255)]
    [InlineData(170, OrbisPadButton.Cross, 128, 128)]
    [InlineData(175, 0u, 128, 128)]
    public void ExampleHasEndExclusiveWindows(double seconds, uint buttons, byte ly, byte rx)
    {
        var state = Parse("60:cross;90-150:ls_up;150-170:ls_up+rs_right;170-175:cross").Merge(Neutral, seconds);
        Assert.Equal(buttons, state.Buttons);
        Assert.Equal(ly, state.LeftY);
        Assert.Equal(rx, state.RightX);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1:cross;")]
    [InlineData("1:cross;bad")]
    [InlineData("-1:cross")]
    [InlineData("NaN:cross")]
    [InlineData("Infinity:cross")]
    [InlineData("1-Infinity:cross")]
    [InlineData("1-1:cross")]
    [InlineData("2-1:cross")]
    [InlineData("1,5:cross")]
    [InlineData("1:")]
    [InlineData("1:cross+")]
    [InlineData("1:cross+typo")]
    [InlineData("1:cross:circle")]
    public void AnyParseErrorRejectsTheEntireScript(string text)
    {
        Assert.False(PadInputScript.TryParse(text, out var script, out var error));
        Assert.Null(script);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void MergesWithUnfocusedControllerAndPreservesUnscriptedAxesAndMetadata()
    {
        var controller = Neutral with { Buttons = OrbisPadButton.Circle, LeftX = 200, LeftY = 240,
            RightY = 25, R2 = 70, ControllerButtons = OrbisPadButton.Circle, HostWindowFocused = false };
        var script = Parse(" 1.5-3 : CROSS + l2 + ls_up + rs_right ");
        var merged = script.Merge(controller, 2);
        Assert.Equal(OrbisPadButton.Circle | OrbisPadButton.Cross | OrbisPadButton.L2, merged.Buttons);
        Assert.Equal((byte)255, merged.L2);
        Assert.Equal((byte)70, merged.R2);
        Assert.Equal((byte)200, merged.LeftX);
        Assert.Equal((byte)0, merged.LeftY);
        Assert.Equal((byte)255, merged.RightX);
        Assert.Equal((byte)25, merged.RightY);
        Assert.False(merged.HostWindowFocused);
        Assert.Equal(controller.ControllerButtons, merged.ControllerButtons);
        Assert.Equal(controller, script.Merge(controller, 3));
    }

    [Fact]
    public void SupportsAllButtonsAndStickDirections()
    {
        var state = Parse("0-1:cross+circle+square+triangle+l1+r1+l2+r2+options+up+down+left+right+touchpad+ls_down+ls_left+rs_up+rs_left")
            .Merge(Neutral, 0);
        Assert.Equal(0x10FFF8u, state.Buttons);
        Assert.Equal((byte)255, state.L2);
        Assert.Equal((byte)255, state.R2);
        Assert.Equal((byte)0, state.LeftX);
        Assert.Equal((byte)255, state.LeftY);
        Assert.Equal((byte)0, state.RightX);
        Assert.Equal((byte)0, state.RightY);
        state = Parse("0-1:ls_right+rs_down").Merge(Neutral, 0);
        Assert.Equal((byte)255, state.LeftX);
        Assert.Equal((byte)255, state.RightY);
    }

    [Fact]
    public void OverlapsCombineAndOpposingSegmentsCenterTheAxis()
    {
        var state = Parse("1-3:cross+ls_up;2-4:circle+ls_down").Merge(Neutral, 2);
        Assert.Equal(OrbisPadButton.Cross | OrbisPadButton.Circle, state.Buttons);
        Assert.Equal((byte)128, state.LeftY);
        Assert.Equal((byte)128, Parse("1-3:ls_up+ls_down+ls_up").Merge(Neutral, 2).LeftY);
    }

    [Fact]
    public void ConcurrentReadersLogEachSegmentOnlyOnceIncludingMissedWindows()
    {
        var messages = new ConcurrentBag<string>();
        var script = Parse("1:cross;2-3:ls_up");
        Parallel.For(0, 100, _ => script.Merge(Neutral, 2.5, messages.Add));
        Assert.Equal(2, messages.Count);
        Assert.Single(messages, message => message.Contains("missed=True"));
        Assert.All(messages, message => Assert.StartsWith("[INPUT][SCRIPT]", message));
    }
}
