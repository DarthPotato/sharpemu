// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Offline profile of this machine's RTX 3080. Values were checked with vulkaninfo;
// policies mirror Presenter in Pipelines, Device.Setup, BindlessHeap, and CallMarks.
// No instance, device, window, shader module or native pipeline is created here.
internal sealed class Rtx3080Host : IShaderPipelineHost
{
    public uint MaxPushDescriptors => 32;
    public bool RuntimeImageDescriptorsSupported => true;
    public bool ComputeWave64Supported => false;
    public bool ComputeWave64SubgroupNative => false;
    public bool ComputeRunsOn32LaneSubgroups(ComputeInputInfo input) => true;
    public bool MipStatisticsSubgroupReduction => true;
    public bool RuntimeCallRecordsSupported => true;
    public bool CallMarksSupported => true;
    public bool TableBlocks => Environment.GetEnvironmentVariable("SHARPEMU_TABLE_BLOCKS") != "0";
    public bool GraphicsSubgroupOperationsEnabled => Environment.GetEnvironmentVariable("SHARPEMU_GRAPHICS_SUBGROUPS") != "0";
    public bool SharedInt64AtomicsEnabled => true;
    public bool ImageInt64AtomicsEnabled => true;
    public bool ShaderSignedZeroInfNanPreserveFloat32Supported => true;
    public bool ExactFloat16ConversionSupported => true;
    public bool ExecGuardElisionEnabled => false; // NVIDIA NVVM rejects the elided wave64 form.
    public bool PerVertexPixelInputsSupported => true;
    public bool ClipDistanceEnabled => true;
    public bool ShaderDrawParametersSupported => true;
    public bool MeshShadersSupported => true;
    public MeshShaderLimits MeshLimits => new(128, 256, 256, 28672, 4194304, 65535, 4194304, 128, 32768, 48128, 32, 32, 128);
    public uint MeshSubgroupSize => 32;
    public RenderHostLimits Limits => new(32768, 32768, 32768, 32768);
    public bool SupportsDualSourceBlend => true;
    public SampleCountFlags NoAttachmentSampleCounts => (SampleCountFlags)31;
    public bool TryResolveColorOutput(uint dataFormat, uint numberType, uint componentSwap,
        out Gen5PixelOutputKind outputKind, out Gen5ColorComponentMapping componentMapping)
    {
        if (VulkanVideoPresenter.TryDecodeRenderTargetFormat(dataFormat, numberType, componentSwap, out var format))
        {
            outputKind = format.OutputKind;
            componentMapping = format.ExportMapping;
            return true;
        }
        outputKind = default;
        componentMapping = default;
        return false;
    }
    public bool TryReadGuestWord(ulong address, out uint word) { word = 0; return false; }
    public bool TryReadCleanGuestWord(ulong address, out uint word) { word = 0; return false; }
    public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId) => 1;
    public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description) => throw new NotSupportedException();
    public PipelineHandle CreateComputePipeline(ComputePipelineDescription description) => throw new NotSupportedException();
}
