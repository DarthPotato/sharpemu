// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.Diagnostics;

// Ghost of Yotei 01.512 only, off unless set. The game keeps the row of its own scalability ladder (level of detail
// scale, number of sun shadow views, ...) in one int that its code only reads; the static initializer leaves it at 0,
// the full-quality row. Writing a higher row makes the game itself send fewer draws.
// SHARPEMU_YOTEI_LADDER=<1..15>: hold that row for the whole run.
// SHARPEMU_AB_YOTEI_LADDER=<1..15> (measurement only): that row while the in-run A/B toggle is on, the
// SHARPEMU_YOTEI_LADDER row (0 when unset) while it is off.
// Nothing is written unless the initializer's bytes are found at their 01.512 address, so another title or build is
// left alone. No game code or file is changed.
public static unsafe class YoteiLadder
{
    private const ulong InitializerAddress = 0x8_00D6_4E1B;
    private const ulong LevelAddress = 0x8_04FE_5B70;
    private const int MaximumLevel = 15;

    // mov qword ptr [app+0x4FE5B70], 0
    private static ReadOnlySpan<byte> Initializer => [0x48, 0xC7, 0x05, 0x4A, 0x0D, 0x28, 0x04, 0x00, 0x00, 0x00, 0x00];

    private static readonly int HeldLevel = ReadLevel("SHARPEMU_YOTEI_LADDER");
    private static readonly int ToggledLevel = ReadLevel("SHARPEMU_AB_YOTEI_LADDER");

    public static readonly bool Enabled = HeldLevel > 0 || ToggledLevel > 0;

    private static int _state;
    private static int _written = -1;
    private static int _foreignWrites;

    public static void OnFlip(ICommandStreamHost host)
    {
        if (_state < 0)
        {
            return;
        }

        if (_state == 0)
        {
            Span<byte> found = stackalloc byte[Initializer.Length];
            if (!host.TryReadGuestOperand(InitializerAddress, found) || !found.SequenceEqual(Initializer))
            {
                _state = -1;
                Console.Error.WriteLine("[LOADER][WARN] Yotei ladder: the 01.512 initializer was not found; nothing is written.");
                return;
            }

            _state = 1;
        }

        var wanted = ToggledLevel > 0 && SharpEmu.HLE.Diagnostics.AbToggle.On ? ToggledLevel : HeldLevel;
        var level = (int*)LevelAddress;
        var current = *level;
        if (current == wanted)
        {
            return;
        }

        if (_written >= 0 && current != _written && _foreignWrites++ < 8)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Yotei ladder: the game wrote row {current} over row {_written}.");
        }

        *level = wanted;
        if (_written != wanted)
        {
            Console.Error.WriteLine($"[LOADER][INFO] Yotei ladder: row {current} -> {wanted}.");
        }

        _written = wanted;
    }

    private static int ReadLevel(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) &&
        level is > 0 and <= MaximumLevel
            ? level
            : 0;
}
