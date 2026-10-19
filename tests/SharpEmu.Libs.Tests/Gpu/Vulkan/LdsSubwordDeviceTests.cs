// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class LdsSubwordDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    public static TheoryData<uint, int, bool> Cases = new()
    {
        { 32, 8, false }, { 32, 16, false }, { 64, 8, false }, { 64, 16, false },
        { 32, 8, true }, { 32, 16, true }, { 64, 8, true }, { 64, 16, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ConcurrentPackedWritesPreserveNeighboursAndExtendReads(uint wave, int bits, bool masked)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan, shaderInt64: true)) return;
        // Both offset bytes contribute; the low byte also changes the subword position.
        const uint offset = 0x102;
        var instructions = new List<Gen5ShaderInstruction>();
        void Add(Gen5ShaderInstruction instruction) => instructions.Add(instruction with { Pc = (uint)instructions.Count * 8 });
        void Barrier() => Add(new(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
        var width = (uint)bits / 8;
        var sign = 1u << (bits - 1);
        // v0 is the local lane ID. Initialize every touched dword to a sentinel.
        Add(Vop2(0, "VLshlrevB32", 1, Operand(2), Gen5Operand.Vector(0)));
        Add(MoveVector(0, 2, 0x55555555));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(2)], [], 0, 1));
        Barrier();
        Add(Vop2(0, "VLshlrevB32", 3, Operand(bits == 8 ? 0u : 1u), Gen5Operand.Vector(0)));
        // Deliberately dirty upper bits: only the low byte/halfword may reach LDS.
        Add(Vop2(0, "VOrB32", 2, Operand(0xA5A50000u | sign), Gen5Operand.Vector(0)));
        if (masked)
        {
            Add(Vop2(0, "VAndB32", 7, Operand(1), Gen5Operand.Vector(0)));
            Add(Vopc(0, "VCmpxEqU32", Operand(0), 7));
        }
        Add(DataShare(0, bits == 8 ? "DsWriteB8" : "DsWriteB16", false,
            [Gen5Operand.Vector(3), Gen5Operand.Vector(2)], [], offset & 255, offset >> 8));
        Add(Sop1(0, "SMovB64", 126, Gen5Operand.Source(193)));
        Barrier();
        Add(DataShare(0, bits == 8 ? "DsReadU8" : "DsReadU16", false, [Gen5Operand.Vector(3)], [8], offset & 255, offset >> 8));
        Add(DataShare(0, bits == 8 ? "DsReadI8" : "DsReadI16", false, [Gen5Operand.Vector(3)], [9], offset & 255, offset >> 8));
        Add(Vop2(0, "VAddU32", 4, Operand(offset), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VAndB32", 4, Operand(0xFFFFFFFC), Gen5Operand.Vector(4)));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [10]));
        Add(Vop2(0, "VLshlrevB32", 5, Operand(4), Gen5Operand.Vector(0)));
        Add(BufferAccess(0, "BufferStoreDwordx3", 4, dwords: 3, vectorData: 8, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = wave, ThreadCountX = wave, WaveSize = wave,
            HostComputeSubgroup32 = true, LocalDataShareDwords = 256,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(fixture.Vulkan!);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(wave * 16);
        var registers = new uint[256];
        registers[6] = wave * 16;
        var expected = Enumerable.Repeat((byte)0x55, 1024).ToArray();
        for (uint lane = 0; lane < wave; lane++)
        {
            if (masked && (lane & 1) != 0) continue;
            for (uint b = 0; b < width; b++) expected[offset + lane * width + b] = (byte)((sign | lane) >> (int)(8 * b));
        }
        // Repeated dispatches exercise different atomic scheduling without assuming LDS starts at zero.
        for (var repeat = 0; repeat < 8; repeat++)
        {
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
            var data = runner.ReadBack(output, 0, wave * 16);
            for (uint lane = 0; lane < wave; lane++)
            {
                var address = (int)(offset + lane * width);
                var unsigned = bits == 8 ? expected[address] : BitConverter.ToUInt16(expected, address);
                var signed = bits == 8 ? unchecked((uint)(sbyte)unsigned) : unchecked((uint)(short)unsigned);
                Assert.True(BitConverter.ToUInt32(data, (int)lane * 16) == unsigned, $"U{bits} wave={wave} lane={lane} repeat={repeat}");
                Assert.Equal(signed, BitConverter.ToUInt32(data, (int)lane * 16 + 4));
                Assert.Equal(BitConverter.ToUInt32(expected, address & ~3), BitConverter.ToUInt32(data, (int)lane * 16 + 8));
            }
        }
        harness.AssertNoValidationMessages();
    }
}

