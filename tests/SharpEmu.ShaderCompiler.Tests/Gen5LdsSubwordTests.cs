// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5LdsSubwordTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    public static TheoryData<uint, string, bool> Opcodes = new()
    {
        { 0x1E, "DsWriteB8", true }, { 0x1F, "DsWriteB16", true },
        { 0x39, "DsReadI8", false }, { 0x3A, "DsReadU8", false },
        { 0x3B, "DsReadI16", false }, { 0x3C, "DsReadU16", false },
    };

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void EncodingDecodesOperandsAndCompiles(uint opcode, string name, bool write)
    {
        var program = Decode([(0x36u << 26) | (opcode << 18) | 0x12ABu,
            7u | (19u << 8) | (23u << 16) | (31u << 24), 0xBF810000]);
        var instruction = program.Instructions[0];
        Assert.Equal(name, instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Ds, instruction.Encoding);
        Assert.Equal(write ? new[] { Gen5Operand.Vector(7), Gen5Operand.Vector(19) } : [Gen5Operand.Vector(7)], instruction.Sources);
        Assert.Equal(write ? Array.Empty<Gen5Operand>() : [Gen5Operand.Vector(31)], instruction.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0xABu, control.Offset0);
        Assert.Equal(0x12u, control.Offset1);
        Assert.Equal(0x12ABu, control.SingleOffsetBytes);
        Assert.False(control.Gds);
        foreach (var stage in new[] { ShaderStage.Compute, ShaderStage.Vertex })
        {
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(ResourceTestProgram.Request(program, stage, userDataCount: 0), out var shader, out var error), error);
            Assert.NotEmpty(shader.Spirv);
        }
    }
    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        return program;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < baseAddress || address - baseAddress > int.MaxValue)
            {
                return false;
            }

            offset = (int)(address - baseAddress);
            return offset <= _storage.Length - length;
        }
    }
}

