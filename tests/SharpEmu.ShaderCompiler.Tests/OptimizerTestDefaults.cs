// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Tests;

internal static class OptimizerTestDefaults
{
    // The emulator keeps the SPIR-V builder optimizations off by default
    // (SpirvOptimizationPolicy); these tests pin the optimized output.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Enable() => Environment.SetEnvironmentVariable("SHARPEMU_SPIRV_OPTIMIZE", "1");
#pragma warning restore CA2255
}
