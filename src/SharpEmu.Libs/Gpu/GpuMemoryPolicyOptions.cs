// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Libs.Gpu;

// Diagnostic A/B controls, read once at startup. Defaults keep memory-safe policies.
internal static class GpuMemoryPolicyOptions
{
    internal static bool Enabled(string name) => Environment.GetEnvironmentVariable(name) != "0";
    internal static ulong Megabytes(string name, ulong fallback) =>
        ulong.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value <= 4096 ? value << 20 : fallback;
    internal static readonly bool FaultWrittenPass = Enabled("SHARPEMU_FAULT_WRITTEN_PASS");
    internal static readonly bool FaultKernelWindows = Enabled("SHARPEMU_FAULT_KERNEL_WINDOWS");
    internal static readonly bool FaultRetain = Enabled("SHARPEMU_FAULT_RETAIN");
    internal static readonly bool FaultRetainGated = Enabled("SHARPEMU_FAULT_RETAIN_GATED");
    internal static readonly bool OomBatch = Enabled("SHARPEMU_OOM_BATCH");
    internal static readonly bool DynamicImageBudget = Enabled("SHARPEMU_DYNAMIC_IMAGE_BUDGET");
    internal static readonly bool OomReleaseScratch = Enabled("SHARPEMU_OOM_RELEASE_SCRATCH");
}
