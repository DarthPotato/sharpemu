// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    internal static uint[] MergedHullUserData(UserScalarRegisters registers, uint declaredCount, ulong backAddress)
    {
        var front = UserData(registers, declaredCount, false, 0, "merged-hull");
        // Carry the architectural s0:s1 pair independently of s8 and above.
        // Lowering copies these appended inputs before executing guest code.
        var data = new uint[front.Length + 2];
        front.CopyTo(data, 0);
        data[front.Length] = (uint)backAddress;
        data[front.Length + 1] = (uint)(backAddress >> 32);
        return data;
    }

    internal static bool IsMergedTessellationMask(uint stages)
    {
        // LS_EN=1, HS_EN=1, ES_EN=1, DYNAMIC_HS=1, PRIMGEN_EN=1,
        // PRIMGEN_PASSTHRU_EN=1, without a geometry or legacy vertex stage.
        const uint mask = 1u | (1u << 2) | (1u << 3) | (1u << 8) | (1u << 13) | (1u << 25);
        return (stages & ~((1u << 22) | (1u << 23))) == mask;
    }

    // The merged local/hull program of a tessellated draw, read from its registers and guest headers
    // before any cache is touched. A header it cannot read is kept as Error and raised only when the
    // hull is resolved, as before: a draw whose other stages are unavailable never reaches it.
    private sealed record HullSource(ShaderSource? Source, RegisteredShader? Registered, string? Error);

    private HullSource PrepareHullSource(VertexStageRegisters vertex)
    {
        var outer = SubmissionScheduler.ThrowOnFatal;
        SubmissionScheduler.ThrowOnFatal = true;
        try
        {
            return PrepareHullSourceCore(vertex);
        }
        catch (SubmissionScheduler.AbandonedWorkException error)
        {
            return new HullSource(null, null, error.Message);
        }
        finally
        {
            SubmissionScheduler.ThrowOnFatal = outer;
        }
    }

    private HullSource PrepareHullSourceCore(VertexStageRegisters vertex)
    {
        var address = vertex.LocalAddress != 0 ? vertex.LocalAddress : vertex.HullAddress;
        var registered = _registry.Require(address, "merged-hull");
        if (!registered.IsFused)
            return new HullSource(null, null, "The tessellation path requires a registered merged local/hull program.");
        var count = vertex.HullResource2.UserScalarCount;
        // A merged front may consume more user data than the back's RSRC2.
        // Reading its own declared count preserves the guest's AGC header.
        if (!Gen5ShaderTranslator.TryGetFusedProgramParts(_context, address, out var frontHeader, out _, out _) ||
            !_context.TryReadUInt64(frontHeader + 0x20, out var shRegisters) ||
            !_context.TryReadUInt32(frontHeader + 0x5C, out var shCount))
            return new HullSource(null, null, "The merged hull front has an unreadable register header.");
        for (uint index = 0; index < (shCount & 0xFF); index++)
        {
            if (!_context.TryReadUInt32(shRegisters + index * 8, out var offset) ||
                !_context.TryReadUInt32(shRegisters + index * 8 + 4, out var value))
                return new HullSource(null, null, "The merged hull front has an unreadable register entry.");
            if (offset == ShaderRegisterOffset.SpiShaderPgmRsrc2Hs)
                count = Math.Max(count, HullResource2.Decode(value).UserScalarCount);
        }
        var source = PrepareSource(address, ShaderStage.Compute, "merged-hull", vertex.HullUserScalars,
            count, false, VertexUserDataBase);
        return new HullSource(source with { UserData = MergedHullUserData(vertex.HullUserScalars, count, vertex.HullUserDataAddress) },
            registered, null);
    }

    private TessellationDrawPrograms PrepareTessellationHull(HullSource hullSource, VertexStageRegisters vertex,
        ShaderInterfaceRegisters shaderInterface, Gen5TessellationInfo tessellation)
    {
        if (hullSource.Error is { } hullError) throw SubmissionScheduler.Fatal(hullError);
        var source = hullSource.Source!;
        var registered = hullSource.Registered!;
        var decoded = _graphicsPrograms.Decode(source);
        if (decoded.FusedContinuationPc is not { } entry ||
            !Gen5TessellationHullInfo.TryDecode(shaderInterface.LocalHullConfiguration, entry + 8, out var hull, out var error))
            throw SubmissionScheduler.Fatal("The merged hull has an invalid continuation or workgroup configuration.");
        var threads = (hull.PatchesPerGroup * Math.Max(hull.InputControlPoints, hull.OutputControlPoints) + 63) & ~63u;
        var input = new ComputeInputInfo
        {
            ThreadsX = threads, ThreadsY = 1, ThreadsZ = 1, WaveSize = 64, ThreadIdCount = 1,
            LocalDataShareDwords = (uint)vertex.HullResource2.LocalDataShareSize * 128,
            ScratchDwords = registered.ScratchDwords, NeedsLocalDataShareBarriers = true,
        };
        var cursor = 0u;
        var handle = _graphicsPrograms.GetOrCompile(source, new StageCompileOptions
            { ComputeInfo = input, TessellationHull = hull }, ref cursor, out var stage);
        input.Stage = stage;
        return new(tessellation, hull, new ComputeProgram { Program = handle, Input = input });
    }
}
