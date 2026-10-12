// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SharpEmu.GUI;

/// <summary>Colors, names and icons shared by the trophy list and the unlock notification.</summary>
internal static class TrophyUi
{
    public static IBrush GradeBrush(int grade) => new SolidColorBrush(grade switch
    {
        1 => Color.Parse("#8FD8F2"),
        2 => Color.Parse("#FFC857"),
        3 => Color.Parse("#C0C5CF"),
        _ => Color.Parse("#D9894B"),
    });

    public static string GradeName(int grade) =>
        Localization.Instance.Get($"Trophies.Grade.{Math.Clamp(grade, 1, 4)}");

    public static Bitmap? LoadIcon(byte[]? png)
    {
        if (png is null || png.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
