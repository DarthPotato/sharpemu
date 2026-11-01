// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 7u)]
    [InlineData(true, 0u)]
    [InlineData(true, 7u)]
    public void GpuInstanceSnapshot_SurvivesArgumentReuseAndFeedsDirectDraw(bool indexed, uint instances)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var arguments = harness.MapBacked(0x10000, ReadWrite);
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var indices = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        harness.Write(indices, Bytes((ushort)0, (ushort)1, (ushort)2));
        var words = RegisterWords.Color(target, Size, Size);
        var executor = new RenderExecutor(presenter.RenderHost,
            new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices));
        presenter.Run(() =>
        {
            UpdateInstanceTestGpuWords(presenter, arguments, [3, instances, 0, 0, 0]);
            var state = new GpuInstanceState(new(), arguments);
            using (presenter.RenderHost.CaptureInstanceCount(state, indexed)) { }
            UpdateInstanceTestGpuWords(presenter, arguments, [3, 1030888894, 0, 0, 0]);
            state = state with { CaptureAddress = 0, Inherit = true };
            Assert.Equal(instances, presenter.RenderHost.ReadInheritedInstances(state));
            // Check every command word, including signed base vertex and first instance.
            using (presenter.RenderHost.BeginPreparation())
            {
                var binding = presenter.RenderHost.PrepareInheritedInstances(state,
                    indexed ? [11, 0, 0, unchecked((uint)-3), 5] : [11, 0, 3, 5]);
                AssertInstanceTestGpuWords(presenter, binding, indexed ? [11, instances, 0, unchecked((uint)-3), 5] : [11, instances, 3, 5]);
            }
            if (indexed)
                executor.DrawIndexed(1, Banks(words), new(0, 0, 3, indices, 0, 1, 0, 0, DrawOffsetSource.Packet, GpuInstances: state));
            else executor.DrawAuto(1, Banks(words), new(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet, GpuInstances: state));
            presenter.InvokeMethod("FlushBatchedGuestCommands");
        });
        harness.Finish();
        if (instances != 0)
            Assert.Equal(Red, Pixel(harness.ReadImageBytes(TargetImage(presenter, words)), Size / 2, Size / 2));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 1u)]
    [InlineData(false, 2u)]
    [InlineData(false, 5u)]
    [InlineData(true, 0u)]
    [InlineData(true, 1u)]
    [InlineData(true, 2u)]
    [InlineData(true, 5u)]
    public void GpuCountedInstances_ClampToMaximumLatchCountAndPreserveZero(bool indexed, uint count)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var arguments = harness.MapBacked(0x10000, ReadWrite);
        var countAddress = harness.MapBacked(0x10000, ReadWrite);
        presenter.Run(() =>
        {
            UpdateInstanceTestGpuWords(presenter, countAddress, [count]);
            var register = new GpuInstanceRegister();
            for (uint index = 0; index < 3; index++)
            {
                var address = arguments + index * 32;
                var instances = (index + 1) * 2;
                UpdateInstanceTestGpuWords(presenter, address, [3, instances, 0, 0, 0]);
                using var snapshot = presenter.RenderHost.CaptureInstanceCount(new(register, address,
                    CountAddress: countAddress, RecordIndex: index, Initialize: index == 0, InitialCount: 9), indexed);
                Assert.NotNull(snapshot);
                var expected = new uint[indexed ? 5 : 4];
                if (index < count) { expected[0] = 3; expected[1] = instances; }
                AssertInstanceTestGpuWords(presenter, snapshot.Arguments, expected);
                // The packet latches its draw count before executing its records.
                if (index == 0) UpdateInstanceTestGpuWords(presenter, countAddress, [0]);
            }
            using var preparation = presenter.RenderHost.BeginPreparation();
            var inherited = presenter.RenderHost.PrepareInheritedInstances(new(register, Inherit: true), [3, 0, 0, 0]);
            AssertInstanceTestGpuWords(presenter, inherited, [3, count == 0 ? 9 : Math.Min(count, 3) * 2, 0, 0]);
        });
        harness.Shutdown();
    }

    [Fact]
    public void GpuInstanceCopies_RecordInWorkerCommandBufferAndArena()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        using var pool = new SliceArenaPool(_vulkan!.DeviceInfo, harness.Scheduler);
        presenter.Run(() =>
        {
            UpdateInstanceTestGpuWords(presenter, address, [3, 7, 0, 0, 0]);
            var instance = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
            void Set(string name, object value) => PresenterUnderTest.RecordingHostType.GetField(name, PresenterUnderTest.InstanceMembers)!.SetValue(instance, value);
            var host = (IRenderHost)instance;
            var texturesField = PresenterUnderTest.RecordingHostType.GetField("_preparedTextures", PresenterUnderTest.InstanceMembers)!;
            texturesField.SetValue(instance, Activator.CreateInstance(texturesField.FieldType, nonPublic: true));
            Set("_worker", true); Set("_presenter", presenter.Instance); Set("_vk", _vulkan.Vk);
            Set("_deviceInfo", _vulkan.DeviceInfo); Set("_scheduler", harness.Scheduler);
            Set("_bufferCache", harness.Cache); Set("_buffers", harness.Cache.RefreshSnapshot());
            Set("_arena", new SliceArena(pool, _vulkan.DeviceInfo.NonCoherentAtomSize));
            var segmentType = typeof(VulkanVideoPresenter).GetNestedType("SliceSegment", System.Reflection.BindingFlags.NonPublic)!;
            Set("_segment", Activator.CreateInstance(segmentType, nonPublic: true)!);
            var recording = harness.Scheduler.CreateRecordingBuffer((IRenderingState)instance);
            Set("_workerBuffer", recording);
            harness.Scheduler.BeginRecording(recording);
            var state = new GpuInstanceState(new(), address);
            using (host.CaptureInstanceCount(state, indexed: true)) { }
            BufferBinding binding;
            using (host.BeginPreparation())
                binding = host.PrepareInheritedInstances(state with { CaptureAddress = 0, Inherit = true }, [11, 0, 0, 0, 0]);
            harness.Scheduler.EndRecording(recording);
            harness.Scheduler.Flush(new SubmitBundle(), [recording]);
            AssertInstanceTestGpuWords(presenter, binding, [11, 7, 0, 0, 0]);
        });
        harness.Shutdown();
    }

    private void UpdateInstanceTestGpuWords(PresenterUnderTest presenter, ulong address, uint[] words)
    {
        var (buffer, offset) = presenter.Harness.Cache.ObtainBuffer(address, (ulong)words.Length * 4, isWritten: true);
        var command = (CommandBuffer)presenter.InvokeMethod("BeginBatchedGuestCommands")!;
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, DstAccessMask = AccessFlags.TransferWriteBit };
        _vulkan!.Vk.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
            0, 1, &barrier, 0, null, 0, null);
        fixed (uint* data = words) _vulkan.Vk.CmdUpdateBuffer(command, buffer.Handle, offset, (ulong)words.Length * 4, data);
    }

    private void AssertInstanceTestGpuWords(PresenterUnderTest presenter, BufferBinding source, uint[] expected)
    {
        using var download = new GpuBuffer(_vulkan!.DeviceInfo, presenter.Harness.Scheduler,
            GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, (ulong)expected.Length * 4);
        var command = (CommandBuffer)presenter.InvokeMethod("BeginBatchedGuestCommands")!;
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit, DstAccessMask = AccessFlags.TransferReadBit };
        _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
            0, 1, &barrier, 0, null, 0, null);
        var copy = new BufferCopy(source.Offset, 0, (ulong)expected.Length * 4);
        _vulkan.Vk.CmdCopyBuffer(command, new(source.Handle), download.Handle, 1, &copy);
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit; barrier.DstAccessMask = AccessFlags.HostReadBit;
        _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
            0, 1, &barrier, 0, null, 0, null);
        presenter.InvokeMethod("FlushBatchedGuestCommands");
        presenter.Harness.Scheduler.Finish();
        download.Invalidate(0, download.Size);
        Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(download.Mapped[..(int)download.Size]).ToArray());
    }
}
