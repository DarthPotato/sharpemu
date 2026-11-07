// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// Process-constant settings: in-memory programs never change policy beneath their cache.
public static class SpirvOptimizationPolicy
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SHARPEMU_SPIRV_OPTIMIZE") != "0";
    // A guest instruction can expand into many guarded SPIR-V blocks. The replay
    // corpus shows that even medium guest programs can grow huge inherited maps.
    // Keep the cheap local passes for those requests, without speculative inheritance.
    public static int MaxInheritedInstructions { get; } = ReadLimit("SHARPEMU_SPIRV_MAX_INHERIT_INSTRUCTIONS");
    public static int MaxExpressionInstructions { get; } = ReadLimit("SHARPEMU_SPIRV_MAX_EXPRESSION_INSTRUCTIONS");
    public static string CacheIdentity => $"spirv-opt-v3:{Enabled}:inherit-max={MaxInheritedInstructions}:expression-max={MaxExpressionInstructions}";

    public static bool AllowsInheritance(int guestInstructionCount) => Enabled && guestInstructionCount <= MaxInheritedInstructions;
    public static bool AllowsExpressions(int guestInstructionCount) => Enabled && guestInstructionCount <= MaxExpressionInstructions;

    private static int ReadLimit(string name)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable(name), out var limit))
        {
            if (limit == -1) return int.MaxValue;
            if (limit >= 0) return limit;
        }
        return 128;
    }
}
