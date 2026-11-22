// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PairedFlagInterfaceTests
{
    [Fact]
    public void PairedFlagsAreListedInTheEntryPointInterface()
    {
        // Broadcasting the mask to the lanes keeps the program off the lane-local half-wave lowering (on by default), so
        // it runs as paired lanes.
        var program = Program(Vopc(0, "VCmpEqU32", Gen5Operand.Vector(0), 1),
            Sop1(4, "SMovB64", 4, Gen5Operand.Scalar(106)), Vop1(8, "VMovB32", 1, Gen5Operand.Scalar(4)), EndProgram(12));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { WaveSize = 64, HostComputeSubgroup32 = true, LocalSizeX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = MemoryMarshal.Cast<byte, uint>(shader.Spirv.AsSpan());
        var flags = new HashSet<uint>();
        var interfaces = new HashSet<uint>();
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var op = (SpirvOp)(words[i] & 65535);
            var a = words.Slice(i + 1, (int)(words[i] >> 16) - 1);
            if (op == SpirvOp.Name)
            {
                var name = Encoding.UTF8.GetString(MemoryMarshal.AsBytes(a[1..])).TrimEnd('\0');
                if (name is "vccUpperHalf" or "execUpperHalf") flags.Add(a[0]);
            }
            if (op == SpirvOp.EntryPoint)
            {
                var end = 2;
                while (!MemoryMarshal.AsBytes(a.Slice(end++, 1)).Contains((byte)0)) { }
                foreach (var id in a[end..]) interfaces.Add(id);
            }
        }
        Assert.Equal(2, flags.Count);
        Assert.All(flags, flag => Assert.Contains(flag, interfaces));
    }
}
