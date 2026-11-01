// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
namespace SharpEmu.ShaderCompiler.Tests;
public sealed class Gen5RuntimeFormatSharingTests
{
    [Fact]
    public void RepeatedRuntimeFormatLoadsShareTheirDecoder()
    {
        var loads = Enumerable.Range(0, 32).Select(i => BufferLoad((uint)i * 8, 0, dwords: 4, formatted: true)).ToArray();
        var program = Program([.. loads, EndProgram(256)]) with
        {
            FunctionBufferAccesses = loads.Select(i => i.Pc).ToHashSet(),
        };
        var (plan, resources, layout) = Prepare(program);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out var shader, out var error), error);
        // Inlining the four component decoders at every load generated over 1 MB here.
        Assert.True(shader.Spirv.Length < 200_000, $"SPIR-V bytes: {shader.Spirv.Length}");
    }
}
