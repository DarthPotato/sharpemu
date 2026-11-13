// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Replay has captured memory and owns its modules separately, but compiles for the runtime device.
// Do not inherit capability defaults here: they change resource planning and generated SPIR-V.
internal sealed class ShaderPrewarmHost(IShaderPipelineHost inner, Func<IGuestCompiledShader, ulong> createModule) : IShaderPipelineHost
{
    internal static string CapabilityStamp(IShaderPipelineHost host) => string.Join('|',
        host.MaxPushDescriptors, host.RuntimeImageDescriptorsSupported, host.ComputeWave64Supported,
        host.ComputeWave64SubgroupNative, host.MipStatisticsSubgroupReduction, host.RuntimeCallRecordsSupported,
        host.CallMarksSupported, host.TableBlocks, host.GraphicsSubgroupOperationsEnabled,
        host.SharedInt64AtomicsEnabled, host.ImageInt64AtomicsEnabled, host.ShaderSignedZeroInfNanPreserveFloat32Supported,
        host.ExactFloat16ConversionSupported, host.FastFloat16, host.ExecGuardElisionEnabled,
        host.PerVertexPixelInputsSupported,
        host.ClipDistanceEnabled, host.ShaderDrawParametersSupported, host.MeshShadersSupported, host.MeshLimits,
        host.MeshSubgroupSize, host.Limits, host.SupportsDualSourceBlend, host.NoAttachmentSampleCounts);

    public uint MaxPushDescriptors => inner.MaxPushDescriptors;
    public bool RuntimeImageDescriptorsSupported => inner.RuntimeImageDescriptorsSupported;
    public bool RuntimeDescriptorResolutionRequiresSerial => inner.RuntimeDescriptorResolutionRequiresSerial;
    public bool ComputeWave64Supported => inner.ComputeWave64Supported;
    public bool ComputeWave64SubgroupNative => inner.ComputeWave64SubgroupNative;
    public bool ComputeRunsOn32LaneSubgroups(ComputeInputInfo input) => inner.ComputeRunsOn32LaneSubgroups(input);
    public bool MipStatisticsSubgroupReduction => inner.MipStatisticsSubgroupReduction;
    public bool RuntimeCallRecordsSupported => inner.RuntimeCallRecordsSupported;
    public bool CallMarksSupported => inner.CallMarksSupported;
    public long CallMarkSnapshots => inner.CallMarkSnapshots;
    public long CallMarkedAt(uint bit) => inner.CallMarkedAt(bit);
    public bool TableBlocks => inner.TableBlocks;
    public bool GraphicsSubgroupOperationsEnabled => inner.GraphicsSubgroupOperationsEnabled;
    public bool SharedInt64AtomicsEnabled => inner.SharedInt64AtomicsEnabled;
    public bool ImageInt64AtomicsEnabled => inner.ImageInt64AtomicsEnabled;
    public bool ShaderSignedZeroInfNanPreserveFloat32Supported => inner.ShaderSignedZeroInfNanPreserveFloat32Supported;
    public bool ExactFloat16ConversionSupported => inner.ExactFloat16ConversionSupported;
    public int FastFloat16 => inner.FastFloat16;
    public bool ExecGuardElisionEnabled => inner.ExecGuardElisionEnabled;
    public bool PerVertexPixelInputsSupported => inner.PerVertexPixelInputsSupported;
    public bool ClipDistanceEnabled => inner.ClipDistanceEnabled;
    public bool ShaderDrawParametersSupported => inner.ShaderDrawParametersSupported;
    public bool MeshShadersSupported => inner.MeshShadersSupported;
    public MeshShaderLimits MeshLimits => inner.MeshLimits;
    public uint MeshSubgroupSize => inner.MeshSubgroupSize;
    public RenderHostLimits Limits => inner.Limits;
    public bool SupportsDualSourceBlend => inner.SupportsDualSourceBlend;
    public SampleCountFlags NoAttachmentSampleCounts => inner.NoAttachmentSampleCounts;

    public bool TryResolveColorOutput(uint dataFormat, uint numberType, uint componentSwap, out Gen5PixelOutputKind outputKind,
        out Gen5ColorComponentMapping componentMapping) =>
        inner.TryResolveColorOutput(dataFormat, numberType, componentSwap, out outputKind, out componentMapping);

    public bool TryReadGuestWord(ulong address, out uint word) { word = 0; return false; }
    public bool TryReadCleanGuestWord(ulong address, out uint word) { word = 0; return false; }
    public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId) => createModule(shader);
    public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description) =>
        throw new NotSupportedException("The prewarm host does not create pipelines.");
    public PipelineHandle CreateComputePipeline(ComputePipelineDescription description) =>
        throw new NotSupportedException("The prewarm host does not create pipelines.");
}
