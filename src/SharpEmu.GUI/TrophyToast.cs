// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

/// <summary>What a trophy notification shows.</summary>
internal sealed record TrophyToastContent(string Name, string Description, int Grade, byte[]? IconPng);

/// <summary>
/// A topmost, click-through window announcing an earned trophy over the running game. It never
/// takes focus or input, so the game keeps both. Toasts queue instead of stacking.
/// </summary>
/// <remarks>
/// The motion is tiered like the console's: every trophy slides in on a spring, flips its icon in
/// 3D and gets one light sweep; rarer grades add more sparks, and the platinum adds rotating rays
/// and a longer stay. With Windows animations turned off the toast just fades in and out.
/// </remarks>
internal sealed class TrophyToast : Window
{
    private const double WindowWidth = 680;
    private const double WindowHeight = 320;
    private const double CardWidth = 560;
    private const double CardHeight = 132;
    private const double CardBottomMargin = 26;
    private const double IconSize = 92;
    private const double EnterSeconds = 0.55;
    private const double ExitSeconds = 0.45;

    private static readonly Queue<TrophyToastContent> Pending = new();
    private static bool _showing;

    private sealed class Particle
    {
        public Border Visual = null!;
        public double X, Y, Vx, Vy, Life, Age, Spin, Rotation, Size;
    }

    private readonly TrophyToastContent _content;
    private readonly bool _reducedMotion = ReducedMotion();
    private readonly double _holdSeconds;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Random _random = new();
    private readonly List<Particle> _particles = [];
    private readonly Canvas _sparks = new() { IsHitTestVisible = false };
    private readonly Grid _cardHost = new();
    private readonly TranslateTransform _cardShift = new();
    private readonly Border _glow = new();
    private readonly Border _icon = new();
    private readonly Rotate3DTransform _iconFlip = new();
    private readonly ScaleTransform _iconScale = new();
    private readonly Border _shine = new();
    private readonly TranslateTransform _shineShift = new();
    private readonly Border _timeBar = new();
    private readonly Canvas _rays = new() { IsHitTestVisible = false };
    private readonly RotateTransform _raysSpin = new();
    private bool _sparksSpawned;
    private double _lastFrame;

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
        toast.MakeClickThrough();
        toast.Start();
    }

    private TrophyToast(TrophyToastContent content)
    {
        _content = content;
        _holdSeconds = content.Grade switch { 1 => 8.0, 2 => 6.5, 3 => 5.5, _ => 5.0 };

        Width = WindowWidth;
        Height = WindowHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        Content = BuildContent();
        _frame.Tick += (_, _) => Tick();
    }

    private Control BuildContent()
    {
        var resources = Application.Current!;
        var gradeColor = ((SolidColorBrush)TrophyUi.GradeBrush(_content.Grade)).Color;
        var gradeBrush = new SolidColorBrush(gradeColor);

        // Soft light behind the card in the grade color; its opacity breathes while the toast stays.
        _glow.Width = CardWidth;
        _glow.Height = CardHeight;
        _glow.CornerRadius = new CornerRadius(18);
        _glow.Background = Brushes.Transparent;
        _glow.BoxShadow = new BoxShadows(new BoxShadow
        {
            Blur = _content.Grade == 1 ? 60 : 40,
            Spread = _content.Grade == 1 ? 6 : 2,
            Color = Color.FromArgb(_content.Grade == 1 ? (byte)170 : (byte)120, gradeColor.R, gradeColor.G, gradeColor.B),
        });

        // Platinum only: slow rotating rays behind the icon.
        if (_content.Grade == 1 && !_reducedMotion)
        {
            BuildRays(gradeColor);
        }

        _icon.Width = IconSize;
        _icon.Height = IconSize;
        _icon.CornerRadius = new CornerRadius(16);
        _icon.ClipToBounds = true;
        _icon.Background = (IBrush)resources.FindResource("ElevatedBrush")!;
        _icon.BorderBrush = gradeBrush;
        _icon.BorderThickness = new Thickness(2);
        _icon.Child = new Image { Source = TrophyUi.LoadIcon(_content.IconPng), Stretch = Stretch.UniformToFill };
        _icon.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _iconFlip.Depth = 520;
        _icon.RenderTransform = new TransformGroup { Children = { _iconFlip, _iconScale } };

        var iconHost = new Grid { Width = IconSize, Height = IconSize, VerticalAlignment = VerticalAlignment.Center };
        if (_content.Grade == 1 && !_reducedMotion)
        {
            iconHost.Children.Add(_rays);
        }

        iconHost.Children.Add(_icon);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                TrophyUi.Cup(_content.Grade, 16),
                new TextBlock
                {
                    Text = $"{Localization.Instance.Get("Trophies.Unlocked").ToUpperInvariant()}  ·  {TrophyUi.GradeName(_content.Grade).ToUpperInvariant()}",
                    Foreground = gradeBrush,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    LetterSpacing = 1.2,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        var name = new TextBlock
        {
            Text = _content.Name,
            Foreground = (IBrush)resources.FindResource("TextBrush")!,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var description = new TextBlock
        {
            Text = _content.Description,
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
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20, Margin = new Thickness(18) };
        row.Children.Add(iconHost);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // One diagonal band of light that crosses the card once the icon has landed.
        _shine.Width = 110;
        _shine.Height = CardHeight * 2;
        _shine.HorizontalAlignment = HorizontalAlignment.Left;
        _shine.VerticalAlignment = VerticalAlignment.Center;
        _shine.IsHitTestVisible = false;
        _shine.Opacity = 0;
        _shine.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        _shine.RenderTransform = new TransformGroup { Children = { new RotateTransform(18), _shineShift } };

        // Time left before the toast leaves, as a thin line along the bottom edge.
        _timeBar.Height = 3;
        _timeBar.HorizontalAlignment = HorizontalAlignment.Left;
        _timeBar.VerticalAlignment = VerticalAlignment.Bottom;
        _timeBar.Background = new SolidColorBrush(gradeColor, 0.85);
        _timeBar.Width = CardWidth;

        var cardContent = new Grid();
        cardContent.Children.Add(row);
        cardContent.Children.Add(_shine);
        cardContent.Children.Add(_timeBar);

        var card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            ClipToBounds = true,
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
            BorderBrush = new SolidColorBrush(gradeColor, 0.8),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(18),
            Child = cardContent,
        };

        _cardHost.Width = CardWidth;
        _cardHost.Height = CardHeight;
        _cardHost.HorizontalAlignment = HorizontalAlignment.Center;
        _cardHost.VerticalAlignment = VerticalAlignment.Bottom;
        _cardHost.Margin = new Thickness(0, 0, 0, CardBottomMargin);
        _cardHost.RenderTransform = _cardShift;
        _cardHost.Children.Add(_glow);
        _cardHost.Children.Add(card);

        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(_sparks);
        root.Children.Add(_cardHost);
        return root;
    }

    private void BuildRays(Color gradeColor)
    {
        const double length = 150;
        _rays.Width = IconSize;
        _rays.Height = IconSize;
        _rays.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _rays.RenderTransform = _raysSpin;
        for (var i = 0; i < 12; i++)
        {
            var ray = new Border
            {
                Width = 6,
                Height = length,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, gradeColor.R, gradeColor.G, gradeColor.B), 0),
                        new GradientStop(Color.FromArgb(110, gradeColor.R, gradeColor.G, gradeColor.B), 1),
                    },
                },
                RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                RenderTransform = new RotateTransform(i * 30),
            };
            Canvas.SetLeft(ray, IconSize / 2 - 3);
            Canvas.SetTop(ray, IconSize / 2 - length);
            _rays.Children.Add(ray);
        }
    }

    private void Start()
    {
        _clock.Start();
        _frame.Start();
        Tick();
    }

    private void Tick()
    {
        var t = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.05, t - _lastFrame);
        _lastFrame = t;
        var total = EnterSeconds + _holdSeconds + ExitSeconds;
        if (t >= total)
        {
            _frame.Stop();
            Close();
            return;
        }

        var leaving = t > EnterSeconds + _holdSeconds;
        var leave = leaving ? (t - EnterSeconds - _holdSeconds) / ExitSeconds : 0;

        if (_reducedMotion)
        {
            Opacity = leaving ? 1 - leave : Math.Min(1, t / 0.25);
            _iconFlip.AngleY = 0;
            _iconScale.ScaleX = _iconScale.ScaleY = 1;
            _timeBar.Width = CardWidth * Math.Clamp(1 - (t - EnterSeconds) / _holdSeconds, 0, 1);
            return;
        }

        // The card slides up on a spring; leaving, it drops away and fades.
        var enter = Math.Clamp(t / EnterSeconds, 0, 1);
        var slide = leaving ? EaseInCubic(leave) * 50 : (1 - EaseOutBack(enter)) * 90;
        _cardShift.Y = slide;
        Opacity = leaving ? 1 - EaseInCubic(leave) : Math.Min(1, enter * 2.2);

        // The icon waits for the card, then flips in around its vertical axis with a little overshoot.
        var flip = Math.Clamp((t - 0.30) / 0.60, 0, 1);
        var flipEase = EaseOutBack(flip);
        _iconFlip.AngleY = (1 - flipEase) * 100;
        var pop = 0.55 + 0.45 * flipEase;
        _iconScale.ScaleX = _iconScale.ScaleY = pop;
        _icon.Opacity = Math.Min(1, flip * 3);

        // One sweep of light once the icon has landed.
        var sweep = (t - 0.85) / 0.80;
        if (sweep is >= 0 and <= 1)
        {
            _shine.Opacity = 1;
            _shineShift.X = -160 + EaseOutCubic(sweep) * (CardWidth + 260);
        }
        else
        {
            _shine.Opacity = 0;
        }

        // The glow breathes; the platinum's rays turn slowly.
        _glow.Opacity = (0.65 + 0.35 * Math.Sin(t * 2.6)) * (leaving ? 1 - leave : Math.Min(1, enter * 1.5));
        if (_content.Grade == 1)
        {
            _raysSpin.Angle = t * 14;
        }

        if (!_sparksSpawned && t >= 0.75)
        {
            _sparksSpawned = true;
            SpawnSparks();
        }

        StepSparks(dt);
        _timeBar.Width = CardWidth * Math.Clamp(1 - (t - EnterSeconds) / _holdSeconds, 0, 1);
    }

    private void SpawnSparks()
    {
        var gradeColor = ((SolidColorBrush)TrophyUi.GradeBrush(_content.Grade)).Color;
        var count = _content.Grade switch { 1 => 46, 2 => 28, 3 => 18, _ => 12 };
        // The burst starts at the icon's centre.
        var originX = (WindowWidth - CardWidth) / 2 + 18 + IconSize / 2;
        var originY = WindowHeight - CardBottomMargin - CardHeight / 2;
        for (var i = 0; i < count; i++)
        {
            var angle = _content.Grade == 1
                ? _random.NextDouble() * Math.PI * 2
                : -Math.PI / 2 + (_random.NextDouble() - 0.5) * Math.PI * 1.3;
            var speed = 140 + _random.NextDouble() * 300;
            var size = 3 + _random.NextDouble() * 5;
            var useWhite = _random.NextDouble() < 0.35;
            var color = useWhite ? Colors.White : gradeColor;
            var visual = new Border
            {
                Width = size,
                Height = size,
                Background = new SolidColorBrush(color),
                CornerRadius = _random.NextDouble() < 0.5 ? new CornerRadius(size) : new CornerRadius(1),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            };
            var particle = new Particle
            {
                Visual = visual,
                X = originX,
                Y = originY,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Life = 0.9 + _random.NextDouble() * 0.9,
                Spin = (_random.NextDouble() - 0.5) * 540,
                Size = size,
            };
            _particles.Add(particle);
            _sparks.Children.Add(visual);
        }
    }

    private void StepSparks(double dt)
    {
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _sparks.Children.Remove(p.Visual);
                _particles.RemoveAt(i);
                continue;
            }

            p.Vy += 520 * dt;
            p.Vx *= 1 - 0.8 * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Rotation += p.Spin * dt;
            Canvas.SetLeft(p.Visual, p.X - p.Size / 2);
            Canvas.SetTop(p.Visual, p.Y - p.Size / 2);
            p.Visual.Opacity = 1 - EaseInCubic(p.Age / p.Life);
            p.Visual.RenderTransform = new RotateTransform(p.Rotation);
        }
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseInCubic(double t) => t * t * t;

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
        var y = area.Bottom - (int)(Height * scale);
        Position = new PixelPoint(x, y);
    }

    // The whole window, transparent margins included, lets mouse input through to the game.
    private void MakeClickThrough()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        const int gwlExStyle = -20;
        const long wsExTransparent = 0x20;
        const long wsExNoActivate = 0x08000000;
        const long wsExToolWindow = 0x80;
        var style = GetWindowLongPtr(handle.Handle, gwlExStyle).ToInt64();
        SetWindowLongPtr(handle.Handle, gwlExStyle, new IntPtr(style | wsExTransparent | wsExNoActivate | wsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    // True when Windows animations are turned off (Settings > Accessibility > Visual effects).
    private static bool ReducedMotion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        const uint spiGetClientAreaAnimation = 0x1042;
        var enabled = 1;
        return SystemParametersInfo(spiGetClientAreaAnimation, 0, ref enabled, 0) && enabled == 0;
    }
}
