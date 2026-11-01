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
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class DeferredGpuWrittenTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly Type RequestsType = typeof(VulkanVideoPresenter).GetNestedType("SliceRequests", BindingFlags.NonPublic)!;
    private static readonly Type SliceType = typeof(VulkanVideoPresenter).GetNestedType("RenderSlice", BindingFlags.NonPublic)!;
    private static readonly Type SegmentType = typeof(VulkanVideoPresenter).GetNestedType("SliceSegment", BindingFlags.NonPublic)!;
    private static object New(Type type) => Activator.CreateInstance(type, nonPublic: true)!;
    private static List<GpuWrittenSliceMark> Marks(object requests) => (List<GpuWrittenSliceMark>)RequestsType.GetField("GpuWritten")!.GetValue(requests)!;

    private static ResourceSlotIdentifier Depth(CacheHarness h, ulong address)
    {
        var request = AsDepthTarget(LinearRequest(address, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        request.Description.Stencil = new GuestSpan(address + 0x1000, 4);
        request.View = request.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        return h.Acquire(ref request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void JoinMarkMatchesSerialOwnershipAndNextItemReadsWrittenStencil(bool deferred)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var address = h.MapBacked(0x10000, ReadWrite);
        var id = Depth(h, address);
        Assert.True(h.Images.TryCaptureGpuWrittenMark(id, out var mark));
        var requests = New(RequestsType);
        Marks(requests).Add(mark);
        p.Run(() =>
        {
            var depth = h.Image(id);
            depth.ClearGpuModified();
            mark.ExpectedStencil.ClearGpuModified();
            depth.MarkBufferModified();
            mark.ExpectedStencil.MarkBufferModified();
            var command = new CommandBuffer(h.Scheduler.Current.Handle);
            depth.Transition(AccessFlags.TransferWriteBit, null, command);
            var clear = new ClearDepthStencilValue(0.5f, 0x6d);
            var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, 0, 1, 0, 1);
            fixture.Vulkan!.Vk.CmdClearDepthStencilImage(command, depth.Backing.Handle, ImageLayout.General, &clear, 1, &range);
            if (deferred) Assert.True((bool)p.InvokeMethod("ApplyGpuWrittenMarks", requests)!);
            else h.Images.MarkGpuWritten(id);
            Assert.True(depth.IsGpuModified);
            Assert.True(mark.ExpectedStencil.IsGpuModified);
            Assert.False(depth.IsBufferModified);
            Assert.False(mark.ExpectedStencil.IsBufferModified);
            Assert.False(depth.IsCpuDirty);
            Assert.False(mark.ExpectedStencil.IsCpuDirty);
            Assert.False(depth.BufferHoldsGpuContents);
            Assert.False(mark.ExpectedStencil.RawWriteAfterGpuWrite);
            Assert.Equal(id, mark.ExpectedStencil.DepthOwner);
            Assert.True(mark.ExpectedStencil.GpuWriteSequence > depth.GpuWriteSequence);
        });
        // The implementation ends the recording slice at the marked item. This next
        // consumer is prepared after join, so proxy publication sees the new owner.
        var plane = LinearRequest(address + 0x1000, 4, Format.R8Uint, GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 1, 1);
        var read = h.Acquire(ref plane);
        Assert.Equal(new byte[] { 0x6d, 0x6d, 0x6d, 0x6d }, h.ReadImageBytes(h.Image(read)));
        h.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedStencilAssociationRejectsSegmentBeforeApplyingAnyMarks(bool changedExtent)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var address = h.MapBacked(0x10000, ReadWrite);
        var id = Depth(h, address);
        Assert.True(h.Images.TryCaptureGpuWrittenMark(id, out var mark));
        var segment = New(SegmentType);
        var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
        Marks(requests).Add(mark);
        p.Run(() =>
        {
            var sequence = mark.Expected.GpuWriteSequence;
            var extent = mark.ExpectedStencil.Description.Extent;
            if (changedExtent) mark.ExpectedStencil.Description.Extent = new Extent3D(8, 1, 1);
            else mark.ExpectedStencil.AssociateDepth(new ResourceSlotIdentifier(1234, 1));
            Assert.False(h.Images.TryCaptureGpuWrittenMark(id, out _));
            Assert.False((bool)p.InvokeMethod("ValidateSegment", New(SliceType), segment)!);
            Assert.False((bool)p.InvokeMethod("ApplyGpuWrittenMarks", requests)!);
            Assert.Equal(sequence, mark.Expected.GpuWriteSequence);
            mark.ExpectedStencil.AssociateDepth(id);
            mark.ExpectedStencil.Description.Extent = extent;
        });
        h.Shutdown();
    }

    [Fact]
    public void SuccessfulMarkedItemEndsRecordingBeforeNextConsumerAndRejectsSpeculationFromOldEpoch()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var id = Depth(h, h.MapBacked(0x10000, ReadWrite));
        Assert.True(h.Images.TryCaptureGpuWrittenMark(id, out var mark));
        var slice = New(SliceType);
        var segment = New(SegmentType);
        var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
        var boundary = PresenterUnderTest.RecordingHostType.GetMethod("EndSliceAtGpuWritten", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False((bool)boundary.Invoke(null, [slice, segment, 2, 0])!);
        Marks(requests).Add(mark);
        Assert.True((bool)boundary.Invoke(null, [slice, segment, 2, 0])!);
        Assert.True((bool)SliceType.GetField("GpuWrittenBoundary")!.GetValue(slice)!);
        Assert.Equal(3, (int)SliceType.GetField("Resume")!.GetValue(slice)!);
        Assert.Null(SegmentType.GetField("SerialReason")!.GetValue(segment));
        p.Run(() =>
        {
            Assert.True((bool)p.InvokeMethod("ApplyGpuWrittenMarks", requests)!);
            Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_GPU_WRITTEN") == "1" ? 1L : 0L,
                p.GetField<long>("GpuWrittenEpoch"));
            if (Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_GPU_WRITTEN") == "1")
                Assert.False((bool)p.InvokeMethod("ValidateSegment", slice, segment)!);
        });
        h.Shutdown();
    }

    [Fact]
    public void WorkerRecordsMarkWithoutChangingOwnershipAndDefaultRetainsOriginalCut()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var h = new CacheHarness(fixture.Vulkan);
        var id = Depth(h, h.MapBacked(0x10000, ReadWrite));
        var host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
        PresenterUnderTest.RecordingHostType.GetField("_worker", PresenterUnderTest.InstanceMembers)!.SetValue(host, true);
        PresenterUnderTest.RecordingHostType.GetField("_imageCache", PresenterUnderTest.InstanceMembers)!.SetValue(host, h.Images);
        var segment = New(SegmentType);
        PresenterUnderTest.RecordingHostType.GetField("_segment", PresenterUnderTest.InstanceMembers)!.SetValue(host, segment);
        var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
        var before = h.Image(id).GpuWriteSequence;
        if (Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_GPU_WRITTEN") == "1")
        {
            ((IRenderHost)host).MarkGpuWritten(id);
            Assert.Single(Marks(requests));
            Assert.Equal(before, h.Image(id).GpuWriteSequence);
        }
        else
        {
            var cut = Assert.Throws<SerialRequiredException>(() => ((IRenderHost)host).MarkGpuWritten(id));
            Assert.Equal("mark gpu written", cut.Message);
            Assert.False(cut.Resumable);
            Assert.Empty(Marks(requests));
        }
        h.Shutdown();
    }

    [Theory]
    [InlineData("unrelated", true)]
    [InlineData("buffer-stencil-read", false)]
    [InlineData("buffer-depth-write", false)]
    [InlineData("image-target", false)]
    [InlineData("stream-read", false)]
    [InlineData("unrecorded", false)]
    public void OwnershipHistoryRejectsOnlyOverlappingOrUnrecordedSegments(string access, bool survives)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_GPU_WRITTEN") != "1") return;
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        var address = h.MapBacked(0x10000, ReadWrite);
        var id = Depth(h, address);
        Assert.True(h.Images.TryCaptureGpuWrittenMark(id, out var mark));
        var segment = New(SegmentType);
        var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
        RequestsType.GetField("BufferSerial")!.SetValue(requests, h.Cache.RegistrationSerial);
        RequestsType.GetField("LookupGeneration")!.SetValue(requests, h.Images.LookupGeneration);
        RequestsType.GetField("MetadataGeneration")!.SetValue(requests, h.Images.MetadataGeneration);
        var buffers = (List<BufferSliceUse>)RequestsType.GetField("Buffers")!.GetValue(requests)!;
        var reads = (List<GuestSpan>)RequestsType.GetField("OwnershipReads")!.GetValue(requests)!;
        switch (access)
        {
            case "unrelated": buffers.Add(new(address + 0x8000, 16, false, false, false, null!)); break;
            case "buffer-stencil-read": buffers.Add(new(mark.StencilRange.Address, 1, false, false, false, null!)); break;
            case "buffer-depth-write": buffers.Add(new(mark.Data.Address, 1, true, false, false, null!)); break;
            case "stream-read": reads.Add(new(mark.StencilRange.Address - 1, 2)); break;
            case "image-target":
                var images = (List<ImageSliceUse>)RequestsType.GetField("Images")!.GetValue(requests)!;
                var request = AsDepthTarget(LinearRequest(address, 16, Format.D32SfloatS8Uint,
                    GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
                images.Add(new(id, mark.Expected, request));
                break;
            case "unrecorded": RequestsType.GetField("UnrecordedOwnershipAccess")!.SetValue(requests, true); break;
        }
        p.Run(() =>
        {
            Assert.True((bool)p.InvokeMethod("ValidateOwnership", requests)!);
            // Use the serial wrapper too: inline replay must enter the same history
            // as deferred join, including the stencil proxy's guest span.
            p.InvokeMethod("MarkGpuWrittenOrdered", id);
            var enabled = Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_GPU_WRITTEN") == "1";
            Assert.Equal(!enabled || survives, (bool)p.InvokeMethod("ValidateOwnership", requests)!);
            Assert.Equal(!enabled || survives, (bool)p.InvokeMethod("ValidateSegment", New(SliceType), segment)!);
            // New segments begin after this change and do not conflict with it.
            RequestsType.GetField("GpuWrittenEpoch")!.SetValue(requests, p.GetField<long>("GpuWrittenEpoch"));
            Assert.True((bool)p.InvokeMethod("ValidateOwnership", requests)!);
        });
        h.Shutdown();
    }
}
