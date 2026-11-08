// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void HoistedFragmentInputsPreserveRandomArithmeticAndControlFlow()
    {
        if (!Ready()) return;
        for (var seed = 0; seed < 64; seed++)
            Assert.Equal(DrawFragment(RandomFragment(seed, false)), DrawFragment(RandomFragment(seed, true)));
    }

    private byte[] DrawFragment(byte[] fragment)
    {
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1, -1, 0.125f, -1, -1, 3));
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices, interpolationShader: fragment);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        harness.Shutdown();
        return pixels;
    }

    private static byte[] RandomFragment(int seed, bool hoist)
    {
        var random = new System.Random(seed);
        var m = new SpirvModuleBuilder { Optimize = true, HoistEntry = hoist, InheritBlockState = false };
        m.AddCapability(SpirvCapability.Shader);
        var f = m.TypeFloat(32); var u = m.TypeInt(32, false); var v = m.TypeVector(f, 4);
        var input = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, v), SpirvStorageClass.Input);
        var coord = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, v), SpirvStorageClass.Input);
        var lane = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Input, u), SpirvStorageClass.Input);
        m.AddCapability(SpirvCapability.GroupNonUniform);
        m.AddCapability(SpirvCapability.GroupNonUniformBallot);
        m.AddDecoration(lane, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
        m.AddDecoration(lane, SpirvDecoration.Flat);
        m.AddDecoration(input, SpirvDecoration.Location, 0);
        m.AddDecoration(coord, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.FragCoord);
        var output = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Output, v), SpirvStorageClass.Output);
        m.AddDecoration(output, SpirvDecoration.Location, 0);
        var register = m.AddGlobalVariable(m.TypePointer(SpirvStorageClass.Private, u), SpirvStorageClass.Private, m.Constant(u, 0));
        var main = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid())); m.AddLabel();
        uint C(uint n) => m.Constant(u, n);
        uint Vote() {
            var id = m.AddInstruction(SpirvOp.Load, u, lane);
            var mask = m.AddInstruction(SpirvOp.GroupNonUniformBallot, m.TypeVector(u, 4), C(3), m.ConstantBool(true));
            var low = m.AddInstruction(SpirvOp.CompositeExtract, u, mask, 0);
            var own = m.AddInstruction(SpirvOp.ShiftLeftLogical, u, C(1), id);
            var active = m.AddInstruction(SpirvOp.INotEqual, m.TypeBool(), m.AddInstruction(SpirvOp.BitwiseAnd, u, low, own), C(0));
            return m.AddInstruction(SpirvOp.Select, u, active, C(0xFFFFFFFF), C(0));
        }
        uint Read() => random.Next(5) switch {
            0 => m.AddInstruction(SpirvOp.Load, u, lane),
            1 => m.AddInstruction(SpirvOp.Bitcast, u, m.AddInstruction(SpirvOp.CompositeExtract, f, m.AddInstruction(SpirvOp.Load, v, input), (uint)random.Next(4))),
            2 => m.AddInstruction(SpirvOp.ConvertFToU, u, m.AddInstruction(SpirvOp.CompositeExtract, f, m.AddInstruction(SpirvOp.Load, v, coord), (uint)random.Next(2))),
            3 => Vote(),
            _ => m.AddInstruction(SpirvOp.Load, u, register)
        };
        void Work() { for (var k = 0; k < 50; k++) {
            var x = Read(); var y = Read();
            var result = random.Next(7) switch {
                0 => m.AddInstruction(SpirvOp.IAdd, u, x, y),
                1 => m.AddInstruction(SpirvOp.IMul, u, x, y),
                2 => m.AddInstruction(SpirvOp.ISub, u, x, y),
                3 => m.AddInstruction(SpirvOp.BitwiseAnd, u, x, y),
                4 => m.AddInstruction(SpirvOp.BitwiseXor, u, x, y),
                5 => m.AddInstruction(SpirvOp.ShiftRightLogical, u, x, m.AddInstruction(SpirvOp.BitwiseAnd, u, y, C(31))),
                _ => m.AddInstruction(SpirvOp.ShiftLeftLogical, u, x, m.AddInstruction(SpirvOp.BitwiseAnd, u, y, C(31)))
            }; m.AddStatement(SpirvOp.Store, register, result);
        } }
        Work();
        var yes = m.AllocateId(); var no = m.AllocateId(); var merge = m.AllocateId();
        var test = m.AddInstruction(SpirvOp.INotEqual, m.TypeBool(), m.AddInstruction(SpirvOp.BitwiseAnd, u, Read(), C(1)), C(0));
        m.AddStatement(SpirvOp.SelectionMerge, merge, 0); m.AddStatement(SpirvOp.BranchConditional, test, yes, no);
        m.AddLabel(yes); Work(); m.AddStatement(SpirvOp.Branch, merge);
        m.AddLabel(no); Work(); m.AddStatement(SpirvOp.Branch, merge); m.AddLabel(merge); Work();
        var digest = m.AddInstruction(SpirvOp.Load, u, register);
        var color = new uint[4];
        for (var i = 0; i < 3; i++) {
            var component = m.AddInstruction(SpirvOp.FDiv, f, m.AddInstruction(SpirvOp.ConvertUToF, f,
                m.AddInstruction(SpirvOp.BitwiseAnd, u, m.AddInstruction(SpirvOp.ShiftRightLogical, u, digest, C((uint)i * 8)), C(255))), m.ConstantFloat(f, 255));
            color[i] = m.AddInstruction(SpirvOp.FAdd, f, m.AddInstruction((SpirvOp)(i == 1 ? 211 : 210), f, component), m.ConstantFloat(f, 0.5f));
        }
        color[3] = m.ConstantFloat(f, 1);
        m.AddStatement(SpirvOp.Store, output, m.AddInstruction(SpirvOp.CompositeConstruct, v, color));
        m.AddStatement(SpirvOp.Return); m.EndFunction();
        m.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [input, coord, lane, output, register]);
        m.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return m.Build();
    }

    [Fact]
    public void EntryHoistingPreservesBallotBasedDerivativeAtPartialQuadCoverage()
    {
        if (!Ready()) return;
        var reference = DrawHelperBallot(false);
        var optimized = DrawHelperBallot(true);
        Assert.Equal(255u, optimized);
        Assert.Equal(reference, optimized);
    }

    private uint DrawHelperBallot(bool hoist)
    {
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        // At (0, 8) only the left column of the quad is rasterized.
        harness.Write(vertices, Triangle(-1, -1, -1 + 2f / Size, -1, -1, 3));
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: SpirvHelperBallotTests.CreateFragment(hoist, false, bias: true));
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixel = Pixel(harness.ReadImageBytes(TargetImage(presenter, words)), 0, 8) & 255;
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
        return pixel;
    }
}
