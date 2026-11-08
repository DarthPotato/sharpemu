// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcSetShRegistersDirectTests
{
    private const ulong BaseAddress = 0x2_6100_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong RegistersAddress = BaseAddress + 0x800;

    [Fact]
    public void SortsTheRegistersAndWritesOnePacketPerContiguousRun()
    {
        var context = CreateContext(out var memory, capacityBytes: 64);
        WriteRegisters(memory, (0x46, 0xA), (0x44, 0xB), (0x45, 0xC), (0x50, 0xD));
        context[CpuRegister.Rdx] = 4;

        Assert.Equal(0, AgcExports.CbSetShRegistersDirect(context));

        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + 32, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(new uint[] { 0xC003_7600, 0x44, 0xB, 0xC, 0xA, 0xC001_7600, 0x50, 0xD }, ReadUInt32s(memory, PacketAddress, 8));
    }

    [Fact]
    public void LargeRegisterListsUsePooledBuffers()
    {
        const int count = 200;
        var context = CreateContext(out var memory, capacityBytes: (count + 2) * 4);
        var registers = new (uint, uint)[count];
        for (var index = 0; index < count; index++)
        {
            registers[index] = ((uint)(0x300 - index), (uint)index);
        }

        WriteRegisters(memory, registers);
        context[CpuRegister.Rdx] = count;

        Assert.Equal(0, AgcExports.CbSetShRegistersDirect(context));

        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        var packet = ReadUInt32s(memory, PacketAddress, count + 2);
        Assert.Equal(0xC000_7600u | ((uint)count << 16), packet[0]);
        Assert.Equal(0x300u - count + 1, packet[1]);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal((uint)(count - 1 - index), packet[2 + index]);
        }
    }

    [Fact]
    public void AnUnreadableRegisterListWritesNothing()
    {
        var context = CreateContext(out var memory, capacityBytes: 64);
        context[CpuRegister.Rsi] = BaseAddress + 0xFF8;
        context[CpuRegister.Rdx] = 2;

        Assert.Equal(0, AgcExports.CbSetShRegistersDirect(context));

        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(0u, ReadUInt32s(memory, PacketAddress, 1)[0]);
    }

    private static CpuContext CreateContext(out FakeCpuMemory memory, int capacityBytes)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + (ulong)capacityBytes);
        return new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = RegistersAddress,
        };
    }

    private static void WriteRegisters(FakeCpuMemory memory, params (uint Offset, uint Value)[] registers)
    {
        for (var index = 0; index < registers.Length; index++)
        {
            WriteUInt32(memory, RegistersAddress + (ulong)index * 8, registers[index].Offset);
            WriteUInt32(memory, RegistersAddress + (ulong)index * 8 + 4, registers[index].Value);
        }
    }

    private static uint[] ReadUInt32s(FakeCpuMemory memory, ulong address, int count)
    {
        var bytes = new byte[count * sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        var values = new uint[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)));
        }

        return values;
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
