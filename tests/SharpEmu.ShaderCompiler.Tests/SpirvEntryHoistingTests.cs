// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvEntryHoistingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void OwnLaneBallotRetainsParticipationForQuadConsumers(bool inherit, bool guarded, bool equalZero)
    {
        foreach (var helpers in new[] { 0u, 0xAAAAAAAAu, 0xCCCCCCCCu, 0x88888888u })
        {
            var reference = new SpirvCpu(QuadBallot(false, inherit, guarded, equalZero), helpers).Execute(0);
            var optimized = new SpirvCpu(QuadBallot(true, inherit, guarded, equalZero), helpers).Execute(0);
            Assert.Equal(reference.ToArray(), optimized.ToArray());
        }
    }

    private static byte[] QuadBallot(bool hoist, bool inherit, bool guarded, bool equalZero)
    {
        var m = new SpirvModuleBuilder { Optimize = true, HoistEntry = hoist, InheritBlockState = inherit };
        m.AddCapability(SpirvCapability.Shader);
        m.AddCapability(SpirvCapability.GroupNonUniform);
        m.AddCapability(SpirvCapability.GroupNonUniformBallot);
        m.AddCapability((SpirvCapability)68); // GroupNonUniformQuad
        var u = m.TypeInt(32, false);
        var input = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, u), SpirvStorageClass.Input);
        m.AddDecoration(input, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
        m.AddDecoration(input, SpirvDecoration.Flat);
        var output = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Output, u), SpirvStorageClass.Output);
        m.AddDecoration(output, SpirvDecoration.Location, 0);
        var main = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid()));
        m.AddLabel();
        var lane = m.AddInstruction(SpirvOp.Load, u, input);
        var bit = m.AddInstruction(SpirvOp.ShiftLeftLogical, u, m.Constant(u, 1),
            guarded ? m.AddInstruction(SpirvOp.BitwiseAnd, u, lane, m.Constant(u, 31)) : lane);
        if (guarded) bit = m.AddInstruction(SpirvOp.Select, u,
            m.AddInstruction(SpirvOp.ULessThan, m.TypeBool(), lane, m.Constant(u, 32)), bit, m.Constant(u, 0));
        var ballot = m.AddInstruction(SpirvOp.GroupNonUniformBallot, m.TypeVector(u, 4), m.Constant(u, 3), m.ConstantBool(true));
        var word = m.AddInstruction(SpirvOp.CompositeExtract, u, ballot, 0);
        var test = m.AddInstruction(equalZero ? SpirvOp.IEqual : SpirvOp.INotEqual, m.TypeBool(),
            m.AddInstruction(SpirvOp.BitwiseAnd, u, word, bit), m.Constant(u, 0));
        var value = m.AddInstruction(SpirvOp.Select, u, test, m.Constant(u, 1), m.Constant(u, 0));
        var neighbor = m.AddInstruction((SpirvOp)366, u, m.Constant(u, 3), value, m.Constant(u, 0));
        m.AddStatement(SpirvOp.Store, output, neighbor);
        m.AddStatement(SpirvOp.Return);
        m.EndFunction();
        m.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [input, output]);
        m.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return m.Build();
    }

    [Fact]
    public void StableInputLoadsAndArithmeticStillHoistAcrossBlocks()
    {
        var m = new SpirvModuleBuilder { Optimize = true, HoistEntry = true, InheritBlockState = false };
        var u = m.TypeInt(32, false);
        var input = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, u), SpirvStorageClass.Input);
        m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid())); m.AddLabel();
        var loaded = m.AddInstruction(SpirvOp.Load, u, input);
        var sum = m.AddInstruction(SpirvOp.IAdd, u, loaded, m.Constant(u, 7));
        var next = m.AllocateId(); m.AddStatement(SpirvOp.Branch, next); m.AddLabel(next);
        Assert.Equal(loaded, m.AddInstruction(SpirvOp.Load, u, input));
        Assert.Equal(sum, m.AddInstruction(SpirvOp.IAdd, u, loaded, m.Constant(u, 7)));
    }
}
