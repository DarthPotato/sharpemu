// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

internal static class DescriptorShaderData
{
    internal static void Write(ShaderStageResources stage, ShaderProgramInfo program, BindingLayout layout,
        Span<uint> shaderData)
    {
        var snapshot = stage.Resources;
        for (var index = 0; index < layout.UserDataRegisters.Count; index++)
        {
            var register = layout.UserDataRegisters[index];
            var userIndex = (int)(register - program.UserDataBase);
            if (register < program.UserDataBase || userIndex >= snapshot.UserData.Length)
                throw SubmissionScheduler.Fatal($"A user register is outside the draw's user data: register={register} base={program.UserDataBase} count={snapshot.UserData.Length} hash=0x{program.Hash:X16}.");
            shaderData[index] = snapshot.UserData[userIndex];
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
