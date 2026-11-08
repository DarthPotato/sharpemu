// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// Process-constant settings: in-memory programs never change policy beneath their cache.
public static class SpirvOptimizationPolicy
{
    // Off unless SHARPEMU_SPIRV_OPTIMIZE=1: with the builder optimizations on, Ghost of
    // Yotei renders smeared characters and striped terrain (cause not found yet).
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SHARPEMU_SPIRV_OPTIMIZE") == "1";
    public static bool Forward { get; } = ReadSwitch("FORWARD");
    public static bool StoreElision { get; } = ReadSwitch("STORE_ELISION");
    public static bool Unread { get; } = ReadSwitch("UNREAD");
    public static bool Dce { get; } = ReadSwitch("DCE");
    public static bool LaneMask { get; } = ReadSwitch("LANE_MASK");
    public static bool Hoist { get; } = ReadSwitch("HOIST");
    public static bool Inherit { get; } = ReadSwitch("INHERIT");
    public static bool Expressions { get; } = ReadSwitch("EXPRESSIONS");
    // A guest instruction can expand into many guarded SPIR-V blocks. The replay
    // corpus shows that even medium guest programs can grow huge inherited maps.
    // Keep the cheap local passes for those requests, without speculative inheritance.
    public static int MaxInheritedInstructions { get; } = ReadLimit("SHARPEMU_SPIRV_MAX_INHERIT_INSTRUCTIONS");
    public static int MaxExpressionInstructions { get; } = ReadLimit("SHARPEMU_SPIRV_MAX_EXPRESSION_INSTRUCTIONS");
    public static string CacheIdentity => $"spirv-opt-v4:{Enabled}:inherit-max={MaxInheritedInstructions}:expression-max={MaxExpressionInstructions}" +
        $":forward={Forward}:store-elision={StoreElision}:unread={Unread}:dce={Dce}:lane-mask={LaneMask}:hoist={Hoist}:inherit={Inherit}:expressions={Expressions}";

    public static bool AllowsInheritance(int guestInstructionCount) => Enabled && Inherit && guestInstructionCount <= MaxInheritedInstructions;
    public static bool AllowsExpressions(int guestInstructionCount) => Enabled && guestInstructionCount <= MaxExpressionInstructions;

    private static bool ReadSwitch(string suffix) => Environment.GetEnvironmentVariable("SHARPEMU_SPIRV_OPT_" + suffix) != "0";

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
