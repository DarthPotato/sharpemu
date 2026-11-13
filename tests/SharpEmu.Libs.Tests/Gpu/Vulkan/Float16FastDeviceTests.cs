// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class Float16FastDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly Gen5Vop3pControl[] Controls =
    [
        new(0, 7, 0, 0, false), // Default: low halves, then high halves.
        new(7, 0, 0, 0, false), // Swap halves of every source.
        new(0, 0, 0, 0, false),
        new(7, 7, 0, 0, false),
        new(1, 2, 1, 2, false), // Independent selection and sign bits per source and lane.
        new(0, 7, 3, 3, false),
        new(0, 7, 0, 0, true),
    ];

    public static IEnumerable<object[]> ArithmeticCases()
    {
        uint[] values = [0x80000000, 0x00008000, 0x00010002, 0x03FF0400,
            0x7BFFFBFF, 0x40003C00, 0x7C00FC00];
        foreach (var opcode in new[] { "VPkAddF16", "VPkMulF16", "VPkMinF16", "VPkMaxF16" })
        foreach (var a in values)
        foreach (var b in values)
        for (var control = 0; control < Controls.Length; control++)
        {
            // Infinity arithmetic that produces NaN has no payload equality guarantee.
            if ((opcode == "VPkAddF16" || opcode == "VPkMulF16") &&
                (a == 0x7C00FC00 || b == 0x7C00FC00)) continue;
            yield return [opcode, a, b, control];
        }
        // Infinity arithmetic with defined numeric results, including both signs.
        foreach (var opcode in new[] { "VPkAddF16", "VPkMulF16" })
            yield return [opcode, 0x7C00FC00u, 0x40003C00u, 0];
    }

    [Theory]
    [MemberData(nameof(ArithmeticCases))]
    public void ArithmeticMatchesExactBits(string opcode, uint a, uint b, int control)
        => Compare(opcode, a, b, 0, Controls[control], exact: true, fma: false);

    [Theory]
    [InlineData("VPkMinF16", 0x7E113C00u, 0x4000FE22u, 0x40003C00u)]
    [InlineData("VPkMaxF16", 0x7E113C00u, 0x4000FE22u, 0x40003C00u)]
    [InlineData("VPkMinF16", 0x4000FE22u, 0x7E113C00u, 0x40003C00u)]
    [InlineData("VPkMaxF16", 0x4000FE22u, 0x7E113C00u, 0x40003C00u)]
    public void MinimumMaximumSelectNumericOperand(string opcode, uint a, uint b, uint expected)
        => Compare(opcode, a, b, 0, Controls[0], true, false, expected);

    public static IEnumerable<object[]> FmaCases()
    {
        foreach (var opcode in new[] { "VPkFmaF16", "VPkFmacF16" })
        {
            yield return [opcode, 0x40003C00u, 0x42004000u, 0x3C003800u, true];
            yield return [opcode, 0xBC004000u, 0x40003800u, 0x4200BC00u, true];
            yield return [opcode, 0x3C013C01u, 0x3BFE3C01u, 0xBC00BC00u, false];
            yield return [opcode, 0x000103FFu, 0x38003C01u, 0x00018001u, false];
            yield return [opcode, 0x3555B555u, 0x39553955u, 0x10009000u, false];
        }
    }

    [Theory]
    [MemberData(nameof(FmaCases))]
    public void FusedArithmeticStaysWithinOneUnitInTheLastPlace(string opcode, uint a, uint b, uint c, bool exact)
        => Compare(opcode, a, b, c, Controls[0], exact, true);

    [Theory]
    [InlineData("VPkAddF16", 0x7C007E11u, 0xFC003C00u)]
    [InlineData("VPkMulF16", 0x7C007E11u, 0x00003C00u)]
    [InlineData("VPkFmaF16", 0x7C007E11u, 0x00003C00u)]
    public void ClampMapsNanResultToZero(string opcode, uint a, uint b)
        => Compare(opcode, a, b, 0, Controls[0] with { Clamp = true }, true,
            opcode == "VPkFmaF16", 0);

    private void Compare(string opcode, uint a, uint b, uint c, Gen5Vop3pControl control,
        bool exact, bool fma, uint? expected = null)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        if (!vulkan.ExactFloat16Conversion)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "Fast float16 tests require exact float16 conversion support.");
            return;
        }
        var (plan, resources, layout) = Prepare(Program([
            MoveVector(0, 1, a), MoveVector(4, 2, c), MoveVector(8, 3, b), MoveVector(12, 4, c),
            new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Vop3p, opcode, [0u, 0u],
                opcode == "VPkFmaF16"
                    ? [Gen5Operand.Vector(1), Gen5Operand.Vector(3), Gen5Operand.Vector(4)]
                    : [Gen5Operand.Vector(1), Gen5Operand.Vector(3)],
                [Gen5Operand.Vector(2)], control),
            BufferAccess(24, "BufferStoreDword", 4, 0, 1, 2), EndProgram(32),
        ]));
        uint Run(int level)
        {
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                ExactFloat16ConversionSupported = true, FastFloat16 = level,
                LocalSizeX = 1, ThreadCountX = 1, WaveSize = 32,
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            using var harness = new ImageTestHarness(vulkan);
            using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
            var output = runner.CreateBuffer(256);
            var registers = new uint[256];
            registers[6] = 256;
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
            var result = BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(output, 0, 4));
            harness.AssertNoValidationMessages();
            return result;
        }
        var reference = Run(0);
        if (expected.HasValue) Assert.Equal(expected.Value, reference);
        foreach (var level in fma ? new[] { 1, 2 } : new[] { 2 })
        {
            var actual = Run(level);
            var context = $"{opcode} a=0x{a:X8} b=0x{b:X8} c=0x{c:X8} {control}: level 0=0x{reference:X8}, level {level}=0x{actual:X8}";
            if (exact) Assert.True(reference == actual, context);
            else
            {
                for (var shift = 0; shift <= 16; shift += 16)
                {
                    var x = (reference >> shift) & 0xFFFF;
                    var y = (actual >> shift) & 0xFFFF;
                    var bothZero = (x & 0x7FFF) == 0 && (y & 0x7FFF) == 0;
                    Assert.True(bothZero || ((x & 0x8000) == (y & 0x8000) &&
                        Math.Abs((int)x - (int)y) <= 1), context + $" lane={shift / 16}");
                }
            }
        }
    }
}
