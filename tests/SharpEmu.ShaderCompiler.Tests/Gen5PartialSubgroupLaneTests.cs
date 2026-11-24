// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvValueReuse")]
public sealed class Gen5PartialSubgroupLaneTests
{
    private const uint GroupNonUniformBallot = 339;
    private const uint GroupNonUniformShuffle = 345;

    private static uint[] CompilePixel(Gen5ShaderInstruction instruction)
    {
        var program = ResourceTestProgram.Program(instruction, ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
            GraphicsPartialSubgroupLanes = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        return words;
    }

    private static int Count(uint[] words, uint opcode)
    {
        var count = 0;
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            if ((words[index] & 0xFFFF) == opcode) count++;
        }

        return count;
    }

    [Theory]
    [InlineData(31u)]
    [InlineData(63u)]
    public void PixelReadlane_ReadsOneLaneForTheWholeSubgroup(uint lane)
    {
        var words = CompilePixel(ResourceTestProgram.ReadLane(0, 20, 3, lane));
        Assert.True(Count(words, GroupNonUniformShuffle) >= 1);
        Assert.True(Count(words, GroupNonUniformBallot) >= 1);
    }
}
