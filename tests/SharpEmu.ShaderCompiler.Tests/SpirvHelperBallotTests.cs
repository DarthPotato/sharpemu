// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvHelperBallotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnLaneBallotTestPreservesHelperValuesUsedByQuadNeighbors(bool inherit)
    {
        var reference = new SpirvCpu(CreateFragment(false, inherit), 0xAAAAAAAA).Execute(0);
        var optimized = new SpirvCpu(CreateFragment(true, inherit), 0xAAAAAAAA).Execute(0);
        Assert.True(reference.SequenceEqual(optimized),
            $"Reference: {string.Join(';', reference)}; optimized: {string.Join(';', optimized)}");
    }

    public static byte[] CreateFragment(bool hoist, bool inherit, bool bias = false)
    {
        var m = new SpirvModuleBuilder { Optimize = true, HoistEntry = hoist, InheritBlockState = inherit };
        m.AddCapability(SpirvCapability.Shader);
        m.AddCapability(SpirvCapability.GroupNonUniform);
        m.AddCapability(SpirvCapability.GroupNonUniformBallot);
        var u = m.TypeInt(32, false);
        var f = m.TypeFloat(32);
        var laneInput = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, u), SpirvStorageClass.Input);
        m.AddDecoration(laneInput, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
        m.AddDecoration(laneInput, SpirvDecoration.Flat);
        var output = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Output, f), SpirvStorageClass.Output);
        m.AddDecoration(output, SpirvDecoration.Location, 0);
        var main = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid()));
        m.AddLabel();
        var lane = m.AddInstruction(SpirvOp.Load, u, laneInput);
        var bit = m.AddInstruction(SpirvOp.ShiftLeftLogical, u, m.Constant(u, 1), lane);
        var ballot = m.AddInstruction(SpirvOp.GroupNonUniformBallot, m.TypeVector(u, 4), m.Constant(u, 3), m.ConstantBool(true));
        var low = m.AddInstruction(SpirvOp.CompositeExtract, u, ballot, 0);
        var test = m.AddInstruction(SpirvOp.INotEqual, m.TypeBool(), m.AddInstruction(SpirvOp.BitwiseAnd, u, low, bit), m.Constant(u, 0));
        var value = m.AddInstruction(SpirvOp.Select, f, test, m.ConstantFloat(f, 1), m.ConstantFloat(f, 0));
        var derivative = m.AddInstruction((SpirvOp)210, f, value); // OpDPdxFine observes the helper on the right.
        if (bias) derivative = m.AddInstruction(SpirvOp.FAdd, f, derivative, m.ConstantFloat(f, 1));
        m.AddStatement(SpirvOp.Store, output, derivative);
        m.AddStatement(SpirvOp.Return);
        m.EndFunction();
        m.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [laneInput, output]);
        m.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return m.Build();
    }
}
