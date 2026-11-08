// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Xunit;

public sealed class SpirvReflectionTests
{
    private static byte[] WithDeclarations(uint bound, uint[] decorations, uint[] declarations)
    {
        var words = SpirvNonce.Read(SpirvNonceTests.Compute()).ToList();
        var declarationStart = 5;
        while ((ushort)words[declarationStart] != 19) declarationStart += (int)(words[declarationStart] >> 16);
        words.InsertRange(declarationStart, decorations);
        var functionStart = 5;
        while ((ushort)words[functionStart] != 54) functionStart += (int)(words[functionStart] >> 16);
        words.InsertRange(functionStart, declarations);
        words[3] = bound;
        return MemoryMarshal.AsBytes(words.ToArray().AsSpan()).ToArray();
    }

    [Fact]
    public void ReflectsRuntimeSamplerArrayInItsOwnSet()
    {
        var source = WithDeclarations(9,
            [(4u << 16) | 71, 8, 34, 1, (4u << 16) | 71, 8, 33, 1],
            [(2u << 16) | 26, 5, (3u << 16) | 29, 6, 5,
             (4u << 16) | 32, 7, 0, 6, (4u << 16) | 59, 7, 8, 0]);
        var binding = Assert.Single(SpirvReflection.Reflect(SpirvNonce.Inject(source, "heap")).Bindings);
        Assert.Equal(new NativeBinding(1, 1, DescriptorType.Sampler, 1, true), binding);
    }

    [Fact]
    public void ReflectsFixedDescriptorArrayLengthFromConstant()
    {
        var source = WithDeclarations(11,
            [(4u << 16) | 71, 10, 34, 0, (4u << 16) | 71, 10, 33, 17],
            [(2u << 16) | 26, 5, (4u << 16) | 21, 6, 32, 0,
             (4u << 16) | 43, 6, 7, 4, (4u << 16) | 28, 8, 5, 7,
             (4u << 16) | 32, 9, 0, 8, (4u << 16) | 59, 9, 10, 0]);
        Assert.Equal(new NativeBinding(0, 17, DescriptorType.Sampler, 4), Assert.Single(SpirvReflection.Reflect(source).Bindings));
    }

    [Fact]
    public void ReflectsStorageBufferSeparatelyFromUniformBuffer()
    {
        var source = WithDeclarations(9,
            [(4u << 16) | 71, 8, 34, 0, (4u << 16) | 71, 8, 33, 0],
            [(3u << 16) | 22, 5, 32, (3u << 16) | 30, 6, 5,
             (4u << 16) | 32, 7, 12, 6, (4u << 16) | 59, 7, 8, 12]);
        Assert.Equal(DescriptorType.StorageBuffer, Assert.Single(SpirvReflection.Reflect(source).Bindings).Type);
    }

    [Theory]
    [InlineData(1, DescriptorType.SampledImage)]
    [InlineData(2, DescriptorType.StorageImage)]
    public void ReflectsImageSampledOperand(uint sampled, DescriptorType expected)
    {
        var source = WithDeclarations(9,
            [(4u << 16) | 71, 8, 34, 0, (4u << 16) | 71, 8, 33, 7],
            [(3u << 16) | 22, 5, 32,
             (9u << 16) | 25, 6, 5, 1, 0, 0, 0, sampled, 0,
             (4u << 16) | 32, 7, 0, 6, (4u << 16) | 59, 7, 8, 0]);
        Assert.Equal(expected, Assert.Single(SpirvReflection.Reflect(source).Bindings).Type);
    }

    [Theory]
    [InlineData("spirv_bytes", "b")]
    [InlineData("translation_ms", "a")]
    public void CsvTopSelectsTheExactVariantAndUnionsExplicitHashes(string rank, string selected)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllText(path, "id,spirv_bytes,translation_ms\na,100,20\nb,200,10\n");
            var options = ReplayOptions.Parse(["--csv", path, "--top", "1", "--rank", rank, "--hash", "0xABC"]);
            Assert.Single(options.Ids);
            Assert.Contains(selected, options.Ids);
            Assert.True(options.Selected(selected, 0));
            Assert.True(options.Selected("other", 0xABC));
            Assert.False(options.Selected("other", 0));
        }
        finally { File.Delete(path); }
    }
}
