// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Local investigation harness (not for commit): retranslates prewarm records to SPIR-V files.
// SPVDUMP_PREWARM_DIR=<pipeline_cache\TITLE> SPVDUMP_OUT=<dir> [SPVDUMP_HASHES=50CD...,B3A2...]

using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;
using SharpEmu.Libs.Gpu.Vulkan;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class ZzPrewarmSpirvDumpHarness
{
    [Fact]
    public void DumpPrewarmSpirv()
    {
        var dir = Environment.GetEnvironmentVariable("SPVDUMP_PREWARM_DIR");
        var output = Environment.GetEnvironmentVariable("SPVDUMP_OUT");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(output)) return;
        var hashes = (Environment.GetEnvironmentVariable("SPVDUMP_HASHES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => Convert.ToUInt64(text, 16)).ToHashSet();
        Directory.CreateDirectory(output);
        using var list = ShaderPrewarmList.Open(dir) ?? throw new InvalidOperationException("no prewarm list");
        var backend = new VulkanGuestGpuBackend();
        var report = new List<string>();
        foreach (var (record, code) in list.LoadedComputes())
        {
            if (hashes.Count != 0 && !hashes.Contains(record.Hash)) continue;
            var ok = ShaderProgramCache.TryCompilePrewarm(record, code, backend, sharedInt64Atomics: true, execGuardElision: true,
                signedZeroInfNanPreserve: true, out var compiled, out _, out var error,
                hostWave64Supported: false, hostComputeSubgroup32: true, runtimeImageDescriptorsSupported: true,
                exactFloat16Conversion: Environment.GetEnvironmentVariable("SPVDUMP_EXACT16") != "0");
            var name = $"{record.Hash:X16}-{ShaderPrewarmList.Identity(record):X16}.spv";
            if (ok) File.WriteAllBytes(Path.Combine(output, name), compiled!.Payload);
            report.Add($"{name}\t{(ok ? compiled!.Payload.Length.ToString() : "FAIL " + error)}");
        }

        File.WriteAllLines(Path.Combine(output, "report.txt"), report);
    }
}
