// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// The changed-byte merge on the device: only output bytes where a segment's new and old bytes
// differ take the new value; every other output byte keeps what the GPU copy held.
[Collection(SchedulingStateCollection.Name)]
public sealed class ChangedByteMergeDeviceTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public ChangedByteMergeDeviceTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void Merge_WritesOnlyTheBytesThatDiffer()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var random = new System.Random(1234);

        // Three segments of different sizes at word-aligned destinations, one of them 4 KiB (a page).
        (uint Destination, uint Bytes)[] segments = [(0, 64), (128, 4096), (4352, 260)];
        const uint OutputBytes = 4352 + 260 + 32;
        var current = new byte[OutputBytes];
        random.NextBytes(current);

        var records = segments.Length * (int)TilerShaders.MergeSegmentWords * sizeof(uint);
        var data = new List<byte>();
        var expected = current.ToArray();
        var recordWords = new uint[segments.Length * TilerShaders.MergeSegmentWords];
        for (var index = 0; index < segments.Length; index++)
        {
            var (destination, bytes) = segments[index];
            var oldBytes = new byte[bytes];
            random.NextBytes(oldBytes);
            var newBytes = oldBytes.ToArray();
            // Change scattered bytes, including single bytes inside otherwise equal words.
            for (var at = 0; at < bytes; at++)
            {
                if (random.Next(5) == 0)
                {
                    newBytes[at] = (byte)(newBytes[at] + 1 + random.Next(254));
                }
            }

            recordWords[index * 4 + 0] = (uint)(records + data.Count);
            data.AddRange(newBytes);
            recordWords[index * 4 + 1] = (uint)(records + data.Count);
            data.AddRange(oldBytes);
            recordWords[index * 4 + 2] = destination;
            recordWords[index * 4 + 3] = bytes;
            for (var word = 0; word < bytes / 4; word++)
            {
                var offset = (int)destination + word * 4;
                var merged = TilerShaders.MergeChangedBytes(
                    BitConverter.ToUInt32(expected, offset),
                    BitConverter.ToUInt32(newBytes, word * 4),
                    BitConverter.ToUInt32(oldBytes, word * 4));
                BitConverter.TryWriteBytes(expected.AsSpan(offset), merged);
            }
        }

        // Spans start at word offsets that are not storage-aligned, so the binding bases are nonzero.
        const ulong InputOffset = 4;
        const ulong OutputOffset = 12;
        var input = new byte[InputOffset + (ulong)records + (ulong)data.Count];
        MemoryMarshal.AsBytes(recordWords.AsSpan()).CopyTo(input.AsSpan((int)InputOffset));
        data.ToArray().CopyTo(input.AsSpan((int)InputOffset + records));
        var output = new byte[OutputOffset + OutputBytes + 16];
        random.NextBytes(output);
        current.CopyTo(output.AsSpan((int)OutputOffset));
        var outputBefore = output.ToArray();

        var inputBuffer = harness.Upload(input);
        var outputBuffer = harness.Upload(output);
        harness.Run(() => harness.Tiler.MergeChangedBytes(
            new TilerBufferSpan(inputBuffer.Handle, InputOffset, (ulong)(records + data.Count)),
            new TilerBufferSpan(outputBuffer.Handle, OutputOffset, OutputBytes),
            (uint)segments.Length,
            4096));
        var result = harness.ReadBack(outputBuffer.Handle, 0, (ulong)output.Length);

        Assert.Equal(outputBefore.AsSpan(0, (int)OutputOffset).ToArray(), result.AsSpan(0, (int)OutputOffset).ToArray());
        Assert.Equal(expected, result.AsSpan((int)OutputOffset, (int)OutputBytes).ToArray());
        Assert.Equal(outputBefore.AsSpan((int)(OutputOffset + OutputBytes)).ToArray(), result.AsSpan((int)(OutputOffset + OutputBytes)).ToArray());
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(0x11223344u, 0x11223344u, 0xAABBCCDDu, 0xAABBCCDDu)]
    [InlineData(0x11223355u, 0x11223344u, 0xAABBCCDDu, 0xAABBCC55u)]
    [InlineData(0x99223344u, 0x11223344u, 0xAABBCCDDu, 0x99BBCCDDu)]
    [InlineData(0x01000001u, 0x00000000u, 0xFFFFFFFFu, 0x01FFFF01u)]
    [InlineData(0x80008000u, 0x00000000u, 0x12345678u, 0x80348078u)]
    public void ReferenceMerge_TakesExactlyTheChangedBytes(uint newWord, uint oldWord, uint current, uint expected)
    {
        Assert.Equal(expected, TilerShaders.MergeChangedBytes(current, newWord, oldWord));
    }
}
