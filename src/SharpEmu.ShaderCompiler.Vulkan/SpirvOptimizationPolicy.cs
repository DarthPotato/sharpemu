// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// Process-constant settings: in-memory programs never change policy beneath their cache.
public static class SpirvOptimizationPolicy
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SHARPEMU_SPIRV_OPTIMIZE") != "0";
    public static string CacheIdentity => $"spirv-opt-v1:{Enabled}";
}
