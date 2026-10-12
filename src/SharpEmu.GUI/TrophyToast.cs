// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SharpEmu.GUI.Controls;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

/// <summary>What a trophy notification shows. Earned counts the new trophy.</summary>
internal sealed record TrophyToastContent(
    string Name,
    string Description,
    int Grade,
    byte[]? IconPng,
    string? GameName = null,
    int Earned = 0,
    int Total = 0);

/// <summary>
/// A topmost, click-through window announcing an earned trophy over the running game. It never
/// takes focus or input, so the game keeps both. Toasts queue instead of stacking.
/// </summary>
/// <remarks>
/// The card swings in from the right in 3D. Its medal spins like a tossed coin while the sound
/// rises, and lands on the sound's impact: a ring leaves it, confetti bursts out, a glint crosses
/// it and the game's progress bar moves up. The card is tinted in the grade's metal, with light
/// drifting across it; rarer grades get more confetti, and the platinum adds rays, an iridescent
/// edge and a second burst. With Windows animations turned off the toast only fades.
/// </remarks>
internal sealed class TrophyToast : Window
{
    private const double WindowWidth = 780;
    private const double WindowHeight = 360;
    private const double CardWidth = 600;
    private const double CardHeight = 158;
    private const double CardMargin = 28;
    private const double MedalSize = 104;
    private const double MedalColumn = 140;
    private const double EnterSeconds = 0.6;
    private const double ExitSeconds = 0.5;
    private const double SpinStart = 0.15;
    private const double Impact = SpinStart + TrophySound.ImpactDelaySeconds;

    private static readonly Queue<TrophyToastContent> Pending = new();
    private static bool _showing;

    private sealed class Confetti
    {
        public Border Visual = null!;
        public RotateTransform Rotation = null!;
        public ScaleTransform Tumble = null!;
        public double X, Y, Vx, Vy, Life, Age, Spin, TumbleSpeed, Phase, Width, Height;
    }

    private readonly TrophyToastContent _content;
    private readonly Color _light, _mid, _dark;
    private readonly bool _reducedMotion = ReducedMotion();
    private readonly double _leaveAt;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Random _random = new();
    private readonly List<Confetti> _confetti = [];
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Grid _cardHost = new();
    private readonly Rotate3DTransform _cardTilt = new() { Depth = 900 };
    private readonly TranslateTransform _cardShift = new();
    private readonly ScaleTransform _cardScale = new();
    private readonly Border _glow = new();
    private readonly TrophyMedal _medal = new() { Width = MedalSize, Height = MedalSize };
    private readonly ScaleTransform _medalScale = new();
    private readonly Border _burst = new();
    private readonly Canvas _rays = new() { IsHitTestVisible = false };
    private readonly RotateTransform _raysSpin = new();
    private readonly Border _ring = new();
    private readonly ScaleTransform _ringScale = new();
    private readonly Border _shine = new();
    private readonly TranslateTransform _shineShift = new();
    private readonly TranslateTransform _auroraA = new();
    private readonly TranslateTransform _auroraB = new();
    private readonly RotateTransform _edgeSpin = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBlock _progressText = new();
    private readonly List<(Control Line, TranslateTransform Shift, double Delay)> _lines = [];
    private bool _chimed;
    private bool _burstDone;
    private bool _secondBurstDone;
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
        (_light, _mid, _dark) = TrophyMedal.Palette(content.Grade);
        _leaveAt = Impact + content.Grade switch { 1 => 7.5, 2 => 5.5, 3 => 4.8, _ => 4.3 };

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

    private static double CardLeft => WindowWidth - CardMargin - CardWidth;

    private static Point MedalCenter => new(CardLeft + MedalColumn / 2, CardMargin + CardHeight / 2);

    private Control BuildContent()
    {
        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(BuildCard());
        root.Children.Add(_overlay);

        // The ring that leaves the medal as it lands.
        _ring.Width = MedalSize;
        _ring.Height = MedalSize;
        _ring.CornerRadius = new CornerRadius(MedalSize / 2);
        _ring.BorderBrush = new SolidColorBrush(_light);
        _ring.BorderThickness = new Thickness(3);
        _ring.Opacity = 0;
        _ring.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _ring.RenderTransform = _ringScale;
        _ring.BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(200, _mid.R, _mid.G, _mid.B) });
        Canvas.SetLeft(_ring, MedalCenter.X - MedalSize / 2);
        Canvas.SetTop(_ring, MedalCenter.Y - MedalSize / 2);
        _overlay.Children.Add(_ring);
        return root;
    }

    private Control BuildCard()
    {
        var (deep, deeper) = _content.Grade switch
        {
            1 => (Color.Parse("#1F4466"), Color.Parse("#2B1A52")),
            2 => (Color.Parse("#5A3F0C"), Color.Parse("#2A1838")),
            3 => (Color.Parse("#39424F"), Color.Parse("#1B2030")),
            _ => (Color.Parse("#5A2E17"), Color.Parse("#26172E")),
        };

        // Light in the grade's color, drifting slowly behind everything on the card.
        var aurora = new Canvas { IsHitTestVisible = false };
        aurora.Children.Add(Blob(_mid, 150, 360, -60, -170, _auroraA));
        aurora.Children.Add(Blob(Color.Parse("#8C6CFF"), 110, 320, 330, -60, _auroraB));

        // Glass: the top of the card catches a little more light.
        var glass = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(34, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55),
                },
            },
        };

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions($"{MedalColumn},*") };
        content.Children.Add(BuildMedalArea());
        var text = BuildText();
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        // One diagonal sweep of light after the medal lands.
        _shine.Width = 140;
        _shine.Height = CardHeight * 2.2;
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
                new GradientStop(Color.FromArgb(60, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        _shine.RenderTransform = new TransformGroup { Children = { new RotateTransform(20), _shineShift } };

        var layers = new Grid();
        layers.Children.Add(aurora);
        layers.Children.Add(glass);
        layers.Children.Add(content);
        layers.Children.Add(_shine);

        var card = new Border
        {
            CornerRadius = new CornerRadius(24),
            ClipToBounds = true,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(245, deep.R, deep.G, deep.B), 0),
                    new GradientStop(Color.FromArgb(245, deeper.R, deeper.G, deeper.B), 1),
                },
            },
            Child = layers,
        };

        // A bright edge: metal along the top left fading round the card; the platinum's turns.
        var edgeBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            Transform = _edgeSpin,
            TransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        };
        if (_content.Grade == 1)
        {
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#E08FE3FF"), 0));
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#C09B7CFF"), 0.35));
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#B0FF8FD8"), 0.65));
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#E08FE3FF"), 1));
        }
        else
        {
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(220, _light.R, _light.G, _light.B), 0));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(90, _mid.R, _mid.G, _mid.B), 0.45));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(30, 255, 255, 255), 1));
        }

        var edge = new Border
        {
            CornerRadius = new CornerRadius(24),
            BorderBrush = edgeBrush,
            BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
        };

        // Shadow below the card, and a halo in the grade's color that breathes.
        _glow.CornerRadius = new CornerRadius(24);
        _glow.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        _glow.BoxShadow = new BoxShadows(
            new BoxShadow { OffsetY = 16, Blur = 36, Color = Color.FromArgb(150, 0, 0, 0) },
            [new BoxShadow { Blur = _content.Grade == 1 ? 56 : 38, Spread = 2, Color = Color.FromArgb(_content.Grade == 1 ? (byte)170 : (byte)120, _mid.R, _mid.G, _mid.B) }]);

        _cardHost.Width = CardWidth;
        _cardHost.Height = CardHeight;
        _cardHost.HorizontalAlignment = HorizontalAlignment.Right;
        _cardHost.VerticalAlignment = VerticalAlignment.Top;
        _cardHost.Margin = new Thickness(0, CardMargin, CardMargin, 0);
        _cardHost.RenderTransformOrigin = new RelativePoint(1, 0.5, RelativeUnit.Relative);
        _cardHost.RenderTransform = new TransformGroup { Children = { _cardScale, _cardTilt, _cardShift } };
        _cardHost.Children.Add(_glow);
        _cardHost.Children.Add(card);
        _cardHost.Children.Add(edge);
        return _cardHost;
    }

    private Control BuildMedalArea()
    {
        var area = new Grid { Width = MedalColumn, Height = CardHeight };

        // A soft light behind the medal that flares when it lands.
        _burst.Width = MedalSize * 1.7;
        _burst.Height = MedalSize * 1.7;
        _burst.CornerRadius = new CornerRadius(MedalSize);
        _burst.HorizontalAlignment = HorizontalAlignment.Center;
        _burst.VerticalAlignment = VerticalAlignment.Center;
        _burst.Background = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(200, _light.R, _light.G, _light.B), 0),
                new GradientStop(Color.FromArgb(70, _mid.R, _mid.G, _mid.B), 0.45),
                new GradientStop(Color.FromArgb(0, _mid.R, _mid.G, _mid.B), 1),
            },
        };
        _burst.Opacity = 0.35;
        area.Children.Add(_burst);

        // Gold and platinum shine behind the medal with turning rays.
        if (_content.Grade <= 2 && !_reducedMotion)
        {
            BuildRays();
            area.Children.Add(_rays);
        }

        _medal.Grade = _content.Grade;
        _medal.HorizontalAlignment = HorizontalAlignment.Center;
        _medal.VerticalAlignment = VerticalAlignment.Center;
        _medal.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _medal.RenderTransform = _medalScale;
        area.Children.Add(_medal);
        return area;
    }

    private Control BuildText()
    {
        var label = new TextBlock
        {
            Text = Localization.Instance.Get("Trophies.Unlocked").ToUpperInvariant(),
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            FontSize = 11.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 2, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(_light, 0), new GradientStop(_mid, 1) },
            },
            Child = new TextBlock
            {
                Text = TrophyUi.GradeName(_content.Grade).ToUpperInvariant(),
                Foreground = new SolidColorBrush(TrophyMedal.Shade(_dark, 0.55)),
                FontSize = 10.5,
                FontWeight = FontWeight.Black,
                LetterSpacing = 1.2,
            },
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { label, pill } };
        var name = new TextBlock
        {
            Text = _content.Name,
            Foreground = Brushes.White,
            FontSize = 21,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var description = new TextBlock
        {
            Text = _content.Description,
            Foreground = new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stack = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 16, 24, 14),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { header, name, description },
        };
        if (_content.Total > 0)
        {
            stack.Children.Add(BuildProgress());
        }

        for (var i = 0; i < stack.Children.Count; i++)
        {
            var shift = new TranslateTransform();
            stack.Children[i].RenderTransform = shift;
            _lines.Add((stack.Children[i], shift, 0.32 + i * 0.08));
        }

        return stack;
    }

    // The trophy's icon, the game's name and a completion bar that moves up when the medal lands.
    private Control BuildProgress()
    {
        var icon = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = new Image { Source = TrophyUi.LoadIcon(_content.IconPng), Stretch = Stretch.UniformToFill },
        };
        var game = new TextBlock
        {
            Text = _content.GameName ?? string.Empty,
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _progressText.Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        _progressText.FontSize = 11.5;
        _progressText.FontWeight = FontWeight.SemiBold;
        _progressText.Text = $"{Math.Max(0, _content.Earned - 1)} / {_content.Total}";
        var labels = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        labels.Children.Add(game);
        Grid.SetColumn(_progressText, 1);
        labels.Children.Add(_progressText);

        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Height = 4;
        _progress.MinHeight = 4;
        _progress.CornerRadius = new CornerRadius(2);
        _progress.Value = PercentOf(_content.Earned - 1);
        _progress.Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
        _progress.Foreground = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(_mid, 0), new GradientStop(_light, 1) },
        };

        var right = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { labels, _progress } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(icon);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    private double PercentOf(int earned) => _content.Total == 0 ? 0 : Math.Clamp(earned, 0, _content.Total) * 100.0 / _content.Total;

    private static Control Blob(Color color, byte alpha, double size, double left, double top, TranslateTransform shift)
    {
        var blob = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            },
            RenderTransform = shift,
        };
        Canvas.SetLeft(blob, left);
        Canvas.SetTop(blob, top);
        return blob;
    }

    private void BuildRays()
    {
        const double length = 120;
        _rays.Width = MedalColumn;
        _rays.Height = CardHeight;
        _rays.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _rays.RenderTransform = _raysSpin;
        _rays.Opacity = 0;
        var count = _content.Grade == 1 ? 16 : 10;
        for (var i = 0; i < count; i++)
        {
            var ray = new Border
            {
                Width = i % 2 == 0 ? 10 : 5,
                Height = length,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, _light.R, _light.G, _light.B), 0),
                        new GradientStop(Color.FromArgb(120, _light.R, _light.G, _light.B), 1),
                    },
                },
                RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                RenderTransform = new RotateTransform(i * 360.0 / count),
            };
            Canvas.SetLeft(ray, MedalColumn / 2 - ray.Width / 2);
            Canvas.SetTop(ray, CardHeight / 2 - length);
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
        if (t >= _leaveAt + ExitSeconds)
        {
            _frame.Stop();
            Close();
            return;
        }

        var leaving = t > _leaveAt;
        var leave = leaving ? Math.Clamp((t - _leaveAt) / ExitSeconds, 0, 1) : 0;
        if (t >= SpinStart && !_chimed)
        {
            _chimed = true;
            TrophySound.Play(_content.Grade);
        }

        if (_reducedMotion)
        {
            Opacity = leaving ? 1 - leave : Math.Min(1, t / 0.25);
            foreach (var (line, _, _) in _lines)
            {
                line.Opacity = 1;
            }

            _medal.Spin = 0;
            _medalScale.ScaleX = _medalScale.ScaleY = 1;
            if (_content.Total > 0)
            {
                _progress.Value = PercentOf(_content.Earned);
                _progressText.Text = $"{_content.Earned} / {_content.Total}";
            }

            return;
        }

        // The card swings in from the right around its right edge, and back out the same way.
        var enter = Math.Clamp(t / EnterSeconds, 0, 1);
        if (leaving)
        {
            var ease = EaseInCubic(leave);
            _cardShift.X = ease * 140;
            _cardTilt.AngleY = -ease * 40;
            Opacity = 1 - ease;
        }
        else
        {
            _cardShift.X = (1 - EaseOutBack(enter)) * 170;
            _cardTilt.AngleY = (1 - EaseOutCubic(enter)) * 55;
            Opacity = Math.Min(1, enter * 2);
        }

        // The medal spins like a tossed coin, slowing down to land face on at the impact.
        var spin = Math.Clamp((t - SpinStart) / (Impact - SpinStart), 0, 1);
        _medal.Spin = (1 - EaseOutQuart(spin)) * Math.PI * 6;
        var grow = 0.45 + 0.55 * EaseOutCubic(Math.Min(1, spin * 2.2));
        var sinceImpact = t - Impact;
        var thump = sinceImpact >= 0 ? Math.Exp(-sinceImpact * 9) * Math.Cos(sinceImpact * 28) * 0.14 : 0;
        _medalScale.ScaleX = _medalScale.ScaleY = grow + thump;
        _medal.Glint = sinceImpact is >= 0.05 and <= 0.75 ? (sinceImpact - 0.05) / 0.7 : -1;

        // Landing: the card gives a little, the light behind the medal flares, the ring goes out.
        _cardScale.ScaleX = _cardScale.ScaleY = sinceImpact >= 0 ? 1 + Math.Exp(-sinceImpact * 10) * Math.Sin(sinceImpact * 30) * 0.012 : 1;
        _burst.Opacity = sinceImpact >= 0
            ? 0.4 + 0.6 * Math.Exp(-sinceImpact * 3.5) + 0.08 * Math.Sin(t * 3)
            : 0.25 + 0.25 * spin;
        if (sinceImpact >= 0 && !_burstDone)
        {
            _burstDone = true;
            SpawnConfetti(_content.Grade switch { 1 => 90, 2 => 54, 3 => 32, _ => 22 }, 1.0);
        }

        if (_content.Grade == 1 && sinceImpact >= 0.55 && !_secondBurstDone)
        {
            _secondBurstDone = true;
            SpawnConfetti(50, 0.75);
        }

        var ring = sinceImpact / 0.7;
        _ring.Opacity = ring is >= 0 and <= 1 ? 0.95 * (1 - ring) : 0;
        if (ring is >= 0 and <= 1)
        {
            _ringScale.ScaleX = _ringScale.ScaleY = 1 + EaseOutCubic(ring) * (_content.Grade == 1 ? 1.9 : 1.2);
            Canvas.SetLeft(_ring, MedalCenter.X - MedalSize / 2 + _cardShift.X);
        }

        _rays.Opacity = sinceImpact >= 0 ? Math.Min(1, sinceImpact * 3) * (leaving ? 1 - leave : 1) : 0;
        _raysSpin.Angle = t * 18;

        // The text follows, line by line, sliding in from the right.
        foreach (var (line, shift, delay) in _lines)
        {
            var progress = Math.Clamp((t - delay) / 0.4, 0, 1);
            shift.X = (1 - EaseOutCubic(progress)) * 34;
            line.Opacity = progress;
        }

        // The game's completion moves up to count the new trophy.
        if (_content.Total > 0)
        {
            var fill = Math.Clamp((sinceImpact - 0.15) / 0.8, 0, 1);
            var before = PercentOf(_content.Earned - 1);
            _progress.Value = before + (PercentOf(_content.Earned) - before) * EaseOutCubic(fill);
            _progressText.Text = $"{(fill > 0.5 ? _content.Earned : Math.Max(0, _content.Earned - 1))} / {_content.Total}";
        }

        var sweep = (sinceImpact - 0.25) / 0.85;
        _shine.Opacity = sweep is >= 0 and <= 1 ? 1 : 0;
        _shineShift.X = -180 + EaseInOutCubic(Math.Clamp(sweep, 0, 1)) * (CardWidth + 320);

        _auroraA.X = Math.Sin(t * 0.7) * 60;
        _auroraA.Y = Math.Cos(t * 0.5) * 25;
        _auroraB.X = Math.Cos(t * 0.6) * 70;
        _auroraB.Y = Math.Sin(t * 0.8) * 30;
        _glow.Opacity = 0.75 + 0.25 * Math.Sin(t * 2.4);
        if (_content.Grade == 1)
        {
            _edgeSpin.Angle = t * 40;
        }

        StepConfetti(dt);
    }

    private void SpawnConfetti(int count, double strength)
    {
        Color[] colors = _content.Grade == 1
            ? [_light, _mid, Colors.White, Color.Parse("#B79CFF"), Color.Parse("#FF9BDB")]
            : [_light, _mid, Colors.White, TrophyMedal.Shade(_mid, 0.8)];
        var origin = MedalCenter;
        for (var i = 0; i < count; i++)
        {
            // Mostly up and out to the left, away from the screen edge.
            var angle = -Math.PI / 2 + (_random.NextDouble() - 0.62) * Math.PI * 1.5;
            var speed = (260 + _random.NextDouble() * 420) * strength;
            var round = _random.NextDouble() < 0.25;
            var width = round ? 5 + _random.NextDouble() * 3 : 6 + _random.NextDouble() * 6;
            var height = round ? width : 3 + _random.NextDouble() * 3;
            var rotation = new RotateTransform(_random.NextDouble() * 360);
            var tumble = new ScaleTransform();
            var visual = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(round ? width / 2 : 1),
                Background = new SolidColorBrush(colors[_random.Next(colors.Length)]),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = new TransformGroup { Children = { tumble, rotation } },
            };
            _confetti.Add(new Confetti
            {
                Visual = visual,
                Rotation = rotation,
                Tumble = tumble,
                X = origin.X + _cardShift.X,
                Y = origin.Y,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Life = 1.4 + _random.NextDouble() * 1.2,
                Spin = (_random.NextDouble() - 0.5) * 720,
                TumbleSpeed = 6 + _random.NextDouble() * 10,
                Phase = _random.NextDouble() * Math.PI * 2,
                Width = width,
                Height = height,
            });
            _overlay.Children.Add(visual);
        }
    }

    private void StepConfetti(double dt)
    {
        for (var i = _confetti.Count - 1; i >= 0; i--)
        {
            var p = _confetti[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _overlay.Children.Remove(p.Visual);
                _confetti.RemoveAt(i);
                continue;
            }

            // Air slows the pieces quickly, so they burst and then flutter down.
            p.Vy += 620 * dt;
            p.Vx *= 1 - 2.2 * dt;
            p.Vy *= 1 - 1.4 * dt;
            p.X += p.Vx * dt + Math.Sin(p.Age * 5 + p.Phase) * 18 * dt;
            p.Y += p.Vy * dt;
            p.Rotation.Angle += p.Spin * dt;
            p.Tumble.ScaleY = Math.Cos(p.Age * p.TumbleSpeed + p.Phase);
            Canvas.SetLeft(p.Visual, p.X - p.Width / 2);
            Canvas.SetTop(p.Visual, p.Y - p.Height / 2);
            p.Visual.Opacity = 1 - EaseInCubic(p.Age / p.Life);
        }
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.4;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseOutQuart(double t) => 1 - Math.Pow(1 - t, 4);

    private static double EaseInCubic(double t) => t * t * t;

    private static double EaseInOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    // Top right of the primary screen, where games rarely put their own HUD.
    private void Place()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        Position = new PixelPoint(area.Right - (int)(Width * scale), area.Y);
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
