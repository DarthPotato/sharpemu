// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

internal static class SpirvNonce
{
    // OpSourceExtension belongs after the execution modes, before all other debug
    // instructions, annotations and declarations. It allocates no result ID.
    public static byte[] Inject(ReadOnlySpan<byte> source, string nonce)
    {
        var words = Read(source);
        if (nonce.Contains('\0')) throw new ArgumentException("Nonce contains NUL", nameof(nonce));
        var at = 5;
        while (at < words.Length && (ushort)words[at] is 17 or 10 or 11 or 14 or 15 or 16 or 331)
            at += (int)(words[at] >> 16);
        var literal = Encoding.UTF8.GetBytes("SharpEmu.NativeBench." + nonce + "\0");
        var count = 1 + (literal.Length + 3) / 4;
        if (count > ushort.MaxValue) throw new ArgumentException("Nonce is too long", nameof(nonce));
        var output = new uint[words.Length + count];
        words.AsSpan(0, at).CopyTo(output);
        output[at] = ((uint)count << 16) | 4u; // OpSourceExtension
        literal.CopyTo(MemoryMarshal.AsBytes(output.AsSpan(at + 1, count - 1)));
        words.AsSpan(at).CopyTo(output.AsSpan(at + count));
        return MemoryMarshal.AsBytes(output.AsSpan()).ToArray();
    }

    public static uint[] Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < 20 || source.Length % 4 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(source) != 0x07230203)
            throw new InvalidDataException("Invalid little-endian SPIR-V header/byte length");
        var words = MemoryMarshal.Cast<byte, uint>(source).ToArray();
        if (words[3] == 0 || words[4] != 0) throw new InvalidDataException("Invalid SPIR-V bound/schema");
        for (var i = 5; i < words.Length;)
        {
            var count = (int)(words[i] >> 16);
            if (count == 0 || count > words.Length - i) throw new InvalidDataException($"Invalid SPIR-V word count at {i}");
            i += count;
        }
        return words;
    }
}
