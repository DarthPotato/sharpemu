// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.Diagnostics;

// Ghost of Yotei 01.512 and 02.002. The game keeps the row of its own scalability ladder (level of detail scale, number of
// sun shadow views, small shadow caster culling, ...) in one int that its code only reads; the static initializer
// leaves it at 0, the full-quality row. Writing a higher row makes the game itself send fewer draws: row 15 sends
// about 45% fewer draws and dispatches per frame than row 0 (+39% frames per second in an in-run A/B).
// SHARPEMU_YOTEI_LADDER=<0..21>: hold that row for the whole run. Unset means row 15, the last row that only lowers
// detail; 0 restores full quality. Rows 16..21 also stop drawing the player character's body.
// SHARPEMU_AB_YOTEI_LADDER=<1..21> (measurement only): that row while the in-run A/B toggle is on, the held row
// while it is off.
// Nothing is written unless an initializer's bytes are found at the matching address, so another title or build is
// left alone. No game code or file is changed.
public static unsafe class YoteiLadder
{
    private readonly record struct Version(string Name, ulong InitializerAddress, ulong LevelAddress, byte[] Initializer);

    private static readonly Version[] Versions =
    [
        new("01.512", 0x8_00D6_4E1B, 0x8_04FE_5B70, [0x48, 0xC7, 0x05, 0x4A, 0x0D, 0x28, 0x04, 0x00, 0x00, 0x00, 0x00]),
        new("02.002", 0x8_00DE_6E76, 0x8_0509_E1B0, [0x48, 0xC7, 0x05, 0x2F, 0x73, 0x2B, 0x04, 0x00, 0x00, 0x00, 0x00]),
    ];
    private const int MaximumLevel = 21;

    private const int DefaultLevel = 15;

    private static readonly bool HeldLevelSet = Environment.GetEnvironmentVariable("SHARPEMU_YOTEI_LADDER") is not null;
    private static readonly int HeldLevel = ReadLevel("SHARPEMU_YOTEI_LADDER", DefaultLevel);
    private static readonly int ToggledLevel = ReadLevel("SHARPEMU_AB_YOTEI_LADDER", 0);

    public static readonly bool Enabled = HeldLevel > 0 || ToggledLevel > 0;

    private static int _state;
    private static Version _version;
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
            foreach (var version in Versions)
            {
                Span<byte> found = new byte[version.Initializer.Length];
                if (host.TryReadGuestOperand(version.InitializerAddress, found) && found.SequenceEqual(version.Initializer))
                {
                    _version = version;
                    _state = 1;
                    Console.Error.WriteLine($"[LOADER][INFO] Yotei ladder: {version.Name} code found, holding row {HeldLevel}.");
                    break;
                }
            }

            if (_state == 0)
            {
                _state = -1;
                if (HeldLevelSet || ToggledLevel > 0)
                {
                    Console.Error.WriteLine("[LOADER][WARN] Yotei ladder: no 01.512 or 02.002 initializer was found; nothing is written.");
                }

                return;
            }
        }

        var wanted = ToggledLevel > 0 && SharpEmu.HLE.Diagnostics.AbToggle.On ? ToggledLevel : HeldLevel;
        var level = (int*)_version.LevelAddress;
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

    private static int ReadLevel(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) &&
        level is >= 0 and <= MaximumLevel
            ? level
            : fallback;
}
