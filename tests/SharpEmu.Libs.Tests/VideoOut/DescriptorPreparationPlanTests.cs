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
public sealed class DescriptorPreparationPlanTests
{
    private static ShaderProgramInfo Program(BindingLayout layout, uint userBase = 100, params bool[] writes) => new()
    {
        Bindings = layout, UserDataBase = userBase,
        Resources = new SpecializedResourceInfo { Info = new ShaderResourceInfo
        {
            Buffers = writes.Select(written => new BufferResource { Written = written }).ToList(),
        } },
    };

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("false", true)]
    [InlineData("0", false)]
    public void SwitchDefaultsOnAndOnlyZeroRestoresOriginalPath(string? value, bool expected) =>
        Assert.Equal(expected, DescriptorPreparationOptions.ParseFast(value));

    [Fact]
    public void ProcessSwitchGatesPlanCreation()
    {
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_DESC_PREP_FAST") != "0";
        Assert.Equal(expected, DescriptorPreparationOptions.Fast);
        Assert.Equal(expected, DescriptorPreparationPlan.Create(Program(new BindingLayout())) is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentUserDataAndDynamicFieldsAreWrittenForEverySnapshot(bool fast)
    {
        var layout = new BindingLayout
        {
            UserDataRegisters = new uint[] { 100, 101, 104, 103, 104 },
            ShaderBaseDword = 5, MemoryOffsetDword = 7, UsesDispatchThreadLimits = true,
            UsesObjectId = true, UsesMeshDrawParameters = true,
            CallArgumentCount = 1, CallTargetCount = 1,
        };
        var program = Program(layout);
        var plan = new DescriptorPreparationPlan(program);
        foreach (var seed in new uint[] { 10, 20 })
        {
            var stage = new ShaderStageResources(program, new ResourceSnapshot { UserData = [seed, seed + 1, seed + 2, seed + 3, seed + 4] }, 0x1234567800000000UL + seed)
            {
                ObjectId = seed + 100, ThreadLimits = new DispatchThreadLimits(seed, seed + 1, seed + 2),
                MeshDraw = new MeshDrawParameters(seed, seed + 1, seed + 2, 4, 0x56789ABC00000000UL + seed),
                CallArguments = [0xABCDEF1200000000UL + seed], CallTargets = [0x1234ABCD00000000UL + seed],
            };
            var expected = new uint[layout.ShaderDataDwordCount];
            var actual = new uint[layout.ShaderDataDwordCount];
            DescriptorShaderData.Write(stage, program, layout, expected, null, false);
            DescriptorShaderData.Write(stage, program, layout, actual, plan, fast);
            Assert.Equal(expected, actual);
            Assert.Equal(new uint[] { seed, seed + 1, seed + 4, seed + 3, seed + 4 }, actual[..5]);
            Assert.Equal(seed + 100, actual[layout.ObjectIdDword]);
        }
    }

    [Fact]
    public void ShortenedSnapshotOrDifferentProgramCannotUseCachedRegisterPlan()
    {
        using var fatal = new FatalScope();
        var layout = new BindingLayout { UserDataRegisters = new uint[] { 100, 103 }, MemoryOffsetDword = 2 };
        var program = Program(layout);
        var plan = new DescriptorPreparationPlan(program);
        var words = new uint[layout.ShaderDataDwordCount];
        Assert.True(plan.TryWriteUserData(new(program, new ResourceSnapshot { UserData = [1, 2, 3, 4] }), words));
        Assert.False(plan.TryWriteUserData(new(program, new ResourceSnapshot { UserData = [1, 2] }), words));
        var other = Program(layout, 99);
        Assert.False(plan.TryWriteUserData(new(other, new ResourceSnapshot { UserData = [1, 2, 3, 4, 5] }), words));
        Assert.False(plan.TryWriteUserData(new(program, new ResourceSnapshot { UserData = [1, 2, 3, 4] }), new uint[1]));
        var stage = new ShaderStageResources(program, new ResourceSnapshot { UserData = [1, 2] });
        var baseline = Assert.ThrowsAny<Exception>(() => DescriptorShaderData.Write(stage, program, layout, words, null, false));
        var optimized = Assert.ThrowsAny<Exception>(() => DescriptorShaderData.Write(stage, program, layout, words, plan, true));
        Assert.Equal(baseline.GetType(), optimized.GetType());
        Assert.Equal(baseline.Message, optimized.Message);
    }

    [Fact]
    public void InvalidRegisterMappingFallsBackToOriginalValidation()
    {
        var layout = new BindingLayout { UserDataRegisters = new uint[] { 99 }, MemoryOffsetDword = 1 };
        var program = Program(layout);
        var plan = new DescriptorPreparationPlan(program);
        Assert.False(plan.TryWriteUserData(new(program, new ResourceSnapshot { UserData = [42] }), new uint[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TessellationAndUserValuesAreCurrentEvenWhenArrayReferencesAreReused(bool fast)
    {
        var layout = new BindingLayout
        {
            UserDataRegisters = new uint[] { 100, 101 }, MemoryOffsetDword = 2, UsesTessellationData = true,
        };
        var program = Program(layout);
        var plan = new DescriptorPreparationPlan(program);
        var snapshot = new ResourceSnapshot { UserData = [10, 11] };
        var tessellation = new uint[Gen5TessellationData.DwordCount];
        var stage = new ShaderStageResources(program, snapshot) { TessellationData = tessellation };
        var words = new uint[layout.ShaderDataDwordCount];
        DescriptorShaderData.Write(stage, program, layout, words, plan, fast);
        snapshot.UserData[1] = 123;
        tessellation[0] = 456;
        DescriptorShaderData.Write(stage, program, layout, words, plan, fast);
        Assert.Equal(123u, words[1]);
        Assert.Equal(456u, words[layout.TessellationDataDword]);
    }

    [Fact]
    public void ImmutableWriteSetAndBindingPresenceBelongToOneProgram()
    {
        var first = new DescriptorPreparationPlan(Program(new BindingLayout
        {
            Descriptors = new DescriptorBinding[] { new(DescriptorBindingKind.ShaderData, []), new(DescriptorBindingKind.GlobalDataShare, []) },
        }, 100, false, true, false, true));
        Assert.Equal(new[] { 1, 3 }, first.WrittenBuffers);
        Assert.True(first.ShaderData);
        Assert.True(first.GlobalDataShare);
        Assert.False(first.CallRecords);
        var second = new DescriptorPreparationPlan(Program(new BindingLayout
        {
            Descriptors = new DescriptorBinding[] { new(DescriptorBindingKind.CallMarks, []), new(DescriptorBindingKind.FlattenedResourceTable, []), new(DescriptorBindingKind.CallRecords, []) },
        }, 100, true, false));
        Assert.Equal(new[] { 0 }, second.WrittenBuffers);
        Assert.False(second.ShaderData);
        Assert.False(second.GlobalDataShare);
        Assert.True(second.CallMarks);
        Assert.True(second.FlattenedTable);
        Assert.True(second.CallRecords);
    }
}
