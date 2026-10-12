// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

/// <summary>
/// One game's trophies, laid out like the console's list. A hidden trophy stays hidden, icon
/// included, until its row is clicked.
/// </summary>
public partial class TrophiesWindow : Window
{
    private static readonly IBrush RowBrush = new SolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IBrush RowHoverBrush = new SolidColorBrush(Color.Parse("#26FFFFFF"));

    private readonly string _gameDirectory;
    private readonly string _titleId;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, Bitmap?> _icons = [];
    private readonly HashSet<int> _revealed = [];
    private IReadOnlyList<TrophyEntry> _trophies = [];
    private string _lastSignature = string.Empty;
    private HashSet<int> _earnedBefore = [];
    private int _shownPercent;
    private bool _animated;

    public TrophiesWindow()
        : this(string.Empty, string.Empty, string.Empty)
    {
    }

    public TrophiesWindow(string gameName, string gameDirectory, string titleId)
    {
        InitializeComponent();
        _gameDirectory = gameDirectory;
        _titleId = titleId;
        GameNameText.Text = gameName;
        // The game may earn a trophy while this window is open.
        _refreshTimer.Tick += (_, _) => Reload(force: false);
        Opened += (_, _) =>
        {
            Reload(force: true);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private async void Reload(bool force)
    {
        var gameDirectory = _gameDirectory;
        var titleId = _titleId;
        var trophies = await Task.Run(() => TrophyCatalog.Load(gameDirectory, titleId));
        var signature = string.Join(',', trophies.Where(t => t.Unlocked).Select(t => t.Id)) + "/" + trophies.Count;
        if (!force && signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;
        // Trophies earned while the window is open get a moment in the light.
        var earnedNow = trophies.Where(t => t.Unlocked).Select(t => t.Id).ToHashSet();
        var fresh = force ? new HashSet<int>() : earnedNow.Except(_earnedBefore).ToHashSet();
        _earnedBefore = earnedNow;
        _trophies = trophies;
        RenderSummary();
        RenderList(fresh);

        // The package icons are large; decode the ones not seen yet off the UI thread.
        var missing = trophies.Where(t => !_icons.ContainsKey(t.Id)).ToArray();
        if (missing.Length > 0)
        {
            var decoded = await Task.Run(() => missing.Select(t => (t.Id, Icon: TrophyUi.LoadIcon(t.IconPng))).ToArray());
            foreach (var (id, icon) in decoded)
            {
                _icons[id] = icon;
            }

            RenderList();
        }
    }

    private void RenderSummary()
    {
        var earned = _trophies.Count(t => t.Unlocked);
        var percent = _trophies.Count == 0 ? 0 : earned * 100 / _trophies.Count;
        // The completion counts up to its value instead of appearing at it.
        TrophyUi.Animate(_shownPercent, percent, TimeSpan.FromMilliseconds(_animated ? 500 : 900), value =>
        {
            PercentText.Text = $"{Math.Round(value)} %";
            ProgressBar.Value = value;
        });
        _shownPercent = percent;
        ProgressText.Text = Localization.Instance.Format("Trophies.Progress", earned, _trophies.Count, percent);
        GradeCounts.Children.Clear();
        for (var grade = 1; grade <= 4; grade++)
        {
            if (_trophies.All(t => t.Grade != grade))
            {
                continue;
            }

            GradeCounts.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    TrophyUi.Cup(grade, 26),
                    new TextBlock
                    {
                        Text = _trophies.Count(t => t.Grade == grade && t.Unlocked).ToString(),
                        FontSize = 22,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            });
        }
    }

    private void RenderList(IReadOnlySet<int>? fresh = null)
    {
        TrophyList.Children.Clear();
        if (_trophies.Count == 0)
        {
            TrophyList.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Get("Trophies.None"),
                Foreground = (IBrush)this.FindResource("MutedBrush")!,
                FontSize = 15,
            });
            return;
        }

        // Earned trophies first, then by id, so progress is visible without scrolling.
        foreach (var trophy in _trophies.OrderByDescending(t => t.Unlocked).ThenBy(t => t.Id))
        {
            var row = BuildRow(trophy);
            TrophyList.Children.Add(row);
            if (fresh?.Contains(trophy.Id) == true)
            {
                Celebrate((Border)row, trophy.Grade);
            }
        }

        if (!_animated && TrophyList.Children.Count > 0)
        {
            _animated = true;
            TrophyUi.StaggerIn(TrophyList.Children.ToList());
        }
    }

    private Control BuildRow(TrophyEntry trophy)
    {
        var muted = (IBrush)this.FindResource("MutedBrush")!;
        var canReveal = trophy.Hidden && !trophy.Unlocked;
        var hidden = canReveal && !_revealed.Contains(trophy.Id);

        // While hidden, the icon, name and description all stay out of sight.
        _icons.TryGetValue(trophy.Id, out var bitmap);
        Control iconContent = hidden
            ? new TextBlock
            {
                Classes = { "materialSymbol" },
                Text = "visibility_off",
                FontSize = 30,
                LineHeight = 34,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new Image { Source = bitmap, Stretch = Stretch.UniformToFill, Opacity = trophy.Unlocked ? 1.0 : 0.55 };
        // An earned trophy's icon carries its grade color around it.
        var icon = new Border
        {
            Width = 76,
            Height = 76,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#22FFFFFF")),
            BorderBrush = trophy.Unlocked ? TrophyUi.GradeBrush(trophy.Grade) : Brushes.Transparent,
            BorderThickness = new Thickness(trophy.Unlocked ? 2 : 0),
            Child = iconContent,
        };

        var text = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = hidden ? Localization.Instance.Get("Trophies.Hidden") : trophy.Name,
                    FontSize = 18,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = hidden ? Localization.Instance.Get("Trophies.HiddenDescription") : trophy.Description,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                },
            },
        };

        var side = new StackPanel
        {
            Spacing = 4,
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = TrophyUi.GradeName(trophy.Grade),
                            Foreground = muted,
                            FontSize = 14,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        TrophyUi.Cup(trophy.Grade, 30),
                    },
                },
                new TextBlock
                {
                    Text = trophy.UnlockedAtUtc is { } at
                        ? at.ToLocalTime().ToString("g")
                        : Localization.Instance.Get("Trophies.Locked"),
                    Foreground = trophy.Unlocked ? (IBrush)this.FindResource("SecondaryTextBrush")! : muted,
                    FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Right,
                },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 24 };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);

        var row = new Border
        {
            Background = RowBrush,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(14, 12, 24, 12),
            Child = grid,
        };
        TrophyUi.SmoothHover(row);
        row.PointerEntered += (_, _) =>
        {
            row.Background = RowHoverBrush;
            row.BorderBrush = Brushes.White;
        };
        row.PointerExited += (_, _) =>
        {
            row.Background = RowBrush;
            row.BorderBrush = Brushes.Transparent;
        };
        if (canReveal)
        {
            // Like the console: a hidden trophy stays a mystery until you choose to look.
            row.Cursor = new Cursor(StandardCursorType.Hand);
            row.PointerPressed += (_, _) =>
            {
                if (!_revealed.Remove(trophy.Id))
                {
                    _revealed.Add(trophy.Id);
                }

                // Only this row changes: it fades over to its other face.
                var index = TrophyList.Children.IndexOf(row);
                if (index < 0)
                {
                    return;
                }

                var replacement = (Border)BuildRow(trophy);
                replacement.Opacity = 0;
                replacement.Transitions!.Add(new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) });
                TrophyList.Children[index] = replacement;
                Dispatcher.UIThread.Post(() => replacement.Opacity = 1, DispatcherPriority.Background);
            };
        }

        return row;
    }

    // A trophy earned while the list is open glows in its grade color, then settles.
    private static void Celebrate(Border row, int grade)
    {
        var color = ((SolidColorBrush)TrophyUi.GradeBrush(grade)).Color;
        row.BorderBrush = new SolidColorBrush(color);
        row.Background = new SolidColorBrush(color, 0.22);
        TrophyUi.Animate(0, 1, TimeSpan.FromMilliseconds(1600), t =>
        {
            var pulse = 0.5 + 0.5 * Math.Cos(t * Math.PI * 4);
            row.Background = new SolidColorBrush(color, 0.08 + 0.14 * pulse * (1 - t));
            if (t >= 1)
            {
                row.Background = RowBrush;
                row.BorderBrush = Brushes.Transparent;
            }
        });
        row.BringIntoView();
    }
}
