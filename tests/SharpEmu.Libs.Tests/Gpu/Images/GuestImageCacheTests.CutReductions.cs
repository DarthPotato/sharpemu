// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(false, 0x100ul, 0x100ul, true)]
    [InlineData(false, 0ul, 0x8000ul, true)]
    [InlineData(false, 0ul, 0x10000ul, false)]
    [InlineData(true, 0ul, 0x10000ul, true)]
    [InlineData(true, 0x100ul, 0x100ul, true)]
    public void PublicationPreflightMatchesSerialSelection(bool formatted, ulong offset, ulong size, bool publishes)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var h = new CacheHarness(_vulkan);
        var address = h.MapBacked(0x10000, ReadWrite);
        var request = Color32(address, 8192);
        var id = h.Acquire(ref request);
        Assert.True(h.Worker.Run(() => h.Images.TryClearImageFromBuffer(address, 0x8000, 0x11111111)));
        var predicted = h.Images.WouldPublishImagesUnderWrite(address + offset, size, formatted);
        var exactEnabled = Environment.GetEnvironmentVariable("SHARPEMU_PUBLICATION_EXACT") != "0";
        Assert.Equal(publishes && (exactEnabled || formatted || offset != 0 || size < 0x8000), predicted);
        h.Worker.Run(() =>
        {
            if (formatted) h.Images.PublishImagesUnderFormattedWrite(address + offset, size);
            else h.Images.PublishImagesUnderRawWrite(address + offset, size);
        });
        Assert.Equal(publishes, !h.Image(id).IsGpuModified);
        if (exactEnabled && !predicted) Assert.True(h.Image(id).IsGpuModified);
        h.Shutdown();
    }

    [Fact]
    public void PublicationPreflightFiltersHandedOverNewerAlias()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var h = new CacheHarness(_vulkan);
        var address = h.MapBacked(0x10000, ReadWrite);
        var first = Color32(address, 8192);
        var firstId = h.Acquire(ref first);
        var second = LinearRequest(address, 0x8000, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm,
            GuestImageType.Color2D, new Extent3D(128, 64, 1), 1, 4, 1);
        var secondId = h.Acquire(ref second);
        h.Worker.Run(() =>
        {
            h.Image(firstId).MarkGpuModified();
            h.Image(secondId).MarkGpuModified();
            h.Image(secondId).ClearGpuModified();
        });
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_PUBLICATION_EXACT") == "0",
            h.Images.WouldPublishImagesUnderWrite(address + 4, 4, false));
        h.Worker.Run(() => h.Images.PublishImagesUnderRawWrite(address + 4, 4));
        Assert.True(h.Image(firstId).IsGpuModified);
        h.Shutdown();
    }

    [Fact]
    public void PublicationPreflightIncludesStencilDepthProxy()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var h = new CacheHarness(_vulkan);
        var address = h.MapBacked(0x20000, ReadWrite);
        var stencil = address + 0x10000;
        var depth = AsDepthTarget(LinearRequest(address, 16, Format.D32SfloatS8Uint, GuestPixelFormat.Bits32Float,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        depth.Description.Stencil = new GuestSpan(stencil, 4);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        h.Acquire(ref depth);
        var proxy = h.ProxyAt(stencil, 4);
        Assert.True(h.Worker.Run(() => h.Images.TryClearImageFromBuffer(stencil, 4, 0x08080808)));
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_PUBLICATION_EXACT") != "0",
            h.Images.WouldPublishImagesUnderWrite(stencil, 4, true));
        h.Worker.Run(() => h.Images.PublishImagesUnderFormattedWrite(stencil, 4));
        Assert.False(h.Image(proxy).IsGpuModified);
        h.Shutdown();
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("gpu")]
    [InlineData("dma")]
    [InlineData("image")]
    [InlineData("metadata")]
    [InlineData("interpretation")]
    [InlineData("format")]
    public void DccNonclearMemoRejectsEveryWriteAndInterpretationChange(string change)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var h = new CacheHarness(_vulkan);
        var address = h.MapBacked(0x20000, ReadWrite);
        var metadata = address + 0x10000;
        var bytes = Enumerable.Repeat((byte)0xc0, 0x1000).ToArray();
        bytes[^1] = 0xff; // Clear-looking first byte, but a proven mixed slice.
        h.Write(metadata, bytes);
        h.Worker.Run(() => h.Cache.ObtainBuffer(metadata, 0x1000, isWritten: false, allowStream: false));
        var request = AsColorTarget(LinearRequest(address, 64, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm,
            GuestImageType.Color2D, new Extent3D(4, 4, 1), 1, 4, 1));
        request.Description.Metadata.Kind = MetadataKind.Dcc;
        request.Description.Metadata.Range = new GuestSpan(metadata, 0x1000);
        var id = h.Find(ref request);
        var enabled = Environment.GetEnvironmentVariable("SHARPEMU_DCC_NONCLEAR_MEMO") == "1";
        Assert.Equal(enabled, h.Images.TryFindImageForSlice(ref request, false, out _));
        var uses = new List<ImageSliceUse> { new(id, h.Image(id), request) };
        var lookupGeneration = h.Images.LookupGeneration;
        var metadataGeneration = h.Images.MetadataGeneration;
        if (enabled) Assert.True(h.Images.ValidateSliceImages(uses, lookupGeneration, metadataGeneration));
        if (change == "cpu")
        {
            h.Write(metadata + 0xfff, [0xc0]);
            h.Gpu.MarkCpuWriteLanded(metadata + 0xfff, 1);
            // Clear the dirty flag too: the write stamp must still reject the old proof.
            h.Worker.Run(() => h.Cache.ObtainBuffer(metadata, 0x1000, isWritten: false, allowStream: false));
        }
        else if (change == "gpu")
        {
            h.Worker.Run(() =>
            {
                var (buffer, offset) = h.Cache.ObtainBuffer(metadata, 0x1000, isWritten: true, allowStream: false);
                buffer.Fill(offset, 0x1000, 0xc0c0c0c0);
            });
            Assert.True(h.Cache.DownloadToCpu(metadata, 0x1000));
        }
        else if (change == "dma") h.Worker.Run(() => h.Cache.WriteHostMemory(metadata, Enumerable.Repeat((byte)0xc0, 0x1000).ToArray()));
        else if (change == "image")
        {
            var alias = Color32(metadata, 1024);
            var aliasId = h.Acquire(ref alias);
            h.Worker.Run(() => h.Images.MarkGpuWritten(aliasId));
        }
        else if (change == "metadata") h.Worker.Run(() => h.Images.TryAbsorbDccFill(metadata, 0x1000, 0xc0c0c0c0));
        else if (change == "interpretation") request.Description.Metadata.DccAlphaMsb = !request.Description.Metadata.DccAlphaMsb;
        else request.View = request.View with { Format = Format.R8G8B8A8Srgb };
        Assert.False(h.Images.TryFindImageForSlice(ref request, false, out _));
        if (enabled && change is "cpu" or "gpu" or "dma")
            Assert.False(h.Images.ValidateSliceImages(uses, lookupGeneration, metadataGeneration));
        h.Shutdown();
    }

    [Fact]
    public void DccNonclearMemoDoesNotCertifyWritableOrUntrackedMetadata()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var h = new CacheHarness(_vulkan);
        var address = h.MapBacked(0x20000, ReadWrite);
        var bytes = Enumerable.Repeat((byte)0xc0, 0x1000).ToArray();
        bytes[^1] = 0xff;
        h.Write(address + 0x10000, bytes);
        var request = AsColorTarget(LinearRequest(address, 64, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm,
            GuestImageType.Color2D, new Extent3D(4, 4, 1), 1, 4, 1));
        request.Description.Metadata.Kind = MetadataKind.Dcc;
        request.Description.Metadata.Range = new GuestSpan(address + 0x10000, 0x1000);
        h.Find(ref request);
        Assert.False(h.Images.TryFindImageForSlice(ref request, false, out _));
        h.Shutdown();
    }
}
