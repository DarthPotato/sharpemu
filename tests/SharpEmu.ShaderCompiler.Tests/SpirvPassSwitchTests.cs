// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvPassSwitchTests
{
    [Fact]
    public void ForwardingCanBeDisabledIndependently()
    {
        var (m, u, register, _) = Module(new() { ForwardRegisters = false });
        var value = m.Constant(u, 7);
        m.AddStatement(SpirvOp.Store, register, value);
        Assert.NotEqual(value, m.AddInstruction(SpirvOp.Load, u, register));
    }

    [Fact]
    public void StoreElisionCanBeDisabledIndependently()
    {
        var (m, u, register, _) = Module(new() { ElideStores = false, RemoveUnread = false });
        m.AddStatement(SpirvOp.Store, register, m.Constant(u, 7));
        m.AddStatement(SpirvOp.Store, register, m.Constant(u, 7));
        m.AddStatement(SpirvOp.Store, register, m.Constant(u, 8));
        Finish(m);
        Assert.Equal(3, Ops(m.Build()).Count(op => op == SpirvOp.Store));
    }

    [Fact]
    public void UnreadStorageCanBePreservedIndependently()
    {
        var (m, u, register, _) = Module(new() { RemoveUnread = false });
        m.AddStatement(SpirvOp.Store, register, m.Constant(u, 7));
        Finish(m);
        Assert.Contains(SpirvOp.Store, Ops(m.Build()));
    }

    [Fact]
    public void DeadDefinitionsCanBePreservedIndependently()
    {
        var (m, u, _, input) = Module(new() { RemoveDeadCode = false });
        m.AddInstruction(SpirvOp.UDiv, u, m.AddInstruction(SpirvOp.Load, u, input), m.Constant(u, 3));
        Finish(m);
        Assert.Contains(SpirvOp.UDiv, Ops(m.Build()));
    }

    [Fact]
    public void HoistingCanBeDisabledIndependently()
    {
        var (m, u, _, input) = Module(new() { HoistEntry = false });
        var read = m.AddInstruction(SpirvOp.Load, u, input);
        var next = m.AllocateId();
        m.AddStatement(SpirvOp.Branch, next); m.AddLabel(next);
        Assert.NotEqual(read, m.AddInstruction(SpirvOp.Load, u, input));
    }

    [Fact]
    public void InheritanceCanBeDisabledIndependently()
    {
        var (m, u, register, _) = Module(new() { InheritBlockState = false });
        var value = m.Constant(u, 7);
        m.AddStatement(SpirvOp.Store, register, value);
        var next = m.AllocateId();
        m.AddStatement(SpirvOp.Branch, next); m.AddLabel(next);
        Assert.NotEqual(value, m.AddInstruction(SpirvOp.Load, u, register));
    }

    [Fact]
    public void ExpressionFoldingAndReuseCanBeDisabledIndependently()
    {
        var (m, u, _, input) = Module(new() { FoldExpressions = false });
        var value = m.AddInstruction(SpirvOp.Load, u, input);
        var first = m.AddInstruction(SpirvOp.IAdd, u, value, m.Constant(u, 3));
        Assert.NotEqual(first, m.AddInstruction(SpirvOp.IAdd, u, value, m.Constant(u, 3)));
        Assert.NotEqual(m.Constant(u, 5), m.AddInstruction(SpirvOp.IAdd, u, m.Constant(u, 2), m.Constant(u, 3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaskWordSplittingHasItsOwnSwitch(bool enabled)
    {
        var (m, u, _, input) = Module(new() { FoldLaneMasks = enabled });
        var value = m.AddInstruction(SpirvOp.Load, u, input);
        var wide = m.TypeInt(64, false);
        var widened = m.AddInstruction(SpirvOp.UConvert, wide, value);
        var shifted = m.AddInstruction(SpirvOp.ShiftLeftLogical, wide, widened, m.Constant64(wide, 32));
        var mask = m.AddInstruction(SpirvOp.BitwiseOr, wide, widened, shifted);
        var low = m.AddInstruction(SpirvOp.UConvert, u, mask);
        Assert.Equal(enabled, low == value);
    }

    private static (SpirvModuleBuilder M, uint U, uint Register, uint Input) Module(SpirvModuleBuilder m)
    {
        var u = m.TypeInt(32, false);
        var register = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Private, u), SpirvStorageClass.Private);
        var input = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, u), SpirvStorageClass.Input);
        m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid())); m.AddLabel();
        return (m, u, register, input);
    }
    private static void Finish(SpirvModuleBuilder m) { m.AddStatement(SpirvOp.Return); m.EndFunction(); }
    private static List<SpirvOp> Ops(byte[] bytes)
    {
        var words = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        var result = new List<SpirvOp>();
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16)) result.Add((SpirvOp)(words[i] & 65535));
        return result;
    }
}
