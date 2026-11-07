// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class AsyncPipelineIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static DrawState State(bool depth = false)
    {
        var programs = Programs();
        var state = DrawState.Create();
        state.Programs = new GraphicsPrograms(programs.VertexInput, programs.PixelInput)
        {
            Vertex = programs.Vertex, Pixel = programs.Pixel, DropUnwrittenColorTargets = true,
        };
        var request = new ImageRequest { View = ImageViewDescription.Default with { Format = Format.R8G8B8A8Unorm } };
        var color = new ColorTargetResolution(request, 0x1000, 0x10000, new Extent2D(64, 64), 0, 0, 1,
            ColorComponentMap.Identity, false, false, default);
        state.Colors[0] = new(in color, 0, ResourceSlotIdentifier.Invalid);
        state.Colors[1] = new(in color, 1, ResourceSlotIdentifier.Invalid);
        state.ColorCount = 2;
        if (depth)
        {
            var target = new DepthTargetResolution(default, Format.D32SfloatS8Uint, 64, 64, 1, true, false,
                0x2000, 0x1000, 0x3000, 0x1000, 0, 0, false, false, false, false);
            var attachment = new DepthTargetState(target, default);
            state.Depth = new(in attachment, ResourceSlotIdentifier.Invalid);
        }
        return state;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataPredictionMatchesRecorderKeyAfterPruningACopy(bool depth)
    {
        using var fatal = new FatalScope();
        using var scheduler = new PipelineCompileScheduler(2);
        var guest = new PipelineTestGuest();
        guest.Host.PipelineCompiles = scheduler;
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        var state = State(depth);
        var prediction = cache.RequestGraphicsAhead(in state, banks.Context, PrimitiveTopology.TriangleList, false)!;
        Assert.NotNull(prediction);
        Assert.Equal(2u, state.ColorCount);
        Assert.Equal(1u, state.Colors[1].Slot);
        RenderExecutor.DropUnwrittenColorTargets(banks.Context, ref state, state.Programs.PixelInput.Stage.Program);
        state.Rendering = ShaderPipelineCache.MetadataRendering(in state);
        for (var i = 0; i < state.ColorCount; i++) state.Colors[i].Image = new ResourceSlotIdentifier(1, 1);
        var description = ShaderPipelineCache.BuildGraphicsDescription(DrawState.BoundColors(ref state), in state.Depth,
            state.Programs.VertexInput, state.Programs.PixelInput, banks.Context, in state.Rendering,
            PrimitiveTopology.TriangleList, false, false, state.Programs.Vertex, state.Programs.Pixel, SampleCountFlags.Count1Bit);
        Assert.Equal(ShaderPipelineCache.KeyOf(description), prediction.Key);
        await prediction.Completion.Task.WaitAsync(Timeout);
        Assert.Single(guest.Host.GraphicsPipelines);
        var frozen = guest.Host.GraphicsPipelines[0];
        Assert.NotSame(state.Programs.VertexInput, frozen.VertexInfo);
        Assert.NotSame(state.Programs.PixelInput, frozen.PixelInfo);
        var used = cache.CreateGraphicsPipeline(DrawState.BoundColors(ref state), in state.Depth, state.Programs.VertexInput,
            state.Programs.PixelInput, banks.Context, in state.Rendering, PrimitiveTopology.TriangleList, false, false,
            state.Programs.Vertex, state.Programs.Pixel);
        Assert.Equal(await prediction.Completion.Task, used);
        Assert.Single(guest.Host.GraphicsPipelines);
    }

    [Fact]
    public async Task MismatchDemandCreatesAuthoritativeKeyAndRetainsValidPrediction()
    {
        using var fatal = new FatalScope();
        using var scheduler = new PipelineCompileScheduler(2);
        var guest = new PipelineTestGuest();
        guest.Host.PipelineCompiles = scheduler;
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        var state = State();
        var prediction = cache.RequestGraphicsAhead(in state, banks.Context, PrimitiveTopology.TriangleList, false)!;
        await prediction.Completion.Task.WaitAsync(Timeout);
        RenderExecutor.DropUnwrittenColorTargets(banks.Context, ref state, state.Programs.PixelInput.Stage.Program);
        state.Rendering = ShaderPipelineCache.MetadataRendering(in state);
        for (var i = 0; i < state.ColorCount; i++) state.Colors[i].Image = new ResourceSlotIdentifier(1, 1);
        // Recording discovers another static state: it must use this full native key.
        banks.Context.RasterMode.CullBack = !banks.Context.RasterMode.CullBack;
        var record = new ResolvedDraw { Context = banks.Context, Topology = PrimitiveTopology.TriangleList, PredictedPipeline = prediction };
        cache.ValidatePrediction(record, ref state, state.Programs.VertexInput, state.Programs.PixelInput);
        Assert.False(prediction.Consumed);
        var actual = cache.CreateGraphicsPipeline(DrawState.BoundColors(ref state), in state.Depth, state.Programs.VertexInput,
            state.Programs.PixelInput, banks.Context, in state.Rendering, PrimitiveTopology.TriangleList, false, false,
            state.Programs.Vertex, state.Programs.Pixel);
        Assert.NotEqual(await prediction.Completion.Task, actual);
        Assert.Equal(2, cache.GraphicsPipelineCount);
        Assert.Equal(2, guest.Host.GraphicsPipelines.Count);
        banks.Context.RasterMode.CullBack = !banks.Context.RasterMode.CullBack;
        var later = cache.CreateGraphicsPipeline(DrawState.BoundColors(ref state), in state.Depth, state.Programs.VertexInput,
            state.Programs.PixelInput, banks.Context, in state.Rendering, PrimitiveTopology.TriangleList, false, false,
            state.Programs.Vertex, state.Programs.Pixel);
        Assert.Equal(await prediction.Completion.Task, later);
        if (SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled) Assert.True(prediction.Consumed);
        Assert.Equal(2, guest.Host.GraphicsPipelines.Count);
    }

    [Fact]
    public async Task OrdinaryComputeStartsAheadAndDemandWaitDoesNotHoldCacheGate()
    {
        using var scheduler = new PipelineCompileScheduler(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var guest = new PipelineTestGuest();
        guest.Host.PipelineCompiles = scheduler;
        guest.Host.BeforeComputeCompile = description =>
        {
            if (description.Program.Id == 1) { entered.Set(); Assert.True(release.Wait(Timeout)); }
        };
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var input = ComputeProgram().Input;
        cache.RequestComputeAhead(input, new ShaderProgram(1, 1));
        try
        {
            Assert.True(entered.Wait(Timeout));
            var demand = Task.Run(() => cache.CreateComputePipeline(input, new ShaderProgram(1, 1)));
            var independent = Task.Run(() => cache.CreateComputePipeline(input, new ShaderProgram(2, 2)));
            await independent.WaitAsync(Timeout);
            release.Set();
            await demand.WaitAsync(Timeout);
            Assert.Equal(2, guest.Host.ComputePipelines.Count);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void LegacyHostDoesNoCompileAheadAndCreatesOnlyAtUse()
    {
        var guest = new PipelineTestGuest();
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        var state = State();
        Assert.Null(cache.RequestGraphicsAhead(in state, banks.Context, PrimitiveTopology.TriangleList, false));
        var compute = ComputeProgram();
        cache.RequestComputeAhead(compute.Input, compute.Program);
        Assert.Empty(guest.Host.GraphicsPipelines);
        Assert.Empty(guest.Host.ComputePipelines);
        var thread = Environment.CurrentManagedThreadId;
        guest.Host.BeforeComputeCompile = _ => Assert.Equal(thread, Environment.CurrentManagedThreadId);
        cache.CreateComputePipeline(compute.Input, compute.Program);
        Assert.Single(guest.Host.ComputePipelines);
    }

    [Fact]
    public void SwitchOffDisablesPresenterScheduler()
    {
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_PIPELINES");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_ASYNC_PIPELINES", "0");
            var type = typeof(SharpEmu.Libs.VideoOut.VulkanVideoPresenter).GetNestedType("Presenter", System.Reflection.BindingFlags.NonPublic)!;
            var presenter = (IShaderPipelineHost)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            Assert.Null(presenter.PipelineCompiles);
            LegacyHostDoesNoCompileAheadAndCreatesOnlyAtUse();
        }
        finally { Environment.SetEnvironmentVariable("SHARPEMU_ASYNC_PIPELINES", previous); }
    }

    [Fact]
    public void ReadyHitsAllocateNothingOnResolveOrUseWhenUnprofiled()
    {
        if (SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled) return;
        using var scheduler = new PipelineCompileScheduler(1);
        var guest = new PipelineTestGuest();
        guest.Host.PipelineCompiles = scheduler;
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        var original = State();
        var state = original;
        RenderExecutor.DropUnwrittenColorTargets(banks.Context, ref state, state.Programs.PixelInput.Stage.Program);
        state.Rendering = ShaderPipelineCache.MetadataRendering(in state);
        for (var i = 0; i < state.ColorCount; i++) state.Colors[i].Image = new ResourceSlotIdentifier(1, 1);
        Action use = () =>
        {
            cache.CreateGraphicsPipeline(DrawState.BoundColors(ref state), in state.Depth, state.Programs.VertexInput,
                state.Programs.PixelInput, banks.Context, in state.Rendering, PrimitiveTopology.TriangleList, false, false,
                state.Programs.Vertex, state.Programs.Pixel);
            cache.RequestGraphicsAhead(in original, banks.Context, PrimitiveTopology.TriangleList, false);
        };
        for (var i = 0; i < 1024; i++) use();
        var allocated = AllocationMeasurementCollection.Measure(() => { for (var i = 0; i < 1024; i++) use(); });
        Assert.Equal(0, allocated);
        Assert.Single(guest.Host.GraphicsPipelines);
    }
}
