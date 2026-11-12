// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.Diagnostics;

// Ghost of Yotei 01.512 only. The game renders at one of 16 sizes (index 0 = 3840x2160, each step 128x72 smaller,
// index 15 = 1920x1080) and upscales the result to its 3840x2160 output. Its renderer picks the index from the
// display mode, and then replaces it with one int of the renderer object whenever that int is not negative; the
// game's code only reads that int. Writing an index there makes the game itself render at that size.
// Left alone, the game moves the index with its own load (dynamic resolution): under this emulator it settles at
// index 15 in gameplay but climbs after loading and in quiet scenes. In an in-run A/B, index 0 ran at 6.9 frames per
// second where index 15 ran at 10.1.
// SHARPEMU_YOTEI_RENDER_INDEX=<0..15>: hold that index for the whole run. Unset means 15 (1920x1080). "game" writes
// nothing and leaves the choice to the game.
// SHARPEMU_AB_YOTEI_RENDER_INDEX=<0..15> (measurement only): that index while the in-run A/B toggle is on, the held
// index (or the value the game left in the int) while it is off.
// The game applies a new index within a frame or two; the log shows each change as a "renders at" line. Nothing is
// written unless the code that reads the int is found at its 01.512 address, so another title or build is left
// alone. No game code or file is changed.
public static unsafe class YoteiRenderResolution
{
    private const ulong ReaderAddress = 0x8_00D5_E33C;
    private const ulong RendererAddress = 0x8_04FD_B4A0;
    private const ulong RenderWidthAddress = RendererAddress + 0x327590;
    private const ulong RenderHeightAddress = RendererAddress + 0x327594;
    private const ulong CurrentIndexAddress = RendererAddress + 0x327840;
    private const ulong OverrideAddress = RendererAddress + 0x327848;
    private const int MaximumIndex = 15;
    private const int DefaultIndex = 15;
    private const int MaximumReports = 64;

    // mov ecx,[rbx+327848h]; lea rdx,[app+0x1A50024]; mov r12,[rbp-200h]; test ecx,ecx; cmovns eax,ecx
    private static ReadOnlySpan<byte> Reader =>
    [
        0x8B, 0x8B, 0x48, 0x78, 0x32, 0x00, 0x48, 0x8D, 0x15, 0xDB, 0x1C, 0xCF, 0x00,
        0x4C, 0x8B, 0xA5, 0x00, 0xFE, 0xFF, 0xFF, 0x85, 0xC9, 0x0F, 0x49, 0xC1,
    ];

    private static readonly string? HeldText = Environment.GetEnvironmentVariable("SHARPEMU_YOTEI_RENDER_INDEX");
    private static readonly int HeldIndex = HeldText is null ? DefaultIndex : ReadIndex("SHARPEMU_YOTEI_RENDER_INDEX");
    private static readonly int ToggledIndex = ReadIndex("SHARPEMU_AB_YOTEI_RENDER_INDEX");

    private static int _state;
    private static int _original;
    private static int _written = int.MinValue;
    private static int _foreignWrites;
    private static int _reports;
    private static int _reportedIndex = int.MinValue;
    private static int _reportedWidth;
    private static int _reportedHeight;

    public static void OnFlip(ICommandStreamHost host)
    {
        if (_state < 0)
        {
            return;
        }

        var field = (int*)OverrideAddress;
        if (_state == 0)
        {
            Span<byte> found = stackalloc byte[Reader.Length];
            if (!host.TryReadGuestOperand(ReaderAddress, found) || !found.SequenceEqual(Reader))
            {
                _state = -1;
                if (HeldText is not null || ToggledIndex >= 0)
                {
                    Console.Error.WriteLine("[LOADER][WARN] Yotei render resolution: the 01.512 code was not found; nothing is written.");
                }

                return;
            }

            _state = 1;
            _original = *field;
            Console.Error.WriteLine($"[LOADER][INFO] Yotei render resolution: the game's override int holds {_original}.");
        }

        var index = *(int*)CurrentIndexAddress;
        var width = *(int*)RenderWidthAddress;
        var height = *(int*)RenderHeightAddress;
        if ((index != _reportedIndex || width != _reportedWidth || height != _reportedHeight) && _reports < MaximumReports)
        {
            _reports++;
            _reportedIndex = index;
            _reportedWidth = width;
            _reportedHeight = height;
            Console.Error.WriteLine($"[LOADER][INFO] Yotei render resolution: the game renders at {width}x{height} (index {index}).");
        }

        if (HeldIndex < 0 && ToggledIndex < 0)
        {
            return;
        }

        var held = HeldIndex >= 0 ? HeldIndex : _original;
        var wanted = ToggledIndex >= 0 && SharpEmu.HLE.Diagnostics.AbToggle.On ? ToggledIndex : held;
        var current = *field;
        if (current == wanted)
        {
            return;
        }

        if (_written != int.MinValue && current != _written && _foreignWrites++ < 8)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Yotei render resolution: the game wrote {current} over {_written}.");
        }

        *field = wanted;
        if (_written != wanted)
        {
            Console.Error.WriteLine($"[LOADER][INFO] Yotei render resolution: override {current} -> {wanted}.");
        }

        _written = wanted;
    }

    private static int ReadIndex(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
        index is >= 0 and <= MaximumIndex
            ? index
            : -1;
}
