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

    /// <summary>A trophy cup in the grade color: the console shows grades as cups, not as words.</summary>
    public static Avalonia.Controls.TextBlock Cup(int grade, double size) => new()
    {
        Classes = { "materialSymbol" },
        Text = "emoji_events",
        FontSize = size,
        LineHeight = size + 4,
        Foreground = GradeBrush(grade),
    };

    /// <summary>Fades the rows in one after another; used once, when a list first opens.</summary>
    public static void StaggerIn(IReadOnlyList<Avalonia.Controls.Control> rows)
    {
        foreach (var row in rows)
        {
            row.Opacity = 0;
            row.Transitions = [new Avalonia.Animation.DoubleTransition { Property = Avalonia.Controls.Control.OpacityProperty, Duration = TimeSpan.FromMilliseconds(320) }];
        }

        var next = 0;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(28) };
        timer.Tick += (_, _) =>
        {
            // Rows past the first screenful appear together; nobody waits on them.
            var batch = next < 10 ? 1 : rows.Count;
            for (var i = 0; i < batch && next < rows.Count; i++)
            {
                rows[next++].Opacity = 1;
            }

            if (next >= rows.Count)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }

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
            return Bitmap.DecodeToWidth(stream, 192);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
