// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class GpuTilerBenchmarkTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;
    private readonly ITestOutputHelper _output;

    public GpuTilerBenchmarkTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    {
        _vulkan = fixture.Vulkan;
        _output = output;
    }

    [Fact]
    public void CompareMemoryPlacementAndElementSizes()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TILER_BENCH") != "1") return;
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var vk = harness.Vk;
        vk.GetPhysicalDeviceProperties(_vulkan.Physical, out var properties);
        var timestampPeriod = properties.Limits.TimestampPeriod;
        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(_vulkan.Physical, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pointer = families)
            vk.GetPhysicalDeviceQueueFamilyProperties(_vulkan.Physical, &familyCount, pointer);
        var validBits = families[_vulkan.QueueFamily].TimestampValidBits;
        var timestamps = validBits != 0 && properties.Limits.TimestampPeriod > 0;
        var pool = default(QueryPool);
        if (timestamps)
        {
            var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = 2 };
            Assert.Equal(Result.Success, vk.CreateQueryPool(_vulkan.Device, &info, null, out pool));
        }

        _output.WriteLine($"TILER_BENCH device={SilkMarshal.PtrToString((nint)properties.DeviceName)} timing={(timestamps ? "Vulkan timestamps (barriers included)" : "wall clock submit+wait including recording")} warmup=2 samples=5 dispatch=480x270x1 local=8x8x1");
        _output.WriteLine("TILER_BENCH D uses the public Detile DeviceLocal ring and includes its GPU destination clear; E is 66,355,200 bytes (decimal MB).");
        try
        {
            var transfer = CreateTransfer(8);
            var inputBytes = ImageTestHarness.Pattern((int)transfer.LinearSize, 71);
            var upload = harness.Upload(inputBytes);
            var linear = harness.CreateDeviceLocalBuffer(transfer.LinearSize);
            var tiled = harness.CreateDeviceLocalBuffer(transfer.TiledSize);
            using var download = new GpuBuffer(harness.Device, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, transfer.TiledSize);
            harness.Run(() =>
            {
                Copy(upload, linear, transfer.LinearSize);
                tiled.Fill(0, tiled.Size, 0);
                download.Fill(0, download.Size, 0);
                harness.Scheduler.Finish();
            });
            Measure("A", () => harness.Tiler.Tile(linear.Handle, 0, linear.Size, tiled.Handle, 0, tiled.Size, [transfer]), transfer);
            Measure("B", () => harness.Tiler.Tile(linear.Handle, 0, linear.Size, download.Handle, 0, download.Size, [transfer]), transfer);
            // Both padding and active pixels are deterministic: targets were cleared outside timing.
            var actual = harness.ReadBack(tiled.Handle, 0, tiled.Size);
            var host = harness.ReadBack(download.Handle, 0, download.Size);
            Assert.True(actual.AsSpan().SequenceEqual(host), "A and B differ byte-for-byte.");
            // Also check a subregion against the CPU address twin, avoiding a shared shader bug.
            Assert.True(TileGeometry.TryGetBlockLayout(transfer.Kind, 8, out var block));
            for (uint y = 0; y < 37; y++)
            for (uint x = 0; x < 53; x++)
            {
                Assert.True(TileGeometry.TryGetBlockOffset(block, x, y, 0, out var offset));
                Assert.True(TileGeometry.TryGetBlockXor(block, 0, 0, out var xor));
                Assert.True(actual.AsSpan((int)(offset ^ xor), 8).SequenceEqual(inputBytes.AsSpan((int)((y * 3840 + x) * 8), 8)));
            }
            _output.WriteLine("TILER_BENCH correctness=A/B byte-for-byte and A CPU twin 53x37 passed");
            foreach (uint bytes in new uint[] { 1, 2 })
            {
                var small = CreateTransfer(bytes);
                var source = harness.CreateDeviceLocalBuffer(small.LinearSize);
                var target = harness.CreateDeviceLocalBuffer(small.TiledSize);
                harness.Run(() => { Copy(upload, source, small.LinearSize); target.Fill(0, target.Size, 0); harness.Scheduler.Finish(); });
                Measure($"C{bytes}B", () => harness.Tiler.Tile(source.Handle, 0, source.Size, target.Handle, 0, target.Size, [small]), small);
            }
            Measure("D", () => { _ = harness.Tiler.Detile(tiled.Handle, 0, tiled.Size, linear.Size, [transfer]); }, transfer);
            Measure("E", () => Copy(linear, download, transfer.LinearSize), transfer);
            harness.AssertNoValidationMessages();
        }
        finally
        {
            harness.Finish();
            if (timestamps) vk.DestroyQueryPool(_vulkan.Device, pool, null);
        }

        void Measure(string name, Action record, TileTransfer transfer)
        {
            var samples = new double[5];
            for (var pass = -2; pass < samples.Length; pass++)
            {
                var elapsed = harness.Run(() =>
                {
                    var command = new CommandBuffer(harness.Scheduler.Current.Handle);
                    var watch = Stopwatch.StartNew();
                    if (timestamps)
                    {
                        vk.CmdResetQueryPool(command, pool, 0, 2);
                        vk.CmdWriteTimestamp(command, PipelineStageFlags.TopOfPipeBit, pool, 0);
                    }
                    record();
                    if (timestamps) vk.CmdWriteTimestamp(command, PipelineStageFlags.BottomOfPipeBit, pool, 1);
                    harness.Scheduler.Finish();
                    watch.Stop();
                    if (!timestamps) return watch.Elapsed.TotalMilliseconds;
                    var values = stackalloc ulong[2];
                    Assert.Equal(Result.Success, vk.GetQueryPoolResults(_vulkan.Device, pool, 0, 2, 16, values, 8, QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit));
                    var mask = validBits == 64 ? ulong.MaxValue : (1UL << (int)validBits) - 1;
                    return ((values[1] - values[0]) & mask) * (double)timestampPeriod / 1_000_000;
                });
                if (pass >= 0) samples[pass] = elapsed;
            }
            var median = samples.Order().ElementAt(2);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TILER_BENCH case={name} ms={median:F3} bytesPerElement={transfer.BytesPerElement} kind={transfer.Kind} linearBytes={transfer.LinearSize} tiledBytes={transfer.TiledSize} samples_ms={string.Join(",", samples.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))}"));
        }

        void Copy(GpuBuffer source, GpuBuffer target, ulong size)
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit, DstAccessMask = AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit };
            vk.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
            var region = new BufferCopy(0, 0, size);
            vk.CmdCopyBuffer(command, source.Handle, target.Handle, 1, &region);
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.HostReadBit;
            vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        }
    }

    private static TileTransfer CreateTransfer(uint bytes)
    {
        var kind = TileBlockKind.RenderTarget64KBGen5;
        if (!TileGeometry.TryGetBlockLayout(kind, bytes, out var block))
        {
            kind = TileBlockKind.Standard64KB;
            Assert.True(TileGeometry.TryGetBlockLayout(kind, bytes, out block));
        }
        var width = (3840u + block.BlockWidth - 1) / block.BlockWidth * block.BlockWidth;
        var height = (2160u + block.BlockHeight - 1) / block.BlockHeight * block.BlockHeight;
        return new TileTransfer
        {
            Kind = kind, BytesPerElement = bytes, Width = 3840, Height = 2160, Depth = 1, Pitch = 3840,
            TiledWidth = width, TiledHeight = height, LinearSize = 3840UL * 2160 * bytes,
            TiledSize = (ulong)(width / block.BlockWidth) * (height / block.BlockHeight) * block.BlockSize,
        };
    }
}
