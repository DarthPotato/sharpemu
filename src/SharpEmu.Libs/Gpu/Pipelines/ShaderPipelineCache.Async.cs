// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    internal PipelineCompileScheduler.Request? RequestGraphicsAhead(in DrawState original, ContextRegisters context,
        PrimitiveTopology topology, bool restart)
    {
        if (_compiles is null || original.Programs.MeshInput is not null || original.Programs.VertexInput.Tessellation is not null ||
            topology == PrimitiveTopology.PatchList) return null;
        // DrawState owns inline attachment arrays: pruning this value cannot alter the record.
        var state = original;
        var vertex = state.Programs.VertexInput;
        var pixel = state.PixelActive ? state.Programs.PixelInput : null;
        RenderExecutor.DropUnwrittenColorTargets(context, ref state, pixel?.Stage.Program);
        var rendering = MetadataRendering(in state);
        var colors = DrawState.BoundColors(ref state);
        var lookup = GraphicsLookup(colors, in state.Depth, vertex, pixel, context, in rendering, topology, restart,
            state.Programs.DisableBlending, state.Programs.Vertex, state.Programs.Pixel, metadataOnly: true);
        if (_graphicsPipelines.ContainsKey(lookup)) return null;
        var description = BuildGraphicsDescription(colors, in state.Depth, vertex, pixel, context, in rendering,
            topology, restart, state.Programs.DisableBlending, state.Programs.Vertex, state.Programs.Pixel,
            _host.NoAttachmentSampleCounts, metadataOnly: true);
        return RequestGraphics(description, KeyOf(description), speculative: true);
    }

    internal static RenderingState MetadataRendering(in DrawState state)
    {
        var rendering = new RenderingState { ColorAttachmentCount = state.ColorCount };
        uint samples = 0;
        for (var i = 0; i < state.ColorCount; i++)
        {
            var target = state.Colors[i].Resolution;
            CheckSamples(target.Samples);
            rendering.ColorAttachments[i] = new RenderingAttachment { Format = target.Request.View.Format };
        }
        if (state.Depth.HasTarget)
        {
            var target = state.Depth.Target.Target;
            CheckSamples(target.Samples);
            var aspects = ViewFormatRules.DepthAspects(target.Format);
            rendering.DepthStencilAttachment = new RenderingAttachment
            {
                Format = target.Format,
                HasDepth = (aspects & ImageAspectFlags.DepthBit) != 0,
                HasStencil = (aspects & ImageAspectFlags.StencilBit) != 0,
            };
        }
        rendering.Samples = samples == 0 ? 1 : samples;
        return rendering;

        void CheckSamples(uint next)
        {
            if (samples != 0 && samples != next)
                throw SubmissionScheduler.Fatal("Mixed attachment sample counts are not supported.");
            samples = next;
        }
    }

    private GraphicsPipelineKey GraphicsLookup(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth,
        VertexInputInfo vertex, PixelInputInfo? pixel, ContextRegisters context, in RenderingState rendering,
        PrimitiveTopology topology, bool restart, bool disableBlending, ShaderProgram vertexProgram,
        ShaderProgram pixelProgram, bool metadataOnly)
    {
        var lookup = _graphicsLookup ??= new GraphicsPipelineKey
        {
            Rendering = new PipelineRenderingState(), VertexInput = new PipelineVertexInputState(),
            StaticParameters = new PipelineStaticParameters(),
        };
        lookup.Rendering.Reset(); lookup.VertexInput.Reset(); lookup.StaticParameters.Reset();
        FillGraphicsState(colors, in depth, vertex, pixel, context, in rendering, topology, restart, disableBlending,
            vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts, lookup.Rendering, lookup.StaticParameters, lookup.VertexInput, metadataOnly);
        lookup.VertexProgramId = vertexProgram.Id;
        lookup.PixelProgramId = pixel is not null ? pixelProgram.Id : 0;
        lookup.Tessellation = vertex.Tessellation;
        return lookup;
    }

    internal void ValidatePrediction(ResolvedDraw record, ref DrawState state, VertexInputInfo vertex, PixelInputInfo? pixel)
    {
        if (record.PredictedPipeline is not { } prediction) return;
        var lookup = GraphicsLookup(DrawState.BoundColors(ref state), in state.Depth, vertex, pixel, record.Context,
            in state.Rendering, record.Topology, record.PrimitiveRestart, state.Programs.DisableBlending,
            state.Programs.Vertex, state.Programs.Pixel, metadataOnly: false);
        if (lookup.Equals(prediction.Key)) _compiles!.Consume(prediction);
        else AsyncPipelineProfile.Mismatch();
    }

    private PipelineCompileScheduler.Request? RequestGraphics(GraphicsPipelineDescription description, GraphicsPipelineKey key, bool speculative)
    {
        var request = _compiles!.Enqueue(key, () =>
        {
            var created = _host.CreateGraphicsPipeline(description);
            lock (_pipelineGate) _graphicsPipelines.TryAdd(key, created);
            if (_host.ShaderPrewarm is { } prewarm && description.Tessellation is null &&
                description.VertexStage.Stage == ShaderStageKind.Vertex) prewarm.RecordGraphics(description);
            ShaderCacheCounters.CountGraphicsPipeline();
            return created;
        }, speculative, 4096L + description.VertexInfo.Buffers.Length * 32L + description.VertexInfo.Attributes.Length * 128L);
        if (request is not null && !speculative) _compiles.Consume(request);
        return request;
    }

    internal void RequestComputeAhead(ComputeInputInfo input, ShaderProgram program)
    {
        var stage = input.Stage.Program;
        if (_compiles is null || !program.IsValid || stage is null || stage.Bindings is { CallTargetCount: not 0 } ||
            _computePipelines.ContainsKey(new ComputePipelineKey(program.Id))) return;
        RequestCompute(input, program, stage, speculative: true);
    }

    private PipelineCompileScheduler.Request? RequestCompute(ComputeInputInfo input, ShaderProgram program, ShaderProgramInfo stage, bool speculative)
    {
        var key = new ComputePipelineKey(program.Id);
        var description = new ComputePipelineDescription { Input = input.Snapshot(), Program = program, Stage = stage };
        var request = _compiles!.Enqueue(key, () =>
        {
            var created = _host.CreateComputePipeline(description);
            lock (_pipelineGate) _computePipelines.TryAdd(key, created);
            ShaderCacheCounters.CountComputePipeline();
            return created;
        }, speculative, 1024, compute: true);
        if (request is not null && !speculative) _compiles.Consume(request);
        return request;
    }

    // Preserve the linked host's nonblocking poll/skip protocol. A short claim
    // prevents two callers from registering the same pending result, without
    // retaining the cache gate during host setup or an explicitly inline compile.
    private bool TryCreateComputeOutsideGate(ComputeInputInfo input, ShaderProgram program, out PipelineHandle handle)
    {
        var key = new ComputePipelineKey(program.Id);
        TaskCompletionSource claim;
        lock (_pipelineGate)
        {
            if (_computePipelines.TryGetValue(key, out handle)) return true;
            if (_computeClaims.ContainsKey(key)) { handle = default; return false; }
            claim = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _computeClaims.Add(key, claim);
        }
        try
        {
            var ready = _host.TryCreateComputePipeline(new ComputePipelineDescription
            {
                Input = input, Program = program, Stage = input.Stage.Program!,
            }, out handle);
            lock (_pipelineGate)
            {
                if (ready) _computePipelines.TryAdd(key, handle);
                _computeClaims.Remove(key);
            }
            claim.SetResult();
            if (ready) ShaderCacheCounters.CountComputePipeline();
            return ready;
        }
        catch
        {
            lock (_pipelineGate) _computeClaims.Remove(key);
            claim.SetResult();
            throw;
        }
    }
}
