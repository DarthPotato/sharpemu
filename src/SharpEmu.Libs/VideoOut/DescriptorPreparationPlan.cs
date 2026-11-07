// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

internal static class DescriptorPreparationOptions
{
    internal static readonly bool Fast = ParseFast(Environment.GetEnvironmentVariable("SHARPEMU_DESC_PREP_FAST"));
    internal static bool ParseFast(string? value) => value != "0";
}

// Only immutable compiled-program facts live here. No guest contents, handles, mappings,
// ownership, metadata or upload state are cached. A new program gets a new plan.
internal sealed class DescriptorPreparationPlan
{
    internal static DescriptorPreparationPlan? Create(ShaderProgramInfo program) => DescriptorPreparationOptions.Fast ? new(program) : null;
    private readonly ShaderProgramInfo _program;
    private readonly BindingLayout _layout;
    private readonly (int Source, int Destination, int Count)[] _runs;
    private readonly int _requiredUserData;
    private readonly bool _validRegisters;
    internal readonly int[] WrittenBuffers;
    internal readonly bool GlobalDataShare;
    internal readonly bool CallMarks;
    internal readonly bool FlattenedTable;
    internal readonly bool ShaderData;
    internal readonly bool CallRecords;

    internal DescriptorPreparationPlan(ShaderProgramInfo program)
    {
        _program = program;
        _layout = program.Bindings!;
        GlobalDataShare = _layout.Find(DescriptorBindingKind.GlobalDataShare) is not null;
        CallMarks = _layout.Find(DescriptorBindingKind.CallMarks) is not null;
        FlattenedTable = _layout.Find(DescriptorBindingKind.FlattenedResourceTable) is not null;
        ShaderData = _layout.Find(DescriptorBindingKind.ShaderData) is not null;
        CallRecords = _layout.Find(DescriptorBindingKind.CallRecords) is not null;
        var written = new List<int>();
        for (var index = 0; index < program.Resources!.Info.Buffers.Count; index++)
            if (program.Resources.Info.Buffers[index].Written) written.Add(index);
        WrittenBuffers = written.ToArray();

        var runs = new List<(int Source, int Destination, int Count)>();
        _validRegisters = true;
        for (var index = 0; index < _layout.UserDataRegisters.Count; index++)
        {
            var register = _layout.UserDataRegisters[index];
            var offset = (long)register - program.UserDataBase;
            if (offset < 0 || offset >= int.MaxValue || index >= _layout.ShaderDataDwordCount)
            {
                _validRegisters = false;
                break;
            }
            var source = (int)offset;
            _requiredUserData = Math.Max(_requiredUserData, source + 1);
            if (runs.Count != 0 && runs[^1] is var previous && previous.Source + previous.Count == source)
                runs[^1] = (previous.Source, previous.Destination, previous.Count + 1);
            else
                runs.Add((source, index, 1));
        }
        _runs = runs.ToArray();
    }

    internal bool TryWriteUserData(ShaderStageResources stage, Span<uint> destination)
    {
        // A shortened snapshot must still take the original validation/error path. Values
        // are copied from this call, never retained from a previous snapshot or memo hit.
        if (!_validRegisters || !ReferenceEquals(stage.Program, _program) ||
            !ReferenceEquals(stage.Program.Bindings, _layout) || stage.Resources.UserData.Length < _requiredUserData ||
            destination.Length != _layout.ShaderDataDwordCount)
            return false;
        foreach (var run in _runs)
            stage.Resources.UserData.AsSpan(run.Source, run.Count).CopyTo(destination.Slice(run.Destination, run.Count));
        return true;
    }
}

internal static class DescriptorShaderData
{
    internal static void Write(ShaderStageResources stage, ShaderProgramInfo program, BindingLayout layout,
        Span<uint> shaderData, DescriptorPreparationPlan? plan, bool fast)
    {
        var snapshot = stage.Resources;
        if (!(fast && plan is not null && plan.TryWriteUserData(stage, shaderData)))
        {
            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
            {
                var register = layout.UserDataRegisters[index];
                var userIndex = (int)(register - program.UserDataBase);
                if (register < program.UserDataBase || userIndex >= snapshot.UserData.Length)
                    throw SubmissionScheduler.Fatal($"A user register is outside the draw's user data: register={register} base={program.UserDataBase} count={snapshot.UserData.Length} hash=0x{program.Hash:X16}.");
                shaderData[index] = snapshot.UserData[userIndex];
            }
        }
        if (layout.UsesShaderBase)
        {
            shaderData[(int)layout.ShaderBaseDword] = (uint)stage.ShaderBase;
            shaderData[(int)layout.ShaderBaseDword + 1] = (uint)(stage.ShaderBase >> 32);
        }
        stage.WriteObjectId(shaderData);
        stage.WriteDispatchThreadLimits(shaderData);
        stage.WriteMeshDrawParameters(shaderData);
        stage.WriteTessellationData(shaderData);
        stage.WriteCallData(shaderData);
    }
}
