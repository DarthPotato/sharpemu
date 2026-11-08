// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvDifferentialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterpreterRejectsObservedUndefinedValuesAndIgnoresUnselectedFallbacks(bool selectDefined)
    {
        var m = new SpirvModuleBuilder { Optimize = false };
        var u = m.TypeInt(32, false);
        var output = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Output, u), SpirvStorageClass.Output);
        m.AddDecoration(output, SpirvDecoration.Location, 0);
        var main = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid())); m.AddLabel();
        var fallback = m.AddInstruction(SpirvOp.Load, u, output);
        var selected = m.AddInstruction(SpirvOp.Select, u, m.ConstantBool(selectDefined), m.Constant(u, 7), fallback);
        m.AddStatement(SpirvOp.Store, output, selected); m.AddStatement(SpirvOp.Return); m.EndFunction();
        m.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", [output]);
        var cpu = new SpirvCpu(m.Build());
        if (selectDefined) Assert.Contains("0000000000000007", cpu.Execute(0).Values);
        else Assert.Throws<NotSupportedException>(() => cpu.Execute(0));
    }

    [Fact]
    public void InterpreterCollectsActualPredicatesFromAll32Lanes()
    {
        var result = new SpirvCpu(BallotProgram(false)).Execute(0);
        var outputs = result.Where(p => !p.Key.EndsWith("killed")).ToArray();
        Assert.Equal(32, outputs.Length);
        Assert.All(outputs, output => Assert.Equal("0000000000001FFF", output.Value));
    }

    [Fact]
    public void InterpreterRejectsPartiallyParticipatingCollectives()
    {
        Assert.Throws<NotSupportedException>(() => new SpirvCpu(BallotProgram(true)).Execute(0));
    }

    private static byte[] BallotProgram(bool divergent)
    {
        var m = new SpirvModuleBuilder { Optimize = false };
        var u = m.TypeInt(32, false);
        var input = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, u), SpirvStorageClass.Input);
        var output = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Output, u), SpirvStorageClass.Output);
        m.AddDecoration(input, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
        m.AddDecoration(output, SpirvDecoration.Location, 0);
        var main = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid())); m.AddLabel();
        var predicate = m.AddInstruction(SpirvOp.ULessThan, m.TypeBool(), m.AddInstruction(SpirvOp.Load, u, input), m.Constant(u, 13));
        uint merge = 0;
        if (divergent)
        {
            var body = m.AllocateId(); merge = m.AllocateId();
            m.AddStatement(SpirvOp.SelectionMerge, merge, 0);
            m.AddStatement(SpirvOp.BranchConditional, predicate, body, merge); m.AddLabel(body);
        }
        var ballot = m.AddInstruction(SpirvOp.GroupNonUniformBallot, m.TypeVector(u, 4), m.Constant(u, 3), predicate);
        m.AddStatement(SpirvOp.Store, output, m.AddInstruction(SpirvOp.CompositeExtract, u, ballot, 0));
        if (divergent) { m.AddStatement(SpirvOp.Branch, merge); m.AddLabel(merge); }
        m.AddStatement(SpirvOp.Return); m.EndFunction();
        m.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", [input, output]);
        return m.Build();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RandomRegisterProgramsPreserveOutputsAcrossBranchesAndLoops(bool inherit)
    {
        for (var programSeed = 0; programSeed < 64; programSeed++)
        {
            var reference = new SpirvCpu(Program(programSeed, false, inherit));
            var optimized = new SpirvCpu(Program(programSeed, true, inherit));
            for (var input = 0; input < 16; input++)
                Assert.True(reference.Execute(input).SequenceEqual(optimized.Execute(input)), $"program={programSeed}, input={input}, inherit={inherit}");
        }
    }

    private static byte[] Program(int seed, bool optimize, bool inherit)
    {
        var random = new Random(seed);
        var m = new SpirvModuleBuilder { Optimize = optimize, InheritBlockState = inherit };
        m.AddCapability(SpirvCapability.Shader);
        var u = m.TypeInt(32, false);
        uint C(uint x) => m.Constant(u, x);
        var input = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, u), SpirvStorageClass.Input);
        var output = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Output, u), SpirvStorageClass.Output);
        m.AddDecoration(input, SpirvDecoration.Location, 0);
        m.AddDecoration(output, SpirvDecoration.Location, 0);
        var array = m.TypeArray(u, 4);
        var registers = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Private, array), SpirvStorageClass.Private, m.ConstantNull(array));
        var scalarPointer = m.TypePointer(SpirvStorageClass.Private, u);
        var counter = m.AddGlobalVariable(scalarPointer, SpirvStorageClass.Private, C(0));
        var main = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid()));
        m.AddLabel();
        var data = m.AddInstruction(SpirvOp.Load, u, input);
        uint Pointer(uint index) => m.AddInstruction(SpirvOp.AccessChain, scalarPointer, registers, index);
        uint Read(uint index) => m.AddInstruction(SpirvOp.Load, u, Pointer(C(index)));
        void Work()
        {
            for (var i = 0; i < 10; i++)
            {
                var index = (uint)random.Next(4);
                var other = Read((uint)random.Next(4));
                var value = m.AddInstruction(random.Next(3) switch { 0 => SpirvOp.IAdd, 1 => SpirvOp.BitwiseXor, _ => SpirvOp.IMul }, u, other, data);
                m.AddStatement(SpirvOp.Store, Pointer(C(index)), value);
                if (random.Next(4) == 0)
                    m.AddStatement(SpirvOp.Store, registers, m.ConstantComposite(array, C((uint)random.Next()), C(2), C(3), C(4)));
            }
        }
        Work();
        var yes = m.AllocateId(); var no = m.AllocateId(); var merge = m.AllocateId();
        var predicate = m.AddInstruction(SpirvOp.INotEqual, m.TypeBool(), m.AddInstruction(SpirvOp.BitwiseAnd, u, data, C(1)), C(0));
        m.AddStatement(SpirvOp.SelectionMerge, merge, 0);
        m.AddStatement(SpirvOp.BranchConditional, predicate, yes, no);
        m.AddLabel(yes); Work(); m.AddStatement(SpirvOp.Branch, merge);
        m.AddLabel(no); Work(); m.AddStatement(SpirvOp.Branch, merge);
        m.AddLabel(merge);
        var header = m.AllocateId(); var body = m.AllocateId(); var next = m.AllocateId(); var exit = m.AllocateId();
        m.DeclareLoopHeader(header);
        m.AddStatement(SpirvOp.Branch, header);
        m.AddLabel(header);
        var iteration = m.AddInstruction(SpirvOp.Load, u, counter);
        var more = m.AddInstruction(SpirvOp.ULessThan, m.TypeBool(), iteration, C(3));
        m.AddStatement(SpirvOp.LoopMerge, exit, next, 0);
        m.AddStatement(SpirvOp.BranchConditional, more, body, exit);
        m.AddLabel(body); Work(); m.AddStatement(SpirvOp.Branch, next);
        m.AddLabel(next);
        m.AddStatement(SpirvOp.Store, counter, m.AddInstruction(SpirvOp.IAdd, u, iteration, C(1)));
        m.AddStatement(SpirvOp.Branch, header);
        m.AddLabel(exit);
        // A dynamic alias escapes the array after several tracked stores/loads.
        var dynamicIndex = m.AddInstruction(SpirvOp.BitwiseAnd, u, data, C(3));
        m.AddStatement(SpirvOp.Store, Pointer(dynamicIndex), data);
        var digest = Read(0);
        for (uint i = 1; i < 4; i++) digest = m.AddInstruction(SpirvOp.BitwiseXor, u, digest, Read(i));
        m.AddStatement(SpirvOp.Store, output, digest);
        m.AddStatement(SpirvOp.Return);
        m.EndFunction();
        m.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", [input, output, registers, counter]);
        return m.Build();
    }
}
