// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class DescriptorPreparationMemoTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    private static object? Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value);
    private static bool Memoized(object value) => (bool)Property(value, "Memoized")!;

    private static ShaderProgramInfo Program(BindingLayout? layout = null, ShaderResourceInfo? info = null,
        IReadOnlyList<CallRecordCallee>? callees = null) => new()
    {
        Stage = ShaderStageKind.Compute, Hash = 0xD35C, UserDataBase = 100,
        Bindings = layout ?? new BindingLayout(),
        Resources = new SpecializedResourceInfo { Info = info ?? new ShaderResourceInfo() },
        CallRecordCallees = callees ?? [],
    };

    [Fact]
    public void ShaderBaseAndCallDataAreCurrentOnExpandedHitsAndProgramChangesDoNotHit()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        var layout = new BindingLayout
        {
            UserDataRegisters = new uint[] { 100 }, ShaderBaseDword = 1, MemoryOffsetDword = 3,
            CallArgumentCount = 1, CallTargetCount = 1,
        };
        var program = Program(layout);
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            for (uint value = 1; value <= 3; value++)
            {
                var stage = new ShaderStageResources(program, new ResourceSnapshot { UserData = [value] }, 0x12340000UL + value)
                {
                    CallArguments = [0x56780000UL + value], CallTargets = [0xABCD0000UL + value],
                };
                var prepared = presenter.RenderHost.PrepareBindings(stage);
                Assert.Equal(DescriptorPreparationOptions.Fast && value > 1, Memoized(prepared));
                var expected = new uint[layout.ShaderDataDwordCount];
                DescriptorShaderData.Write(stage, program, layout, expected, null, false);
                Assert.Equal(expected, (uint[])Property(prepared, "ShaderData")!);
            }
            var other = Program(layout);
            Assert.False(Memoized(presenter.RenderHost.PrepareBindings(new(other, new ResourceSnapshot { UserData = [9] })
            { CallArguments = [1], CallTargets = [2] })));
        });
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void ChangedSamplerReusesImagesButResolvesTheCurrentHandle()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        var program = Program(info: new ShaderResourceInfo { Samplers = [new SamplerResource()] });
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            var first = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot { Samplers = [[0, 0, 0, 0]] }));
            var firstHandle = Assert.Single((Sampler[])Field(Property(first, "Descriptors")!, "Samplers")!);
            var second = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot { Samplers = [[1, 0, 0, 0]] }));
            Assert.Equal(DescriptorPreparationOptions.Fast, Memoized(second));
            var secondHandle = Assert.Single((Sampler[])Field(Property(second, "Descriptors")!, "Samplers")!);
            Assert.NotEqual(firstHandle, secondHandle);
            var third = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot { Samplers = [[0, 0, 0, 0]] }));
            Assert.True(Memoized(third));
            Assert.Equal(firstHandle, Assert.Single((Sampler[])Field(Property(third, "Descriptors")!, "Samplers")!));
        });
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void ShaderBaseAloneNoLongerSplitsMemoButStillRewritesShaderData()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        var layout = new BindingLayout { ShaderBaseDword = 0, MemoryOffsetDword = 2 };
        var program = Program(layout);
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            Assert.False(Memoized(presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot(), 0x1111000012340000UL))));
            var second = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot(), 0x2222000056780000UL));
            Assert.Equal(DescriptorPreparationOptions.Fast, Memoized(second));
            Assert.Equal(new uint[] { 0x56780000, 0x22220000 }, ((uint[])Property(second, "ShaderData")!)[..2]);
            Assert.True(Memoized(presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot(), 0x2222000056780000UL))));
        });
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void ActualCallRecordCalleesRemainIneligibleAndTheirTablesArePreparedEveryTime()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        var layout = new BindingLayout { CallArgumentCount = 1, CallTargetCount = 1 };
        var program = Program(layout, callees: [new CallRecordCallee([], [], 0)]);
        presenter.Run(() =>
        {
            using var profile = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.DrawResources);
            using var preparation = presenter.RenderHost.BeginPreparation();
            for (var index = 0; index < 2; index++)
            {
                var prepared = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot())
                    { CallArguments = [1], CallTargets = [2] });
                Assert.False(Memoized(prepared));
                Assert.NotEmpty((uint[])Property(prepared, "CallRecordWords")!);
                if (RenderPhaseProfile.MovableEnabled)
                    Assert.Equal(RenderPhaseProfile.DescriptorMissReason.CallRecords, Property(prepared, "Miss"));
            }
        });
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void CapacityOverflowEvictsOnlyOneEntryInFastModeAndInvalidatesTheEvictee()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        var program = Program();
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            var prepared = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot()));
            var set = presenter.InvokeMethod("StagePreparationsOf", program)!;
            var dictionary = (IDictionary)Field(set, "ByContent")!;
            var first = Field(set, "Last")!;
            // Exercise the actual capture/eviction path with synthetic distinct keys; no
            // image handles are installed and nothing is submitted to the GPU.
            for (uint index = 1; index <= 1024; index++)
                presenter.InvokeMethod("NoteStagePreparation", set, prepared, new ResourceSnapshot { Images = [[index]] }, 0UL);
            Assert.False((bool)Field(first, "Valid")!);
            Assert.Equal(DescriptorPreparationOptions.Fast ? 1024 : 1, dictionary.Count);
            Assert.DoesNotContain(first, dictionary.Values.Cast<object>());
            Assert.All(dictionary.Values.Cast<object>(), memo => Assert.True((bool)Field(memo, "Valid")!));
            for (uint index = 1025; index <= 3072; index++)
                presenter.InvokeMethod("NoteStagePreparation", set, prepared, new ResourceSnapshot { Images = [[index]] }, 0UL);
            if (RenderPhaseProfile.MovableEnabled)
            {
                using var phase = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.DrawResources);
                using var operation = RenderPhaseProfile.MeasureDescriptor();
                Assert.False((bool)presenter.InvokeMethod("TryFindStagePreparation", set, new ResourceSnapshot(), 0UL, null!, null!)!);
                Assert.Equal(RenderPhaseProfile.DescriptorMissReason.CapacityEviction, RenderPhaseProfile.CurrentDescriptorMiss);
            }
        });
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void ExpandedMemoStillRejectsChangedImagesOverlapsAndHistoryOverflow()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        var address = presenter.Harness.MapBacked(0x200000, ReadWrite);
        var info = new ShaderResourceInfo { Images = [new ImageResource
        {
            Dimension = ImageDimension.Dim2D, NumericClass = ImageNumericClass.Float,
            ResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass.Sampled, Read = true,
        }] };
        var program = Program(info: info);
        var first = new ResourceSnapshot { Images = [RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 8, 8)] };
        var second = new ResourceSnapshot { Images = [RegisterWords.Texture(address + 0x10000, GuestPixelFormat.Bits8_8_8_8UNorm, 8, 8)] };
        presenter.Run(() =>
        {
            using var profile = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.DrawResources);
            using var preparation = presenter.RenderHost.BeginPreparation();
            Assert.False(Memoized(presenter.RenderHost.PrepareBindings(new(program, first))));
            Assert.True(Memoized(presenter.RenderHost.PrepareBindings(new(program, first))));
            var changed = presenter.RenderHost.PrepareBindings(new(program, second));
            Assert.False(Memoized(changed));
            if (RenderPhaseProfile.MovableEnabled)
                Assert.Equal(RenderPhaseProfile.DescriptorMissReason.ImageWordsChanged, Property(changed, "Miss"));
            Assert.True(Memoized(presenter.RenderHost.PrepareBindings(new(program, first))));
            var fake = new ResourceSlotIdentifier(1000000, 1);
            presenter.Harness.Images.AddPageOwner(address, fake);
            Assert.True(presenter.Harness.Images.RemovePageOwner(address, fake));
            var overlap = presenter.RenderHost.PrepareBindings(new(program, first));
            Assert.False(Memoized(overlap));
            if (RenderPhaseProfile.MovableEnabled)
                Assert.Equal(RenderPhaseProfile.DescriptorMissReason.LookupRangeInvalidated, Property(overlap, "Miss"));
            // Each add/remove pair records two lookup events.
            for (var index = 0; index < GuestImageCache.LookupHistorySlots / 2 + 1; index++)
            {
                presenter.Harness.Images.AddPageOwner(address + 0x100000, fake);
                Assert.True(presenter.Harness.Images.RemovePageOwner(address + 0x100000, fake));
            }
            var overflow = presenter.RenderHost.PrepareBindings(new(program, first));
            Assert.False(Memoized(overflow));
            if (RenderPhaseProfile.MovableEnabled)
                Assert.Equal(RenderPhaseProfile.DescriptorMissReason.LookupHistoryOverflow, Property(overflow, "Miss"));
        });
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void ChangedSamplerStillValidatesWordsAndFailureDoesNotReplaceRememberedHandles()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        using var fatal = new FatalScope();
        var program = Program(info: new ShaderResourceInfo { Samplers = [new SamplerResource()] });
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            var valid = new ResourceSnapshot { Samplers = [[0, 0, 0, 0]] };
            var first = presenter.RenderHost.PrepareBindings(new(program, valid));
            var handle = Assert.Single((Sampler[])Field(Property(first, "Descriptors")!, "Samplers")!);
            Assert.ThrowsAny<Exception>(() => presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot { Samplers = [[1, 2, 3]] })));
            var afterFailure = presenter.RenderHost.PrepareBindings(new(program, valid));
            Assert.True(Memoized(afterFailure));
            Assert.Equal(handle, Assert.Single((Sampler[])Field(Property(afterFailure, "Descriptors")!, "Samplers")!));
        });
        presenter.Harness.Shutdown();
    }
}
