// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;
using Silk.NET.Vulkan;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class TessBdaWorkerTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly Type SegmentType = typeof(VulkanVideoPresenter).GetNestedType("SliceSegment", BindingFlags.NonPublic)!;
    private static object New(Type t) => Activator.CreateInstance(t, nonPublic: true)!;
    private static object Requests(object segment) => SegmentType.GetField("Requests")!.GetValue(segment)!;
    private static List<BufferDeviceAddressLease> Leases(object segment) =>
        (List<BufferDeviceAddressLease>)Requests(segment).GetType().GetField("TessBdaLeases")!.GetValue(Requests(segment))!;
    private static IRenderHost Worker(PresenterUnderTest p, object segment)
    {
        var host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
        void Set(string name, object value) => PresenterUnderTest.RecordingHostType.GetField(name, PresenterUnderTest.InstanceMembers)!.SetValue(host, value);
        Set("_worker", true); Set("_presenter", p.Instance); Set("_bufferCache", p.Harness.Cache);
        Set("_imageCache", p.Harness.Images); Set("_buffers", p.Harness.Cache.RefreshSnapshot()); Set("_segment", segment);
        return (IRenderHost)host;
    }
    private static bool Enabled => Environment.GetEnvironmentVariable("SHARPEMU_TESS_BDA_WORKERS") == "1";
    private static void Release(object segment)
    {
        foreach (var lease in Leases(segment)) lease.Buffer.ReleaseForeignRead();
        Leases(segment).Clear();
    }

    [Fact]
    public void WritableLeaseAfterDeferredStencilMarkKeepsOverlappingOwnershipOrdered()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        if (!Enabled || Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_GPU_WRITTEN") != "1") return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var address = h.MapBacked(0x10000, ReadWrite);
        var request = AsDepthTarget(LinearRequest(address, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        request.Description.Stencil = new GuestSpan(address + 0x1000, 4);
        request.View = request.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var id = h.Acquire(ref request);
        var segment = New(SegmentType);
        var host = Worker(p, segment);
        try
        {
            host.MarkGpuWritten(id);
            Assert.Equal("tessellation device addresses", Assert.Throws<SerialRequiredException>(() =>
                host.ObtainBufferDeviceAddress(address + 0x1000, 4, true)).Message);
            Assert.Empty(Leases(segment));
        }
        finally { Release(segment); h.Shutdown(); }
    }

    [Fact]
    public void ResidentLeaseMatchesSerialAddressAndWritablePrologue()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var a = h.MapBacked(0x10000, ReadWrite);
        var serial = p.Run(() => p.RenderHost.ObtainBufferDeviceAddress(a + 32, 64, false));
        var segment = New(SegmentType);
        var host = Worker(p, segment);
        try
        {
            if (!Enabled)
            {
                Assert.False(Assert.Throws<SerialRequiredException>(() => host.ObtainBufferDeviceAddress(a + 32, 64, true)).Resumable);
                return;
            }
            host.PrepareBufferAllocations([new GuestSpan(a + 32, 64)]);
            Assert.Equal(serial, host.ObtainBufferDeviceAddress(a + 32, 64, true));
            Assert.All(Leases(segment), lease => Assert.True(p.Run(() => h.Cache.ValidateDeviceAddressLease(lease))));
            p.Run(() => p.InvokeMethod("RecordSlicePrologue", Requests(segment)));
            Assert.True(p.Run(() => h.Cache.HasGpuDirtyBytes(a + 32, 64)));
            Assert.True((bool)p.Run(() => p.InvokeMethod("ValidateTessBdaLeases", Requests(segment)))!);
        }
        finally { Release(segment); h.Shutdown(); }
    }

    [Fact]
    public void GrowthMergeAndMissingAllocationKeepSerialCutIncludingAdjacentRanges()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var a = h.MapBacked(0x20000, ReadWrite);
        p.Run(() => h.Cache.FindBuffer(a, 4));
        p.Run(() => h.Cache.FindBuffer(a + 0x8000, 4));
        var segment = New(SegmentType);
        var host = Worker(p, segment);
        try
        {
            Assert.False(Assert.Throws<SerialRequiredException>(() => host.PrepareBufferAllocations([new(a, 0x8000)])).Resumable);
            Assert.Throws<SerialRequiredException>(() => host.PrepareBufferAllocations([new(a, 0xc000)]));
            Assert.Throws<SerialRequiredException>(() => host.PrepareBufferAllocations([new(a + 0x18000, 4)]));
            var snapshot = h.Cache.Snapshot;
            Assert.False(snapshot.StableAllocationRanges([new(a, 0x8000), new(a + 0x8000, 4)], out _, out _));
            Assert.Empty(Leases(segment));
        }
        finally { Release(segment); h.Shutdown(); }
    }

    [Fact]
    public void ChangedOwnerRejectsJoinAndOutstandingLeasePinsNativeBuffer()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var a = h.MapBacked(0x10000, ReadWrite);
        p.Run(() => h.Cache.FindBuffer(a, 4));
        var segment = New(SegmentType);
        var host = Worker(p, segment);
        try
        {
            if (!Enabled) return;
            _ = host.ObtainBufferDeviceAddress(a, 4, false);
            var lease = Assert.Single(Leases(segment));
            var handle = lease.Buffer.Handle;
            p.Run(() => h.Cache.FindBuffer(a, 0x8000));
            Assert.False((bool)p.Run(() => p.InvokeMethod("ValidateTessBdaLeases", Requests(segment)))!);
            // Simulate retirement destruction arriving while the worker's segment is unsubmitted.
            lease.Buffer.Dispose();
            Assert.Equal(handle, lease.Buffer.Handle);
            Assert.Equal(lease.DeviceAddress, lease.Buffer.DeviceAddress);
            Release(segment);
            Assert.Equal(0UL, lease.Buffer.Handle.Handle);
            Assert.False(lease.Buffer.TryRetainDeviceAddress());
        }
        finally { Release(segment); h.Shutdown(); }
    }
}
