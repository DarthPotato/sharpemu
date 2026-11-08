// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    private sealed class TinyUploadCapacities : IDisposable
    {
        private readonly string? _arena = Environment.GetEnvironmentVariable("SHARPEMU_CONTENT_ARENA_HALF_BYTES");
        private readonly string? _stream = Environment.GetEnvironmentVariable("SHARPEMU_STREAM_RING_BYTES");

        public TinyUploadCapacities()
        {
            Environment.SetEnvironmentVariable("SHARPEMU_CONTENT_ARENA_HALF_BYTES", "4096");
            Environment.SetEnvironmentVariable("SHARPEMU_STREAM_RING_BYTES", "4096");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SHARPEMU_CONTENT_ARENA_HALF_BYTES", _arena);
            Environment.SetEnvironmentVariable("SHARPEMU_STREAM_RING_BYTES", _stream);
        }
    }

    private static byte[] UploadPattern(byte value) => Enumerable.Repeat(value, 4096).ToArray();

    private static void PinArenaHalves(ContentUploadArena arena, SubmissionScheduler scheduler, byte seed)
    {
        var first = UploadPattern(seed);
        Assert.True(arena.TryUploadAddress(first, 16, out var address));
        scheduler.Finish();
        Assert.True(arena.TryUploadAddress(UploadPattern((byte)(seed + 1)), 16, out var second));
        Assert.NotEqual(address, second);
        // Cache hits pin the inactive half to the current tick as well.
        Assert.True(arena.TryUploadAddress(first, 16, out var hit));
        Assert.Equal(address, hit);
        Assert.False(arena.TryUploadAddress(new byte[1048], 16, out _, wait: true));
    }

    private static VulkanHostBufferAllocation[] HostAllocations(PresenterUnderTest presenter) =>
        ((Dictionary<ulong, VulkanHostBufferAllocation>)typeof(VulkanHostBufferPool)
            .GetField("_allocations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter.HostBuffers)!).Values.ToArray();

    private static IList PreparationOverflows(IResourcePreparation preparation) =>
        (IList)preparation.GetType().GetProperty("OverflowBuffers")!.GetValue(preparation)!;

    [Fact]
    public void TableOverflow_DrawReadsItsDeviceAddressAndSubmissionRecyclesItForReuse()
    {
        if (!Ready() || !_vulkan!.ShaderInt64) return;
        using var capacities = new TinyUploadCapacities();
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var arena = new ContentUploadArena(_vulkan!.DeviceInfo, presenter.Harness.Scheduler);
        presenter.SetField("_contentUploads", arena);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var target = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(vertices, Triangle(-1, -1, 3, -1, -1, 3));
        var words = RegisterWords.Color(target, Size, Size);
        var layout = BindingLayout.Allocate(new ShaderResourceInfo(), [], false, true, false, tableBlock: true);
        Assert.True(layout.UsesTableBlock && layout.UsesPushData);
        uint[] flattened = Enumerable.Range(0, 261).Select(index => (uint)index).ToArray();
        flattened[3] = 0x3F800000; // table byte offset 16: opaque red float4
        flattened[4] = flattened[5] = 0;
        flattened[6] = 0x3F800000;
        var expectedBytes = MemoryMarshal.AsBytes<uint>(new uint[] { 261 }.Concat(flattened).ToArray()).ToArray();
        Assert.Equal(1048, expectedBytes.Length);
        var executor = new RenderExecutor(presenter.RenderHost,
            new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
                interpolationShader: CreateTableColorFragment(layout), pixelBindings: layout, flattenedTable: flattened));
        ulong firstHandle = 0;
        for (byte round = 0; round < 2; round++)
        {
            presenter.Run(() =>
            {
                PinArenaHalves(arena, harness.Scheduler, (byte)(10 + round * 2));
                var stream = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
                Assert.Equal(4096UL, stream.Size);
                Assert.True(stream.TryMap(stream.Size, out var offset));
                stream.Mapped.Clear();
                stream.Commit();
                using var retained = stream.RetainContents();
                Assert.False(stream.TryMap(1048, out _, 16));
                var tick = harness.Scheduler.CurrentTick;
                executor.DrawAuto((ulong)(round + 1), Banks(words), Draw());
                // Overflow must not submit/wait for the tick containing the unrecorded draw.
                Assert.Equal(tick, harness.Scheduler.CurrentTick);
                var allocation = Assert.Single(HostAllocations(presenter));
                Assert.Equal(expectedBytes, new ReadOnlySpan<byte>((void*)allocation.Mapped, 1048).ToArray());
                var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = allocation.Buffer };
                var address = _vulkan.Vk.GetBufferDeviceAddress(_vulkan.Device, &info);
                Assert.NotEqual(0UL, address);
                Assert.Equal(0UL, address % 16);
                if (round == 0) firstHandle = allocation.Buffer.Handle;
                else Assert.Equal(firstHandle, allocation.Buffer.Handle);
                var resources = Assert.Single(presenter.GetField<IEnumerable>("_batchResources").Cast<object>());
                var overflow = ((Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory)[])resources.GetType().GetField("OverflowBuffers")!.GetValue(resources)!;
                var owned = Assert.Single(overflow);
                Assert.Equal(allocation.Buffer.Handle, owned.Buffer.Handle);
                Assert.Equal(allocation.Memory.Handle, owned.Memory.Handle);
                Assert.Empty(PreparationOverflows(presenter.GetField<IResourcePreparation>("_sparePreparation")).Cast<object>());
                Assert.Equal(0UL, presenter.HostBuffers.CachedBytes);
                Assert.Empty(presenter.HostBufferDestructions);
            });
            presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
            presenter.Run(() =>
            {
                Assert.Single(presenter.GetField<IEnumerable>("_pendingGuestSubmissions").Cast<object>());
                Assert.Equal(0UL, presenter.HostBuffers.CachedBytes);
                harness.Scheduler.Finish();
                presenter.InvokeMethod("CollectCompletedGuestSubmissions", false);
                Assert.Empty(presenter.GetField<IEnumerable>("_pendingGuestSubmissions").Cast<object>());
                Assert.Equal(2048UL, presenter.HostBuffers.CachedBytes);
                presenter.InvokeMethod("CollectCompletedGuestSubmissions", false);
                Assert.Equal(2048UL, presenter.HostBuffers.CachedBytes);
                Assert.Empty(presenter.HostBufferDestructions);
            });
            var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
            Assert.Equal(Red, Pixel(pixels, Size / 2, Size / 2));
        }
        presenter.Run(() =>
        {
            presenter.HostBuffers.Dispose();
            presenter.HostBuffers.Dispose();
            Assert.Equal(firstHandle, Assert.Single(presenter.HostBufferDestructions).Buffer.Handle);
            Assert.Empty(HostAllocations(presenter));
        });
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(2048UL)]
    public void TableOverflow_AbortedPreparationReturnsExactlyOnceAndRespectsPoolBudget(ulong cacheBudget)
    {
        if (!Ready()) return;
        using var capacities = new TinyUploadCapacities();
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan!, hostBufferCacheBytes: cacheBudget);
        using var arena = new ContentUploadArena(_vulkan!.DeviceInfo, presenter.Harness.Scheduler);
        presenter.SetField("_contentUploads", arena);
        presenter.Run(() =>
        {
            PinArenaHalves(arena, presenter.Harness.Scheduler, 30);
            var stream = presenter.Harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
            // Near the end before preparation; its first committed allocation makes retention effective.
            stream.Copy(new byte[3072], 16);
            var preparation = presenter.RenderHost.BeginPreparation();
            try
            {
                stream.Copy(new byte[1024], 16);
                var layout = BindingLayout.Allocate(new ShaderResourceInfo(), [], false, true, false, tableBlock: true);
                var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Pixel, 0xAA, bindings: layout);
                var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program,
                    new ResourceSnapshot { FlattenedResourceTable = new uint[261] }));
                var tick = presenter.Harness.Scheduler.CurrentTick;
                presenter.RenderHost.BindResources(prepared);
                Assert.Equal(tick, presenter.Harness.Scheduler.CurrentTick);
                Assert.Single(PreparationOverflows(preparation).Cast<object>());
                Assert.Equal(0UL, presenter.HostBuffers.CachedBytes);
                Assert.Empty(presenter.HostBufferDestructions);
                Assert.False(stream.TryMap(1048, out _, 16));
            }
            finally
            {
                preparation.Dispose();
                preparation.Dispose();
            }
            Assert.Empty(PreparationOverflows(preparation).Cast<object>());
            Assert.Empty(presenter.GetField<IEnumerable>("_batchResources").Cast<object>());
            Assert.Equal(cacheBudget, presenter.HostBuffers.CachedBytes);
            Assert.Equal(cacheBudget == 0 ? 1 : 0, presenter.HostBufferDestructions.Count);
            presenter.HostBuffers.Dispose();
            presenter.HostBuffers.Dispose();
            Assert.Single(presenter.HostBufferDestructions);
            Assert.Empty(HostAllocations(presenter));
        });
    }

    [Fact]
    public void TableOverflow_AllocationFailureDoesNotReclaimOrWaitOnCurrentPreparation()
    {
        if (!Ready()) return;
        using var capacities = new TinyUploadCapacities();
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var arena = new ContentUploadArena(_vulkan!.DeviceInfo, presenter.Harness.Scheduler);
        presenter.SetField("_contentUploads", arena);
        presenter.Run(() =>
        {
            var scheduler = presenter.Harness.Scheduler;
            PinArenaHalves(arena, scheduler, 40);
            var stream = presenter.Harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
            stream.Copy(new byte[3072], 16);
            using var preparation = presenter.RenderHost.BeginPreparation();
            stream.Copy(new byte[1024], 16);
            var layout = BindingLayout.Allocate(new ShaderResourceInfo(), [], false, true, false, tableBlock: true);
            var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Pixel, 0xBB, bindings: layout);
            var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program,
                new ResourceSnapshot { FlattenedResourceTable = new uint[261] }));
            var tick = scheduler.CurrentTick;
            var device = _vulkan!.DeviceInfo;
            var live = device.LiveAllocations;
            var refusals = 0;
            var previousFailure = device.AllocationFailure;
            device.AllocationFailure = _ => { refusals++; return true; };
            try
            {
                var failure = Assert.Throws<InvalidOperationException>(() => presenter.RenderHost.BindResources(prepared));
                Assert.Contains("vkAllocateMemory", failure.Message, StringComparison.Ordinal);
                Assert.Contains("ErrorOutOfDeviceMemory", failure.Message, StringComparison.Ordinal);
                Assert.Equal(1, refusals);
                Assert.Equal(tick, scheduler.CurrentTick);
                Assert.Equal(live, device.LiveAllocations);
                Assert.Empty(PreparationOverflows(preparation).Cast<object>());
                Assert.Empty(HostAllocations(presenter));
                Assert.False(stream.TryMap(1048, out _, 16));
            }
            finally
            {
                device.AllocationFailure = previousFailure;
            }

            // A failed allocation leaves neither a phantom owner nor a damaged preparation.
            presenter.RenderHost.BindResources(prepared);
            Assert.Single(PreparationOverflows(preparation).Cast<object>());
            Assert.Equal(tick, scheduler.CurrentTick);
            preparation.Dispose();
            Assert.Equal(2048UL, presenter.HostBuffers.CachedBytes);
            presenter.HostBuffers.Dispose();
            Assert.Single(presenter.HostBufferDestructions);
        });
    }

    [Fact]
    public void TinyContentArenaRejectsOversizedContentWithoutWritingPastEitherHalf()
    {
        if (!Ready()) return;
        using var capacities = new TinyUploadCapacities();
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.Run(() =>
        {
            using var arena = new ContentUploadArena(_vulkan!.DeviceInfo, presenter.Harness.Scheduler);
            Assert.False(arena.TryUpload(new byte[4097], 16, out _, out _));
            Assert.False(arena.TryUpload(new uint[1025], 16, out _, out _));
            Assert.True(arena.TryUpload(UploadPattern(1), 16, out _, out var offset));
            Assert.Equal(0UL, offset);
            presenter.Harness.Scheduler.Finish();
        });
    }

    // Read the actual pushed table address on the GPU, rather than just rendering a constant.
    private static byte[] CreateTableColorFragment(BindingLayout layout)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        module.AddCapability(SpirvCapability.Int64);
        module.AddCapability(SpirvCapability.PhysicalStorageBufferAddresses);
        module.SetPhysicalStorageBuffer64MemoryModel();
        var u32 = module.TypeInt(32, false);
        var u64 = module.TypeInt(64, false);
        var f32 = module.TypeFloat(32);
        var vec4 = module.TypeVector(f32, 4);
        var pushType = module.TypeStruct(u64);
        module.AddDecoration(pushType, SpirvDecoration.Block);
        module.AddMemberDecoration(pushType, 0, SpirvDecoration.Offset,
            (layout.PushDataStartDword + layout.TableBlockDword) * 4);
        var push = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.PushConstant, pushType), SpirvStorageClass.PushConstant);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, vec4), SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var voidType = module.TypeVoid();
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        var pointer = module.AddInstruction(SpirvOp.AccessChain, module.TypePointer(SpirvStorageClass.PushConstant, u64), push, module.Constant(u32, 0));
        var address = module.AddInstruction(SpirvOp.Load, u64, pointer);
        var colorAddress = module.AddInstruction(SpirvOp.IAdd, u64, address, module.Constant64(u64, 16));
        var colorPointer = module.AddInstruction(SpirvOp.ConvertUToPtr, module.TypePointer(SpirvStorageClass.PhysicalStorageBuffer, vec4), colorAddress);
        var color = module.AddInstruction(SpirvOp.Load, vec4, colorPointer, 2, 16); // Aligned 16
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [push, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }
}
