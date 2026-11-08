// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

public sealed class SpirvNonceTests(ITestOutputHelper output)
{
    internal static byte[] Compute(bool debug = false)
    {
        var words = new List<uint>
        {
            0x07230203, 0x00010300, 0, 5, 0,
            (2u << 16) | 17, 1, // Shader
            (3u << 16) | 14, 0, 1, // Logical GLSL450
            (5u << 16) | 15, 5, 3, 0x6e69616d, 0, // GLCompute %3 main
            (6u << 16) | 16, 3, 17, 1, 1, 1, // LocalSize
        };
        if (debug) words.AddRange([(4u << 16) | 5, 3, 0x6e69616d, 0]); // OpName
        words.AddRange([
            (2u << 16) | 19, 1,
            (3u << 16) | 33, 2, 1,
            (5u << 16) | 54, 1, 3, 0, 2,
            (2u << 16) | 248, 4,
            (1u << 16) | 253,
            (1u << 16) | 56
        ]);
        return MemoryMarshal.AsBytes(words.ToArray().AsSpan()).ToArray();
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("1234")]
    [InlineData("a-GUID-é-雪")]
    public void NoncePreservesAllOriginalWordsAndHeader(string nonce)
    {
        var source = Compute();
        var copy = source.ToArray();
        var result = SpirvNonce.Inject(source, nonce);
        var words = SpirvNonce.Read(result);
        Assert.Equal(copy, source);
        Assert.Equal(source.AsSpan(0, 20).ToArray(), result.AsSpan(0, 20).ToArray());
        var original = new List<uint>(words.AsSpan(0, 5).ToArray());
        var count = 0;
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
            if ((ushort)words[i] == 4) count++;
            else original.AddRange(words.AsSpan(i, (int)(words[i] >> 16)).ToArray());
        Assert.Equal(1, count);
        Assert.Equal(SpirvNonce.Read(source), original);
        Assert.Equal(0, result.Length % 4);
    }

    [Fact]
    public void SameNonceIsDeterministicAndFreshNonceChangesBytes()
    {
        var source = Compute();
        Assert.Equal(SpirvNonce.Inject(source, "a"), SpirvNonce.Inject(source, "a"));
        Assert.NotEqual(SpirvNonce.Inject(source, "a"), SpirvNonce.Inject(source, "b"));
    }

    [Fact]
    public void RejectsMalformedModulesAndInvalidStrings()
    {
        Assert.Throws<InvalidDataException>(() => SpirvNonce.Inject(new byte[20], "a"));
        var bytes = Compute();
        Assert.Throws<InvalidDataException>(() => SpirvNonce.Inject(bytes[..^1], "a"));
        var words = SpirvNonce.Read(bytes);
        words[5] = 17; // zero word count
        Assert.Throws<InvalidDataException>(() => SpirvNonce.Inject(MemoryMarshal.AsBytes(words.AsSpan()), "a"));
        words[5] = (65535u << 16) | 17;
        Assert.Throws<InvalidDataException>(() => SpirvNonce.Inject(MemoryMarshal.AsBytes(words.AsSpan()), "a"));
        Assert.Throws<ArgumentException>(() => SpirvNonce.Inject(bytes, "x\0y"));
        Assert.Throws<ArgumentException>(() => SpirvNonce.Inject(bytes, new string('x', 300000)));
    }

    [Fact]
    public void ReflectionRetainsEntryPointAndExecutionModel()
    {
        var reflection = SpirvReflection.Reflect(SpirvNonce.Inject(Compute(), "reflection"));
        Assert.Equal("main", reflection.EntryPoint);
        Assert.Equal(ShaderStageFlags.ComputeBit, reflection.Stage);
        Assert.Empty(reflection.Bindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpirvValAcceptsNonceAtLegalDebugPosition(bool existingDebug)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var validator = sdk is null ? null : Path.Combine(sdk, "Bin", "spirv-val.exe");
        if (!File.Exists(validator) && Directory.Exists("C:/VulkanSDK"))
            validator = Directory.GetDirectories("C:/VulkanSDK").OrderDescending()
                .Select(p => Path.Combine(p, "Bin", "spirv-val.exe")).FirstOrDefault(File.Exists);
        if (!File.Exists(validator)) { output.WriteLine("spirv-val unavailable; structural checks still ran"); return; }
        var temp = Path.Combine(Path.GetTempPath(), "nonce-test-" + Guid.NewGuid().ToString("N") + ".spv");
        try
        {
            // Also validate repeated insertion before preexisting source extensions.
            File.WriteAllBytes(temp, SpirvNonce.Inject(SpirvNonce.Inject(Compute(existingDebug), "one"), "two-雪"));
            var start = new ProcessStartInfo(validator!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--target-env"); start.ArgumentList.Add("vulkan1.3"); start.ArgumentList.Add(temp);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30000), "spirv-val timed out");
            Assert.True(process.ExitCode == 0, stdout + stderr);
            output.WriteLine("Validated with " + validator);
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public void NativeDefaultsIncludeTheSevenReportedSlowHashes()
    {
        var options = ReplayOptions.Parse(["--native-bench", "result", "--mode", "current=dump"]);
        Assert.Equal(7, options.Hashes.Count);
        Assert.Contains(0x54904FB419D79E49ul, options.Hashes);
        Assert.Equal(2, options.Repeats);
    }
}
