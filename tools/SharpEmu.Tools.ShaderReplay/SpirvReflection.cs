// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

internal sealed record NativeBinding(uint Set, uint Binding, DescriptorType Type, uint Count, bool Runtime = false);

// Deliberately limited to the translator's descriptor and numeric interface types.
// Unknown descriptor types fail rather than silently producing an incompatible layout.
internal sealed class SpirvReflection
{
    public ShaderStageFlags Stage;
    public string EntryPoint = "";
    public readonly List<NativeBinding> Bindings = [];
    public readonly Dictionary<uint, Format> Inputs = [], Outputs = [];

    public static SpirvReflection Reflect(byte[] source)
    {
        var words = SpirvNonce.Read(source);
        var instructions = new List<(ushort Op, uint[] A)>();
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
            instructions.Add(((ushort)words[i], words.AsSpan(i + 1, (int)(words[i] >> 16) - 1).ToArray()));
        var types = instructions.Where(x => x.Op is >= 19 and <= 39).ToDictionary(x => x.A[0]);
        var constants = instructions.Where(x => x.Op == 43 && x.A.Length >= 3).ToDictionary(x => x.A[1], x => x.A[2]);
        var decorations = new Dictionary<(uint Id, uint Decoration), uint>();
        foreach (var (op, a) in instructions.Where(x => x.Op == 71))
            decorations[(a[0], a[1])] = a.Length > 2 ? a[2] : 0;
        var result = new SpirvReflection();
        var entries = instructions.Where(x => x.Op == 15).ToArray();
        if (entries.Length != 1) throw new NotSupportedException("Expected one SPIR-V entry point");
        var entry = entries[0].A;
        result.Stage = entry[0] switch
        {
            0 => ShaderStageFlags.VertexBit, 4 => ShaderStageFlags.FragmentBit, 5 => ShaderStageFlags.ComputeBit,
            5365 => ShaderStageFlags.MeshBitExt,
            _ => throw new NotSupportedException($"Execution model {entry[0]} is unsupported")
        };
        var nameBytes = MemoryMarshal.AsBytes(entry.AsSpan(2));
        result.EntryPoint = Encoding.UTF8.GetString(nameBytes[..nameBytes.IndexOf((byte)0)]);
        foreach (var (_, a) in instructions.Where(x => x.Op == 59))
        {
            var id = a[1];
            var pointer = types[a[0]].A;
            var type = pointer[2];
            if ((a[2] == 1 && result.Stage == ShaderStageFlags.VertexBit ||
                 a[2] == 3 && result.Stage == ShaderStageFlags.FragmentBit) &&
                decorations.TryGetValue((id, 30), out var location))
            {
                var t = types[type];
                uint components = 1;
                if (t.Op == 23) { components = t.A[2]; t = types[t.A[1]]; }
                if (t.Op is not (21 or 22) || t.A[1] != 32 || components is < 1 or > 4)
                    throw new NotSupportedException("Only scalar/vector 32-bit graphics locations are supported");
                var baseFormat = t.Op == 22 ? Format.R32Sfloat : t.A[2] != 0 ? Format.R32Sint : Format.R32Uint;
                var format = (Format)((int)baseFormat + (int)(components - 1) * 3);
                (a[2] == 1 ? result.Inputs : result.Outputs)[location] = format;
            }
            if (!decorations.TryGetValue((id, 33), out var binding)) continue;
            if (!decorations.TryGetValue((id, 34), out var set)) throw new InvalidDataException("Descriptor has no set");
            uint count = 1;
            var runtime = false;
            while (types[type].Op is 28 or 29)
            {
                var array = types[type];
                if (array.Op == 29) runtime = true;
                else count = checked(count * constants[array.A[2]]);
                type = array.A[1];
            }
            var value = types[type];
            var descriptor = a[2] switch
            {
                12 => DescriptorType.StorageBuffer,
                2 => decorations.ContainsKey((type, 3)) ? DescriptorType.StorageBuffer : DescriptorType.UniformBuffer,
                0 => value.Op switch
                {
                    26 => DescriptorType.Sampler,
                    27 => DescriptorType.CombinedImageSampler,
                    25 => value.A[2] == 5
                        ? value.A[6] == 2 ? DescriptorType.StorageTexelBuffer : DescriptorType.UniformTexelBuffer
                        : value.A[2] == 6 ? DescriptorType.InputAttachment
                        : value.A[6] == 2 ? DescriptorType.StorageImage : DescriptorType.SampledImage,
                    _ => throw new NotSupportedException($"UniformConstant type opcode {value.Op}")
                },
                _ => throw new NotSupportedException($"Descriptor storage class {a[2]}")
            };
            result.Bindings.Add(new(set, binding, descriptor, count, runtime));
        }
        return result;
    }
}
