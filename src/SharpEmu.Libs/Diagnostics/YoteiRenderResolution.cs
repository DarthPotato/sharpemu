// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
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
// SHARPEMU_YOTEI_RENDER_SIZE=<W>x<H> (off unless set): a size below 1920x1080, which the game's table does not have.
// The game reads its sizes through a pointer in the renderer object. With this set, that pointer is aimed at a copy
// of the table kept by the emulator in which index 15 is WxH and index 14 is 1920x1080 (the game's 2048x1152 goes),
// so SHARPEMU_AB_YOTEI_RENDER_INDEX=14 compares the size with 1920x1080 in one run. Both numbers must be multiples
// of 8, as all of the game's sizes are, from 640x360 to 1920x1080; the game's own steps continue as 1792x1008,
// 1664x936, 1536x864, 1408x792, 1280x720, 1152x648, 1024x576, 896x504.
// The game applies a new index within a frame or two; the log shows each change as a "renders at" line. Nothing is
// written unless the code that reads the int is found at its 01.512 address, so another title or build is left
// alone. No game code or file is changed.
public static unsafe class YoteiRenderResolution
{
    private const ulong ReaderAddress = 0x8_00D5_E33C;
    private const ulong RendererAddress = 0x8_04FD_B4A0;
    private const ulong GameTableAddress = 0x8_01A7_85E0;
    private const ulong RenderWidthAddress = RendererAddress + 0x327590;
    private const ulong RenderHeightAddress = RendererAddress + 0x327594;
    private const ulong TablePointerAddress = RendererAddress + 0x3277F8;
    private const ulong CurrentIndexAddress = RendererAddress + 0x327840;
    private const ulong OverrideAddress = RendererAddress + 0x327848;
    private const int TableEntries = 16;
    private const int MaximumIndex = 15;
    private const int DefaultIndex = 15;
    private const int MaximumReports = 64;
    private const int MaximumSteps = 4;

    // mov ecx,[rbx+327848h]; lea rdx,[app+0x1A50024]; mov r12,[rbp-200h]; test ecx,ecx; cmovns eax,ecx
    private static ReadOnlySpan<byte> Reader =>
    [
        0x8B, 0x8B, 0x48, 0x78, 0x32, 0x00, 0x48, 0x8D, 0x15, 0xDB, 0x1C, 0xCF, 0x00,
        0x4C, 0x8B, 0xA5, 0x00, 0xFE, 0xFF, 0xFF, 0x85, 0xC9, 0x0F, 0x49, 0xC1,
    ];

    private static readonly string? HeldText = Environment.GetEnvironmentVariable("SHARPEMU_YOTEI_RENDER_INDEX");
    private static readonly int HeldIndex = HeldText is null ? DefaultIndex : ReadIndex("SHARPEMU_YOTEI_RENDER_INDEX");
    private static readonly int ToggledIndex = ReadIndex("SHARPEMU_AB_YOTEI_RENDER_INDEX");
    private static readonly string? SizeText = Environment.GetEnvironmentVariable("SHARPEMU_YOTEI_RENDER_SIZE");
    private static readonly long CustomSize = ReadSize(SizeText);

    private static int _state;
    private static int _original;
    private static int _written = int.MinValue;
    private static int _foreignWrites;
    private static int _reports;
    private static int _reportedIndex = int.MinValue;
    private static int _reportedWidth;
    private static int _reportedHeight;
    private static long* _table;
    private static int _steps;

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
                if (HeldText is not null || ToggledIndex >= 0 || SizeText is not null)
                {
                    Console.Error.WriteLine("[LOADER][WARN] Yotei render resolution: the 01.512 code was not found; nothing is written.");
                }

                return;
            }

            _state = 1;
            _original = *field;
            Console.Error.WriteLine($"[LOADER][INFO] Yotei render resolution: the game's override int holds {_original}.");
            if (CustomSize != 0)
            {
                UseOwnTable();
            }
            else if (SizeText is not null)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Yotei render resolution: SHARPEMU_YOTEI_RENDER_SIZE={SizeText} is not <W>x<H> in multiples of 8 from 640x360 to 1920x1080; ignored.");
            }
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

        // The game reads a size only when its index changes. An index that was already current when the table
        // changed keeps its old size until the index has been somewhere else, so it takes one step away and back.
        var step = _table is not null && wanted is >= 0 and <= MaximumIndex && index == wanted &&
            Pack(width, height) != _table[wanted] && _steps < MaximumSteps;
        if (step)
        {
            wanted = wanted == 0 ? 1 : wanted - 1;
        }

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
        if (step)
        {
            _steps++;
            Console.Error.WriteLine($"[LOADER][INFO] Yotei render resolution: index {index} still has {width}x{height}; one step to index {wanted} and back.");
        }
        else if (_written != wanted)
        {
            Console.Error.WriteLine($"[LOADER][INFO] Yotei render resolution: override {current} -> {wanted}.");
        }

        _written = wanted;
    }

    // Aims the renderer's table pointer at a copy whose last entry is the custom size. The copy is never freed: the
    // game reads through the pointer for as long as it runs.
    private static void UseOwnTable()
    {
        var pointer = (ulong*)TablePointerAddress;
        var game = (long*)GameTableAddress;
        if (*pointer != GameTableAddress || game[0] != Pack(3840, 2160) || game[MaximumIndex] != Pack(1920, 1080))
        {
            Console.Error.WriteLine("[LOADER][WARN] Yotei render resolution: the game's size table is not where 01.512 has it; SHARPEMU_YOTEI_RENDER_SIZE is ignored.");
            return;
        }

        var table = (long*)NativeMemory.Alloc((nuint)TableEntries, (nuint)sizeof(long));
        for (var entry = 0; entry < TableEntries; entry++)
        {
            table[entry] = game[entry];
        }

        table[MaximumIndex - 1] = game[MaximumIndex];
        table[MaximumIndex] = CustomSize;
        _table = table;
        Volatile.Write(ref *pointer, (ulong)table);
        Console.Error.WriteLine($"[LOADER][INFO] Yotei render resolution: own size table in use; index {MaximumIndex} is {CustomSize & 0xFFFFFFFF}x{CustomSize >> 32}, index {MaximumIndex - 1} is 1920x1080.");
    }

    // A table entry: width in the low half, height in the high half.
    private static long Pack(int width, int height) => (uint)width | ((long)height << 32);

    private static int ReadIndex(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
        index is >= 0 and <= MaximumIndex
            ? index
            : -1;

    private static long ReadSize(string? text)
    {
        var parts = text?.Split('x', 'X');
        return parts is { Length: 2 } &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) &&
            width is >= 640 and <= 1920 && height is >= 360 and <= 1080 && width % 8 == 0 && height % 8 == 0
                ? Pack(width, height)
                : 0;
    }
}
