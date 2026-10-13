// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// A fragment subgroup can be only partly populated. The fetch-inactive-off forms
// of DPP and DPP8 judge an absent partner by the guest EXEC bit (s126), which the
// guest wave keeps for lanes the host never launched, and apply the ISA's
// invalid-source rule when it is clear.
public sealed class Gen5PartialSubgroupLaneTests
{

    private static uint[] CompilePixel(Gen5InstructionControl dpp)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Vop1(0, "VMovB32", 4, Gen5Operand.Vector(3)) with { Control = dpp },
            ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        return words;
    }

    // Counts instructions with the given opcode in a SPIR-V module.
    private static int Count(uint[] words, uint opcode)
    {
        var count = 0;
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            if ((words[index] & 0xFFFF) == opcode) count++;
        }

        return count;
    }

    [Fact]
    public void PixelDpp_WithoutFetchInactive_AlsoShufflesTheExecWord()
    {
        const uint GroupNonUniformShuffle = 345;
        Assert.Equal(2, Count(CompilePixel(new Gen5Dpp8Control(0x053977, false)), GroupNonUniformShuffle));
        Assert.True(
            Count(CompilePixel(new Gen5DppControl(0x104, false, false, 0, 0, 15, 15)), GroupNonUniformShuffle) >= 2);
    }

    [Fact]
    public void PixelDpp_WithFetchInactive_ShufflesOnlyTheValue()
    {
        const uint GroupNonUniformShuffle = 345;
        Assert.Equal(1, Count(CompilePixel(new Gen5DppControl(0x104, true, false, 0, 0, 15, 15)), GroupNonUniformShuffle));
        Assert.Equal(1, Count(CompilePixel(new Gen5Dpp8Control(0x053977, true)), GroupNonUniformShuffle));
    }
}