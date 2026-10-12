// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

/// <summary>
/// A small topmost window announcing an earned trophy over the running game. It never takes
/// focus, so the game keeps its input.
/// </summary>
internal sealed class TrophyToast : Window
{
    private static readonly Queue<(TrophyUnlockNotice Notice, byte[]? Icon)> Pending = new();
    private static bool _showing;

    public static void Show(TrophyUnlockNotice notice, byte[]? iconPng)
    {
        Pending.Enqueue((notice, iconPng));
        ShowNext();
    }

    private static void ShowNext()
    {
        if (_showing || !Pending.TryDequeue(out var next))
        {
            return;
        }

        _showing = true;
        var toast = new TrophyToast(next.Notice, next.Icon);
        toast.Closed += (_, _) =>
        {
            _showing = false;
            ShowNext();
        };
        toast.Show();
        toast.Place();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            toast.Close();
        };
        timer.Start();
    }

    private TrophyToast(TrophyUnlockNotice notice, byte[]? iconPng)
    {
        Width = 380;
        Height = 88;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var resources = Application.Current!;
        var gradeBrush = TrophyUi.GradeBrush(notice.Grade);
        var icon = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = (IBrush)resources.FindResource("ElevatedBrush")!,
            Child = new Image { Source = TrophyUi.LoadIcon(iconPng), Stretch = Stretch.UniformToFill },
        };
        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = $"{Localization.Instance.Get("Trophies.Unlocked")} · {TrophyUi.GradeName(notice.Grade)}",
                    Foreground = gradeBrush,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = notice.Name,
                    Foreground = (IBrush)resources.FindResource("TextBrush")!,
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        Content = new Border
        {
            Background = (IBrush)resources.FindResource("CardBrush")!,
            BorderBrush = gradeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = grid,
        };
    }

    private void Place()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var x = area.X + (int)((area.Width - Width * scale) / 2);
        var y = area.Bottom - (int)((Height + 48) * scale);
        Position = new PixelPoint(x, y);
    }
}
