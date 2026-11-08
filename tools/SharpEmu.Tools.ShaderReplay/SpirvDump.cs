// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

internal sealed record Blob(string Id, string Hash, string Kind, string File, int Bytes, string Sha256,
    uint SubgroupSize, NativeBinding[] Bindings);
internal sealed record GraphicsPair(string Id, string Vertex, string? Pixel);
internal sealed class DumpManifest
{
    public int Schema { get; set; } = 1;
    public string Mode { get; set; } = "";
    public string TranslatorAssemblySha256 { get; set; } = "";
    public string HostCapabilities { get; set; } = "";
    public uint PushConstantBytes { get; set; } = PushData.ByteSize;
    public List<Blob> Stages { get; set; } = [];
    public List<GraphicsPair> Graphics { get; set; } = [];
}
internal static class SpirvDump
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Add(DumpManifest manifest, string directory, string id, ShaderCompileRequest request, byte[] spirv)
    {
        Directory.CreateDirectory(directory);
        var file = $"{id}-{request.Hash:X16}.spv";
        File.WriteAllBytes(Path.Combine(directory, file), spirv);
        var bindings = request.Bindings.Descriptors.Select(b => new NativeBinding(0,
            BindingLayout.NativeBindingIndex(request.Stage, b.Kind), DescriptorWriter.DescriptorType(b.Kind),
            DescriptorWriter.DescriptorCount(b))).ToArray();
        manifest.Stages.Add(new(id, $"{request.Hash:X16}", request.Stage.ToString(), file, spirv.Length,
            Convert.ToHexString(SHA256.HashData(spirv)), request.Stage == ShaderStage.Compute && request.HostComputeSubgroup32 ? 32u : 0u, bindings));
    }

    public static void Save(DumpManifest manifest, string directory) =>
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
}
