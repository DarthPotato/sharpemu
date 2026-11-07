// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvOptimizerCorrectnessTests
{
    [Theory]
    [InlineData(SpirvOp.GroupNonUniformElect)]
    [InlineData(SpirvOp.GroupNonUniformAll)]
    [InlineData(SpirvOp.GroupNonUniformAny)]
    [InlineData(SpirvOp.GroupNonUniformAllEqual)]
    [InlineData(SpirvOp.GroupNonUniformBallot)]
    [InlineData(SpirvOp.GroupNonUniformBroadcast)]
    [InlineData(SpirvOp.GroupNonUniformBroadcastFirst)]
    [InlineData(SpirvOp.GroupNonUniformShuffle)]
    [InlineData(SpirvOp.GroupNonUniformShuffleXor)]
    [InlineData(SpirvOp.GroupNonUniformShuffleUp)]
    [InlineData(SpirvOp.GroupNonUniformShuffleDown)]
    [InlineData(SpirvOp.GroupNonUniformUMin)]
    public void LaneDependentResultsAreLocalToTheirBlock(SpirvOp opcode)
    {
        var module = new SpirvModuleBuilder();
        var integer = module.TypeInt(32, false);
        var scope = module.Constant(integer, 3);
        var value = module.Constant(integer, 17);
        var lane = module.Constant(integer, 0);
        var predicate = module.ConstantBool(true);
        uint[] operands = opcode switch
        {
            SpirvOp.GroupNonUniformElect => [scope],
            SpirvOp.GroupNonUniformAll or SpirvOp.GroupNonUniformAny or SpirvOp.GroupNonUniformAllEqual or
                SpirvOp.GroupNonUniformBallot => [scope, predicate],
            SpirvOp.GroupNonUniformBroadcastFirst => [scope, value],
            SpirvOp.GroupNonUniformUMin => [scope, 0, value],
            _ => [scope, value, lane],
        };
        var type = opcode == SpirvOp.GroupNonUniformBallot ? module.TypeVector(integer, 4)
            : opcode is SpirvOp.GroupNonUniformElect or SpirvOp.GroupNonUniformAll or SpirvOp.GroupNonUniformAny or
                SpirvOp.GroupNonUniformAllEqual ? module.TypeBool() : integer;
        var condition = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, module.TypeBool()), SpirvStorageClass.Input);
        module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddLabel();
        var first = module.AddInstruction(opcode, type, operands);
        Assert.Equal(first, module.AddInstruction(opcode, type, operands));
        var header = module.AllocateId();
        var merge = module.AllocateId();
        module.AddStatement(SpirvOp.SelectionMerge, merge, 0);
        module.AddStatement(SpirvOp.BranchConditional, module.AddInstruction(SpirvOp.Load, module.TypeBool(), condition), header, merge);
        // Even loop headers may inherit pure expressions, but never active-lane results.
        module.DeclareLoopHeader(header);
        module.AddLabel(header);
        Assert.NotEqual(first, module.AddInstruction(opcode, type, operands));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VolatileLoadObservesThePreviousStore(bool indexed, bool inherit)
    {
        var (module, integer, register, pointer, output) = MemoryModule(indexed, inherit);
        var first = module.Constant(integer, 7);
        module.AddStatement(SpirvOp.Store, pointer, first);
        var read = module.AddInstruction(SpirvOp.Load, integer, pointer, 1); // Volatile memory operand
        module.AddStatement(SpirvOp.Store, output, read);
        module.AddStatement(SpirvOp.Store, pointer, module.Constant(integer, 8));
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        Assert.Contains(Instructions(module.Build()), instruction => instruction.Op == SpirvOp.Store &&
            instruction.Operands.SequenceEqual(new[] { pointer, first }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VolatileStoreInvalidatesForwardedValues(bool indexed, bool inherit)
    {
        var (module, integer, _, pointer, output) = MemoryModule(indexed, inherit);
        var first = module.Constant(integer, 7);
        module.AddStatement(SpirvOp.Store, pointer, first);
        module.AddStatement(SpirvOp.Store, pointer, module.Constant(integer, 8), 1);
        var read = module.AddInstruction(SpirvOp.Load, integer, pointer);
        Assert.NotEqual(first, read);
        module.AddStatement(SpirvOp.Store, output, read);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        Assert.Contains(Instructions(module.Build()), instruction => instruction.Op == SpirvOp.Load && instruction.Operands[2] == pointer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElementLoadObservesThePreviousWholeArrayStore(bool inherit)
    {
        var (module, integer, register, pointer, output) = MemoryModule(true, inherit);
        var array = module.TypeArray(integer, 2);
        var first = module.ConstantComposite(array, module.Constant(integer, 7), module.Constant(integer, 9));
        var second = module.ConstantComposite(array, module.Constant(integer, 8), module.Constant(integer, 10));
        module.AddStatement(SpirvOp.Store, register, first);
        var read = module.AddInstruction(SpirvOp.Load, integer, pointer);
        module.AddStatement(SpirvOp.Store, output, read);
        module.AddStatement(SpirvOp.Store, register, second);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        Assert.Contains(Instructions(module.Build()), instruction => instruction.Op == SpirvOp.Store &&
            instruction.Operands.SequenceEqual(new[] { register, first }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignedAndUnsignedConstantIndicesNameTheSameTrackedElement(bool inherit)
    {
        var (module, integer, register, pointer, _) = MemoryModule(true, inherit);
        var alias = module.AddInstruction(SpirvOp.AccessChain, module.TypePointer(SpirvStorageClass.Private, integer),
            register, module.Constant(module.TypeInt(32, true), 0));
        Assert.Equal(pointer, alias);
        module.AddStatement(SpirvOp.Store, pointer, module.Constant(integer, 7));
        var next = module.Constant(integer, 8);
        module.AddStatement(SpirvOp.Store, alias, next);
        Assert.Equal(next, module.AddInstruction(SpirvOp.Load, integer, pointer));
    }

    [Fact]
    public void LocalOptimizationRemainsEnabledWithoutInheritance()
    {
        var module = new SpirvModuleBuilder { InheritBlockState = false };
        var integer = module.TypeInt(32, false);
        var input = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, integer), SpirvStorageClass.Input);
        var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, integer), SpirvStorageClass.Private);
        module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddLabel();
        var read = module.AddInstruction(SpirvOp.Load, integer, input);
        var divisor = module.Constant(integer, 3);
        var first = module.AddInstruction(SpirvOp.UDiv, integer, read, divisor);
        Assert.Equal(first, module.AddInstruction(SpirvOp.UDiv, integer, read, divisor));
        var stored = module.Constant(integer, 7);
        module.AddStatement(SpirvOp.Store, register, stored);
        Assert.Equal(stored, module.AddInstruction(SpirvOp.Load, integer, register));
        var successor = module.AllocateId();
        module.AddStatement(SpirvOp.Branch, successor);
        module.AddLabel(successor);
        Assert.NotEqual(first, module.AddInstruction(SpirvOp.UDiv, integer, read, divisor));
        Assert.NotEqual(stored, module.AddInstruction(SpirvOp.Load, integer, register));
    }

    [Fact]
    public void LargeShaderModeKeepsStoreForwardingAndBasicFolding()
    {
        var (module, integer, _, pointer, output) = MemoryModule(false, false, false);
        var first = module.Constant(integer, 7);
        var second = module.Constant(integer, 8);
        module.AddStatement(SpirvOp.Store, pointer, first);
        Assert.Equal(first, module.AddInstruction(SpirvOp.Load, integer, pointer));
        module.AddStatement(SpirvOp.Store, output, first);
        module.AddStatement(SpirvOp.Store, pointer, second);
        Assert.Equal(second, module.AddInstruction(SpirvOp.Load, integer, pointer));
        module.AddStatement(SpirvOp.Store, output, second);
        Assert.Equal(module.Constant(integer, 5), module.AddInstruction(SpirvOp.IAdd, integer,
            module.Constant(integer, 2), module.Constant(integer, 3)));
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        Assert.Equal(new[] { first, second }, Instructions(module.Build())
            .Where(instruction => instruction.Op == SpirvOp.Store && instruction.Operands[0] == output)
            .Select(instruction => instruction.Operands[1]));
    }

    [Fact]
    public void OptimizerCanBeDisabledForDiagnosis()
    {
        var module = new SpirvModuleBuilder { Optimize = false };
        var integer = module.TypeInt(32, false);
        var pointer = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, integer), SpirvStorageClass.Private);
        module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddLabel();
        var value = module.Constant(integer, 7);
        module.AddStatement(SpirvOp.Store, pointer, value);
        var read = module.AddInstruction(SpirvOp.Load, integer, pointer);
        Assert.NotEqual(value, read);
        var sum = module.AddInstruction(SpirvOp.IAdd, integer, read, value);
        Assert.NotEqual(sum, module.AddInstruction(SpirvOp.IAdd, integer, read, value));
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        Assert.Equal(2, Instructions(module.Build()).Count(instruction => instruction.Op == SpirvOp.IAdd));
    }

    private static (SpirvModuleBuilder Module, uint Integer, uint Register, uint Pointer, uint Output) MemoryModule(bool indexed, bool inherit = true, bool expressions = true)
    {
        var module = new SpirvModuleBuilder { InheritBlockState = inherit, OptimizeExpressions = expressions };
        var integer = module.TypeInt(32, false);
        var storage = indexed ? module.TypeArray(integer, 2) : integer;
        var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, storage), SpirvStorageClass.Private);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddLabel();
        var pointer = indexed ? module.AddInstruction(SpirvOp.AccessChain,
            module.TypePointer(SpirvStorageClass.Private, integer), register, module.Constant(integer, 0)) : register;
        return (module, integer, register, pointer, output);
    }

    private static List<(SpirvOp Op, uint[] Operands)> Instructions(byte[] code)
    {
        var words = new uint[code.Length / 4];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var instructions = new List<(SpirvOp, uint[])>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            instructions.Add(((SpirvOp)(words[offset] & 0xffff), words[(offset + 1)..(offset + (int)(words[offset] >> 16))]));
        return instructions;
    }
}
