// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class UniformBufferLoadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnaddressedLoadRemainsUniformAcrossDivergentLoop(bool divergentLoop)
    {
        // v0 has no uniform value, but the load does not read a VGPR address.
        var instructions = new List<Gen5ShaderInstruction>
        {
            BufferAccess(0, "BufferLoadDword", 0, offset: 4, vectorData: 5),
        };
        if (divergentLoop)
        {
            instructions.AddRange([
                Vopc(8, "VCmpxGtU32", Gen5Operand.Scalar(4), 0),
                Branch(12, "SCbranchExecz", 3), // Skip to EXEC restoration at 28.
                MoveVector(16, 3, 0),
                Branch(20, "SCbranchScc0", -2), // Back edge to 16.
                Nop(24),
                Sop1(28, "SMovB64", 126, Gen5Operand.Source(193)),
            ]);
        }
        instructions.AddRange([
            ReadFirstLane(32, 10, 5),
            MoveScalar(36, 8, 0x2000),
            MoveScalar(40, 9, 0),
            MoveScalar(44, 11, 0),
            BufferLoad(48, 8),
            EndProgram(56),
        ]);
        var plan = Extract(Program([.. instructions]));
        var memory = new TestWordMemory { Words = [0, 0x80] };
        var source = plan.Info.Buffers[(int)plan.Memory.Find(48)!.Resource].Source;

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source,
            Inputs([0x1000, 0, 64, 0, 1], memory.Read), out var descriptor));
        Assert.Equal(0x80u, descriptor.Dwords[2]);
        Assert.Equal(1u, memory.Reads);
    }

    [Fact]
    public void UnaddressedLoadAddsTheScalarOffset()
    {
        // SOFFSET (s6 = 4) selects the second word; the immediate offset is zero.
        var load = BufferAccess(4, "BufferLoadDword", 0, vectorData: 5);
        load = load with { Sources = [load.Sources[0], load.Sources[1], Gen5Operand.Scalar(6)] };
        var plan = Extract(Program(
            MoveScalar(0, 6, 4),
            load,
            ReadFirstLane(12, 10, 5),
            MoveScalar(16, 8, 0x2000),
            MoveScalar(20, 9, 0),
            MoveScalar(24, 11, 0),
            BufferLoad(28, 8),
            EndProgram(36)));
        var memory = new TestWordMemory { Words = [0, 0x80] };
        var source = plan.Info.Buffers[(int)plan.Memory.Find(28)!.Resource].Source;

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source,
            Inputs([0x1000, 0, 64, 0, 1], memory.Read), out var descriptor));
        Assert.Equal(0x80u, descriptor.Dwords[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadUnderPartialExecDoesNotDefineInactiveLanes(bool initialized)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        if (initialized)
        {
            instructions.Add(MoveVector(0, 5, 16));
        }
        instructions.AddRange([
            Vopc(4, "VCmpxGtU32", Gen5Operand.Scalar(4), 0),
            BufferAccess(8, "BufferLoadDword", 0, vectorData: 5),
            Sop1(16, "SMovB64", 126, Gen5Operand.Source(193)),
            ReadFirstLane(20, 10, 5),
            MoveScalar(24, 8, 0x2000),
            MoveScalar(28, 9, 0),
            MoveScalar(32, 11, 0),
            BufferLoad(36, 8),
            EndProgram(44),
        ]);

        Assert.Throws<ResourcePlanException>(() => Extract(Program([.. instructions])));
    }

    [Fact]
    public void AtomicReturnIsNotUniformEvenWithoutVectorAddress()
    {
        var atomic = BufferAccess(0, "BufferAtomicAdd", 0, vectorData: 5) with
        {
            Destinations = [Gen5Operand.Vector(5)],
            Control = new Gen5BufferMemoryControl(1, 0, 5, 0, 0,
                IndexEnabled: false, OffsetEnabled: false, Glc: true, Slc: false),
        };
        var program = Program(
            MoveVector(0, 5, 1),
            atomic with { Pc = 4 },
            ReadFirstLane(12, 10, 5),
            MoveScalar(16, 8, 0x2000),
            MoveScalar(20, 9, 0),
            MoveScalar(24, 11, 0),
            BufferLoad(28, 8),
            EndProgram(36));

        Assert.Throws<ResourcePlanException>(() => Extract(program));
    }
}
