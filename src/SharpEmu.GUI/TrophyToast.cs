// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SharpEmu.GUI;

/// <summary>What a trophy notification shows.</summary>
internal sealed record TrophyToastContent(string Name, string Description, int Grade, byte[]? IconPng);

/// <summary>
/// A topmost window announcing an earned trophy over the running game. It never takes focus, so the
/// game keeps its input. Toasts queue instead of stacking.
/// </summary>
internal sealed class TrophyToast : Window
{
    private static readonly Queue<TrophyToastContent> Pending = new();
    private static bool _showing;

    private const double ToastWidth = 560;
    private const double ToastHeight = 132;

    public static void Show(TrophyToastContent content)
    {
        Pending.Enqueue(content);
        ShowNext();
    }

    private static void ShowNext()
    {
        if (_showing || !Pending.TryDequeue(out var next))
        {
            return;
        }

        _showing = true;
        var toast = new TrophyToast(next);
        toast.Closed += (_, _) =>
        {
            _showing = false;
            ShowNext();
        };
        toast.Opacity = 0;
        toast.Show();
        toast.Place();
        // Fade in, hold, fade out, then close.
        Dispatcher.UIThread.Post(() => toast.Opacity = 1, DispatcherPriority.Background);
        var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            toast.Opacity = 0;
            var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            close.Tick += (_, _) =>
            {
                close.Stop();
                toast.Close();
            };
            close.Start();
        };
        hold.Start();
    }

    private TrophyToast(TrophyToastContent content)
    {
        Width = ToastWidth;
        Height = ToastHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) }];

        var resources = Application.Current!;
        var gradeColor = ((SolidColorBrush)TrophyUi.GradeBrush(content.Grade)).Color;
        var gradeBrush = new SolidColorBrush(gradeColor);

        var icon = new Border
        {
            Width = 92,
            Height = 92,
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Background = (IBrush)resources.FindResource("ElevatedBrush")!,
            BorderBrush = gradeBrush,
            BorderThickness = new Thickness(2),
            Child = new Image { Source = TrophyUi.LoadIcon(content.IconPng), Stretch = Stretch.UniformToFill },
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Ellipse { Width = 10, Height = 10, Fill = gradeBrush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = $"{Localization.Instance.Get("Trophies.Unlocked").ToUpperInvariant()}  ·  {TrophyUi.GradeName(content.Grade).ToUpperInvariant()}",
                    Foreground = gradeBrush,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    LetterSpacing = 1.2,
                },
            },
        };
        var name = new TextBlock
        {
            Text = content.Name,
            Foreground = (IBrush)resources.FindResource("TextBrush")!,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var description = new TextBlock
        {
            Text = content.Description,
            Foreground = (IBrush)resources.FindResource("SecondaryTextBrush")!,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { header, name, description },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20 };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // The margin leaves room for the glow so the window edge never clips it.
        Content = new Border
        {
            Margin = new Thickness(14),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#26204A"), 0),
                    new GradientStop(Color.Parse("#171717"), 0.7),
                },
            },
            BorderBrush = new SolidColorBrush(gradeColor, 0.75),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 28,
                Color = Color.FromArgb(150, gradeColor.R, gradeColor.G, gradeColor.B),
            }),
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
        var y = area.Bottom - (int)((Height + 40) * scale);
        Position = new PixelPoint(x, y);
    }
}
