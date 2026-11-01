// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed unsafe class InstanceCountPipeline : IDisposable
{
    private readonly GpuDeviceInfo _device;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private RecycledDescriptorSets? _sets;
    public InstanceCountPipeline(GpuDeviceInfo device)
    {
        _device = device;
        ShaderModule module = default;
        try
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[4];
            for (uint index = 0; index < 4; index++)
                bindings[index] = new DescriptorSetLayoutBinding(index, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            var setInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings,
            };
            Check(device.Vk.CreateDescriptorSetLayout(device.Device, &setInfo, null, out _setLayout));
            _sets = new RecycledDescriptorSets(device, _setLayout, 4);
            var setLayout = _setLayout;
            var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, SpirvInstanceCount.PushWordCount * 4);
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
                PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
            };
            Check(device.Vk.CreatePipelineLayout(device.Device, &layoutInfo, null, out _layout));
            var spirv = SpirvInstanceCount.Create();
            fixed (byte* code = spirv)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code,
                };
                Check(device.Vk.CreateShaderModule(device.Device, &moduleInfo, null, out module));
            }
            fixed (byte* entry = "main\0"u8)
            {
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo, Layout = _layout,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                        Module = module, PName = entry,
                    },
                };
                Check(device.Vk.CreateComputePipelines(device.Device, default, 1, &pipelineInfo, null, out _pipeline));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (module.Handle != 0) device.Vk.DestroyShaderModule(device.Device, module, null);
        }
    }

    public void Record(SubmissionScheduler scheduler, CommandBuffer command, ReadOnlySpan<BufferBinding> bindings, ReadOnlySpan<ulong> sizes, uint recordIndex, uint words)
    {
        var vk = _device.Vk;
        var set = _sets!.Rent();
        var infos = stackalloc DescriptorBufferInfo[4];
        var writes = stackalloc WriteDescriptorSet[4];
        var push = stackalloc uint[6];
        for (uint index = 0; index < 4; index++)
        {
            var binding = bindings[(int)index];
            var aligned = binding.Offset / _device.MinStorageBufferOffsetAlignment * _device.MinStorageBufferOffsetAlignment;
            infos[index] = new(new Silk.NET.Vulkan.Buffer(binding.Handle), aligned, binding.Offset - aligned + sizes[(int)index]);
            push[index] = checked((uint)((binding.Offset - aligned) / 4));
            writes[index] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set,
                DstBinding = index, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + index };
        }
        push[4] = recordIndex;
        push[5] = words;
        vk.UpdateDescriptorSets(_device.Device, 4, writes, 0, null);
        // Only the serial recorder uses descriptor sets here. Completion owns their reuse.
        var retainedSet = set;
        scheduler.QueueCompletionAction(() => _sets!.Return(retainedSet));
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit | AccessFlags.MemoryReadBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit };
        vk.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit,
            PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(command, _layout, ShaderStageFlags.ComputeBit, 0, 24, push);
        NativeBindingEpoch.Advance();
        vk.CmdDispatch(command, 1, 1, 1);
        barrier.SrcAccessMask = AccessFlags.ShaderWriteBit;
        barrier.DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
        vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit,
            0, 1, &barrier, 0, null, 0, null);
    }

    public void Dispose()
    {
        _sets?.Dispose();
        _sets = null;
        if (_pipeline.Handle != 0) _device.Vk.DestroyPipeline(_device.Device, _pipeline, null);
        if (_layout.Handle != 0) _device.Vk.DestroyPipelineLayout(_device.Device, _layout, null);
        if (_setLayout.Handle != 0) _device.Vk.DestroyDescriptorSetLayout(_device.Device, _setLayout, null);
        _pipeline = default; _layout = default; _setLayout = default;
    }

    private static void Check(Result result)
    {
        if (result != Result.Success) throw SubmissionScheduler.Fatal($"The instance-count pipeline could not be created: result={result}.");
    }
}
