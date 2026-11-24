// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class DescriptorShaderDataTests
{
    private static ShaderProgramInfo Program(BindingLayout layout, uint userBase = 100, params bool[] writes) => new()
    {
        Bindings = layout, UserDataBase = userBase,
        Resources = new SpecializedResourceInfo { Info = new ShaderResourceInfo
        {
            Buffers = writes.Select(written => new BufferResource { Written = written }).ToList(),
        } },
    };

    [Fact]
    public void CurrentUserDataAndDynamicFieldsAreWrittenForEverySnapshot()
    {
        var layout = new BindingLayout
        {
            UserDataRegisters = new uint[] { 100, 101, 104, 103, 104 },
            ShaderBaseDword = 5, MemoryOffsetDword = 7, UsesDispatchThreadLimits = true,
            UsesObjectId = true, UsesMeshDrawParameters = true,
            CallArgumentCount = 1, CallTargetCount = 1,
        };
        var program = Program(layout);
        foreach (var seed in new uint[] { 10, 20 })
        {
            var stage = new ShaderStageResources(program, new ResourceSnapshot { UserData = [seed, seed + 1, seed + 2, seed + 3, seed + 4] }, 0x1234567800000000UL + seed)
            {
                ObjectId = seed + 100, ThreadLimits = new DispatchThreadLimits(seed, seed + 1, seed + 2),
                MeshDraw = new MeshDrawParameters(seed, seed + 1, seed + 2, 4, 0x56789ABC00000000UL + seed),
                CallArguments = [0xABCDEF1200000000UL + seed], CallTargets = [0x1234ABCD00000000UL + seed],
            };
            var actual = new uint[layout.ShaderDataDwordCount];
            DescriptorShaderData.Write(stage, program, layout, actual);
            Assert.Equal(new uint[] { seed, seed + 1, seed + 4, seed + 3, seed + 4 }, actual[..5]);
            Assert.Equal(seed + 100, actual[layout.ObjectIdDword]);
        }
    }

    [Fact]
    public void TessellationAndUserValuesAreCurrentEvenWhenArrayReferencesAreReused()
    {
        var layout = new BindingLayout
        {
            UserDataRegisters = new uint[] { 100, 101 }, MemoryOffsetDword = 2, UsesTessellationData = true,
        };
        var program = Program(layout);
        var snapshot = new ResourceSnapshot { UserData = [10, 11] };
        var tessellation = new uint[Gen5TessellationData.DwordCount];
        var stage = new ShaderStageResources(program, snapshot) { TessellationData = tessellation };
        var words = new uint[layout.ShaderDataDwordCount];
        DescriptorShaderData.Write(stage, program, layout, words);
        snapshot.UserData[1] = 123;
        tessellation[0] = 456;
        DescriptorShaderData.Write(stage, program, layout, words);
        Assert.Equal(123u, words[1]);
        Assert.Equal(456u, words[layout.TessellationDataDword]);
    }

}
