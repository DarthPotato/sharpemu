// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class PackedInt16DeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper outputLog) : IClassFixture<HeadlessVulkanFixture>
{
    public static IEnumerable<object[]> Cases()
    {
        (uint Code, string Name)[] operations =
        [
            (0x00, "VPkMadI16"), (0x01, "VPkMulLoU16"),
            (0x04, "VPkLshlrevB16"), (0x05, "VPkLshrrevB16"), (0x06, "VPkAshrrevI16"),
            (0x07, "VPkMaxI16"), (0x08, "VPkMinI16"), (0x09, "VPkMadU16"),
            (0x0A, "VPkAddU16"), (0x0B, "VPkSubU16"), (0x0C, "VPkMaxU16"), (0x0D, "VPkMinU16"),
        ];
        foreach (var (code, name) in operations)
        foreach (var (a, b, c) in new (uint, uint, uint)[]
        {
            (0x8001_FFFF, 0x7FFF_0002, 0x1234_FFFE),
            (0x001F_0010, 0x8001_FFED, 0xABCD_0007), // shifts by 31 and 16 become 15 and 0
            (0xFFFF_0011, 0xFFFD_8000, 0x0001_8003), // shifts by 65535 and 17 become 15 and 1
        })
            yield return [code, name, a, b, c];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PackedResultsFollowIndependentSourceHalfSelections(uint code, string name, uint a, uint b, uint c)
    {
        // Exercise all 64 pairs, including duplicated halves and independent selection of MAD src2.
        var instructions = Inputs();
        var expected = new List<uint>();
        for (uint low = 0; low < 8; low++)
        for (uint high = 0; high < 8; high++)
        {
            var pc = (uint)(12 + expected.Count * 16);
            var decoded = Gen5Float16ArithmeticTests.Decode(
                [0xCC00_0014u | (code << 16) | (low << 11) | ((high >> 2) << 14),
                    0x100u | (0x101u << 9) | (0x102u << 18) | ((high & 3) << 27), 0xBF81_0000u]).Instructions[0];
            Assert.Equal(name, decoded.Opcode);
            Assert.Equal(new Gen5Vop3pControl(low, high, 0, 0, false), decoded.Control);
            instructions.Add(decoded with { Pc = pc });
            instructions.Add(BufferAccess(pc + 8, "BufferStoreDword", 4, offset: expected.Count * 4, vectorData: 20));
            expected.Add(Evaluate(code, Half(a, low, 0), Half(b, low, 1), Half(c, low, 2)) |
                (Evaluate(code, Half(a, high, 0), Half(b, high, 1), Half(c, high, 2)) << 16));
        }
        Run(instructions, expected, a, b, c);
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    [InlineData(8u)] [InlineData(9u)] [InlineData(10u)] [InlineData(11u)]
    public void MultiplyLowWrapsAndPreservesOtherDestinationHalf(uint select)
    {
        const uint a = 0xFFFF_8001, b = 0x8003_FFFE, previous = 0xA5A5_5A5A;
        var instructions = Inputs();
        instructions.Add(MoveVectorFromScalar(12, 20, 10));
        var decoded = Gen5Float16ArithmeticTests.Decode(
            [0xD305_0014u | (select << 11), 0x100u | (0x101u << 9), 0xBF81_0000u]).Instructions[0];
        Assert.Equal("VMulLoU16", decoded.Opcode);
        instructions.Add(decoded with { Pc = 16 });
        instructions.Add(BufferAccess(24, "BufferStoreDword", 4, vectorData: 20));
        var product = (Half(a, select, 0) * Half(b, select, 1)) & 0xFFFF;
        var expected = (select & 8) == 0 ? (previous & 0xFFFF_0000) | product :
            (previous & 0xFFFF) | (product << 16);
        Run(instructions, [expected], a, b, previous);
    }

    private static List<Gen5ShaderInstruction> Inputs() =>
    [MoveVectorFromScalar(0, 0, 8), MoveVectorFromScalar(4, 1, 9), MoveVectorFromScalar(8, 2, 10)];

    private void Run(List<Gen5ShaderInstruction> instructions, IReadOnlyList<uint> expected, uint a, uint b, uint c)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        outputLog.WriteLine($"Vulkan device: {vulkan!.DeviceName}");
        instructions.Add(EndProgram(instructions[^1].Pc + 8));
        var (plan, resources, layout) = Prepare(Program(instructions.ToArray()));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer((uint)expected.Count * 4);
        var registers = new uint[256];
        registers[6] = (uint)expected.Count * 4;
        registers[8] = a; registers[9] = b; registers[10] = c;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var bytes = runner.ReadBack(output, 0, (uint)expected.Count * 4);
        for (var i = 0; i < expected.Count; i++)
        {
            var actual = BitConverter.ToUInt32(bytes, i * 4);
            Assert.True(expected[i] == actual,
                $"Result {i} (op_sel={i / 8}, op_sel_hi={i % 8}): expected 0x{expected[i]:X8}, got 0x{actual:X8}");
        }
        harness.AssertNoValidationMessages();
    }

    private static uint Half(uint value, uint select, int source) => (value >> (((select >> source) & 1) == 0 ? 0 : 16)) & 0xFFFF;

    // RDNA2 unclamped integer arithmetic truncates to 16 bits. Signed MAD has the
    // same low product bits as unsigned MAD; signed comparisons and ASHR differ.
    private static uint Evaluate(uint code, uint a, uint b, uint c)
    {
        var signedA = (int)unchecked((short)a);
        var signedB = (int)unchecked((short)b);
        long result = code switch
        {
            0x00 => (long)signedA * signedB + unchecked((short)c),
            0x01 => (long)a * b,
            0x04 => (long)b << (int)(a & 15),
            0x05 => b >> (int)(a & 15),
            0x06 => signedB >> (int)(a & 15),
            0x07 => Math.Max(signedA, signedB),
            0x08 => Math.Min(signedA, signedB),
            0x09 => (long)a * b + c,
            0x0A => (long)a + b,
            0x0B => (long)a - b,
            0x0C => Math.Max(a, b),
            0x0D => Math.Min(a, b),
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };
        return unchecked((uint)result) & 0xFFFF;
    }
}
