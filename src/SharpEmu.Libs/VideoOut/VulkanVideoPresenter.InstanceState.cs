// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        internal InstanceCountPipeline? _instanceCountPipeline;
        internal readonly object _instanceRegisterGate = new();
        internal readonly Dictionary<GpuInstanceRegister, GpuBuffer> _instanceRegisters = new();

        private void DestroyInstanceRegisters()
        {
            _instanceCountPipeline?.Dispose();
            _instanceCountPipeline = null;
            lock (_instanceRegisterGate)
            {
                foreach (var buffer in _instanceRegisters.Values) buffer.Dispose();
                _instanceRegisters.Clear();
            }
        }
    }

    private sealed partial class RecordingHost
    {
        private GpuBuffer InstanceRegister(GpuInstanceState state)
        {
            if (state.Register is null) throw new ArgumentException("Missing CP instance register.");
            // Native allocation only: no scheduler or command-buffer mutation. Workers share this
            // device-local register, but their command buffers execute in guest stream order.
            lock (_presenter._instanceRegisterGate)
            {
                if (!_presenter._instanceRegisters.TryGetValue(state.Register, out var buffer))
                {
                    buffer = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.DeviceLocal,
                        0, GpuBuffer.AllFlags, 8);
                    _presenter._instanceRegisters.Add(state.Register, buffer);
                }
                return buffer;
            }
        }

        private void InstanceTransferBarrier(CommandBuffer command, bool after)
        {
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = after ? AccessFlags.TransferWriteBit : AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit,
                DstAccessMask = after ? AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit : AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
            };
            _vk.CmdPipelineBarrier(command,
                after ? PipelineStageFlags.TransferBit : PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit,
                after ? PipelineStageFlags.AllCommandsBit : PipelineStageFlags.TransferBit,
                0, 1, &barrier, 0, null, 0, null);
        }

        public IPreparedInstanceCount? CaptureInstanceCount(GpuInstanceState state, bool indexed)
        {
            if (state.CpuCount is { } cpuCount)
            {
                EndRendering();
                var cpuCommand = BeginBatchedGuestCommands();
                InstanceTransferBarrier(cpuCommand, after: false);
                _vk.CmdUpdateBuffer(cpuCommand, InstanceRegister(state).Handle, 0, 4, &cpuCount);
                InstanceTransferBarrier(cpuCommand, after: true);
                return null;
            }
            if (state.CountAddress != 0)
                return CaptureCountedInstances(state, indexed);
            using var preparation = BeginPreparation();
            var source = ObtainBuffer(state.CaptureAddress + 4, 4, false);
            var target = InstanceRegister(state);
            EndRendering();
            var command = BeginBatchedGuestCommands();
            InstanceTransferBarrier(command, after: false);
            var copy = new BufferCopy(source.Offset, 0, 4);
            _vk.CmdCopyBuffer(command, new(source.Handle), target.Handle, 1, &copy);
            InstanceTransferBarrier(command, after: true);
            return default;
        }

        private IPreparedInstanceCount CaptureCountedInstances(GpuInstanceState state, bool indexed)
        {
            // This rare path uses owner descriptor pools; ordinary captures and inherited draws slice normally.
            if (_worker) throw Serial("GPU counted instances", resumable: true);
            using var preparation = BeginPreparation();
            var words = indexed ? 5u : 4u;
            ((IRenderHost)this).PrepareBufferAllocations([new(state.CaptureAddress, words * 4), new(state.CountAddress, 4)]);
            var source = ObtainBuffer(state.CaptureAddress, words * 4, false);
            var count = ObtainBuffer(state.CountAddress, 4, false);
            var target = InstanceRegister(state);
            Span<byte> empty = stackalloc byte[20];
            empty.Clear();
            var retention = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream).RetainUpcomingAllocations();
            var output = UploadTransient(empty[..(int)(words * 4)], 16);
            EndRendering();
            var command = BeginBatchedGuestCommands();
            if (state.RecordIndex == 0)
            {
                InstanceTransferBarrier(command, after: false);
                var countCopy = new BufferCopy(count.Offset, 4, 4);
                _vk.CmdCopyBuffer(command, new(count.Handle), target.Handle, 1, &countCopy);
                InstanceTransferBarrier(command, after: true);
            }
            if (state.Initialize)
            {
                InstanceTransferBarrier(command, after: false);
                var value = state.InitialCount;
                _vk.CmdUpdateBuffer(command, target.Handle, 0, 4, &value);
                InstanceTransferBarrier(command, after: true);
            }
            var pipeline = _presenter._instanceCountPipeline ??= new InstanceCountPipeline(_deviceInfo);
            pipeline.Record(_scheduler, command, [source, new(target.Handle.Handle, 4), new(target.Handle.Handle, 0), output],
                [words * 4, 4, 4, words * 4], state.RecordIndex, words);
            var buffers = RequirePreparation().OverflowBuffers.ToArray();
            RequirePreparation().OverflowBuffers.Clear();
            return new PreparedInstanceCount(this, output, retention, buffers);
        }

        private sealed class PreparedInstanceCount(RecordingHost owner, BufferBinding arguments, IDisposable retention,
            (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory)[] overflow) : IPreparedInstanceCount
        {
            public BufferBinding Arguments => arguments;
            public void Dispose()
            {
                // Extend the ring watch to the final consumer's tick, even if preparation submitted.
                retention.Dispose();
                if (overflow.Length != 0)
                    owner._scheduler.QueueCompletionAction(() =>
                    {
                        foreach (var (buffer, memory) in overflow) owner._presenter.RecycleHostBuffer(buffer, memory);
                    });
            }
        }

        public BufferBinding PrepareInheritedInstances(GpuInstanceState state, ReadOnlySpan<uint> arguments)
        {
            var target = UploadTransient(MemoryMarshal.AsBytes(arguments), 16);
            var source = InstanceRegister(state);
            EndRendering();
            var command = BeginBatchedGuestCommands();
            InstanceTransferBarrier(command, after: false);
            var copy = new BufferCopy(0, target.Offset + 4, 4);
            _vk.CmdCopyBuffer(command, source.Handle, new(target.Handle), 1, &copy);
            InstanceTransferBarrier(command, after: true);
            return target;
        }

        public uint ReadInheritedInstances(GpuInstanceState state)
        {
            using var ordered = RenderPhaseProfile.MeasureMovableDetail(RenderPhaseProfile.Phase.OrderedGuestRead);
            RenderPhaseProfile.BlockPreparation(RenderPhaseProfile.PreparationBlock.GuestRead);
            // The current tessellation bridge sizes CPU batches from the instance count.
            // Its exceptional fallback reads the captured register, never a reused guest record.
            if (_worker) throw Serial("tessellation GPU instances", resumable: true);
            using var download = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download,
                0, GpuBuffer.AllFlags, 4);
            EndRendering();
            var command = BeginBatchedGuestCommands();
            InstanceTransferBarrier(command, after: false);
            var copy = new BufferCopy(0, 0, 4);
            _vk.CmdCopyBuffer(command, InstanceRegister(state).Handle, download.Handle, 1, &copy);
            var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit };
            _vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
                0, 1, &barrier, 0, null, 0, null);
            _scheduler.Finish();
            download.Invalidate(0, 4);
            var count = MemoryMarshal.Read<uint>(download.Mapped);
            if (count > (1u << 24))
                Console.Error.WriteLine($"[GPU][ERROR][DRAW_INSTANCE_COUNT] phase=render-cpu-expansion source=gpu-instance-register instances={count}");
            return count;
        }

    }
}
