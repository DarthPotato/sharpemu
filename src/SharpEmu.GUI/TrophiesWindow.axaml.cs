// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

/// <summary>Lists a game's trophies with the progress the emulator saved for it.</summary>
public partial class TrophiesWindow : Window
{
    private enum Filter
    {
        All,
        Earned,
        Locked,
    }

    private const double ArcRadius = 61;

    private readonly string _gameDirectory;
    private readonly string _titleId;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, Bitmap?> _icons = [];
    private readonly HashSet<int> _revealed = [];
    private IReadOnlyList<TrophyEntry> _trophies = [];
    private Filter _filter = Filter.All;
    private string _lastSignature = string.Empty;

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
        FilterAll.Content = Localization.Instance.Get("Trophies.Filter.All");
        FilterEarned.Content = Localization.Instance.Get("Trophies.Filter.Earned");
        FilterLocked.Content = Localization.Instance.Get("Trophies.Filter.Locked");

        RefreshButton.Click += (_, _) => Reload(force: true);
        CloseButton.Click += (_, _) => Close();
        FilterAll.Click += (_, _) => SetFilter(Filter.All);
        FilterEarned.Click += (_, _) => SetFilter(Filter.Earned);
        FilterLocked.Click += (_, _) => SetFilter(Filter.Locked);
        // The game may earn a trophy while this window is open.
        _refreshTimer.Tick += (_, _) => Reload(force: false);
        Opened += (_, _) =>
        {
            Reload(force: true);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void SetFilter(Filter filter)
    {
        _filter = filter;
        FilterAll.Classes.Set("accent", filter == Filter.All);
        FilterAll.Classes.Set("ghost", filter != Filter.All);
        FilterEarned.Classes.Set("accent", filter == Filter.Earned);
        FilterEarned.Classes.Set("ghost", filter != Filter.Earned);
        FilterLocked.Classes.Set("accent", filter == Filter.Locked);
        FilterLocked.Classes.Set("ghost", filter != Filter.Locked);
        RenderList();
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
        _trophies = trophies;
        RenderSummary();
        RenderList();

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
        PercentText.Text = $"{percent}%";
        ProgressText.Text = Localization.Instance.Format("Trophies.Progress", earned, _trophies.Count, percent);
        ProgressArc.Data = BuildArc(_trophies.Count == 0 ? 0 : (double)earned / _trophies.Count);

        GradeChips.Children.Clear();
        for (var grade = 1; grade <= 4; grade++)
        {
            var total = _trophies.Count(t => t.Grade == grade);
            if (total == 0)
            {
                continue;
            }

            var count = _trophies.Count(t => t.Grade == grade && t.Unlocked);
            GradeChips.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#12FFFFFF")),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Ellipse { Width = 10, Height = 10, Fill = TrophyUi.GradeBrush(grade), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = $"{TrophyUi.GradeName(grade)}  {count}/{total}", FontSize = 13, FontWeight = FontWeight.SemiBold },
                    },
                },
            });
        }
    }

    private static Geometry BuildArc(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        if (fraction <= 0)
        {
            return new StreamGeometry();
        }

        const double centre = 66;
        var angle = fraction * 2 * Math.PI;
        var start = new Point(centre, centre - ArcRadius);
        var end = new Point(centre + ArcRadius * Math.Sin(angle), centre - ArcRadius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false);
            context.ArcTo(end, new Size(ArcRadius, ArcRadius), 0, fraction > 0.5, SweepDirection.Clockwise);
        }

        return geometry;
    }

    private void RenderList()
    {
        TrophyList.Children.Clear();
        if (_trophies.Count == 0)
        {
            TrophyList.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Get("Trophies.None"),
                Foreground = (IBrush)this.FindResource("MutedBrush")!,
                FontSize = 15,
                Margin = new Thickness(0, 20),
            });
            return;
        }

        // Earned trophies first, then by id, so progress is visible without scrolling.
        var shown = _trophies
            .Where(t => _filter switch
            {
                Filter.Earned => t.Unlocked,
                Filter.Locked => !t.Unlocked,
                _ => true,
            })
            .OrderByDescending(t => t.Unlocked)
            .ThenBy(t => t.Id);
        foreach (var trophy in shown)
        {
            TrophyList.Children.Add(BuildRow(trophy));
        }
    }

    private Control BuildRow(TrophyEntry trophy)
    {
        var muted = (IBrush)this.FindResource("MutedBrush")!;
        var canReveal = trophy.Hidden && !trophy.Unlocked;
        var hidden = canReveal && !_revealed.Contains(trophy.Id);
        var gradeBrush = TrophyUi.GradeBrush(trophy.Grade);

        // Hidden trophies keep their icon; only the name and description are held back.
        _icons.TryGetValue(trophy.Id, out var bitmap);
        var icon = new Border
        {
            Width = 80,
            Height = 80,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Background = (IBrush)this.FindResource("ElevatedBrush")!,
            BorderBrush = trophy.Unlocked ? gradeBrush : new SolidColorBrush(Color.Parse("#12FFFFFF")),
            BorderThickness = new Thickness(2),
            Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, Opacity = trophy.Unlocked ? 1.0 : 0.5 },
        };

        var title = new TextBlock
        {
            Text = hidden ? Localization.Instance.Get("Trophies.Hidden") : trophy.Name,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var description = new TextBlock
        {
            Text = hidden ? Localization.Instance.Get("Trophies.HiddenDescription") : trophy.Description,
            Foreground = (IBrush)this.FindResource("SecondaryTextBrush")!,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        var status = new TextBlock
        {
            Text = trophy.UnlockedAtUtc is { } at
                ? "✓  " + Localization.Instance.Format("Trophies.Earned", at.ToLocalTime().ToString("g"))
                : "🔒  " + Localization.Instance.Get("Trophies.Locked"),
            Foreground = trophy.Unlocked ? (IBrush)this.FindResource("SuccessBrush")! : muted,
            FontSize = 12.5,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var grade = new Border
        {
            Background = new SolidColorBrush(((SolidColorBrush)gradeBrush).Color, 0.16),
            BorderBrush = gradeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = TrophyUi.GradeName(trophy.Grade),
                Foreground = gradeBrush,
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var text = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, description, status },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 18 };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(grade, 2);
        grid.Children.Add(grade);

        var strip = new Border
        {
            Width = 5,
            Background = trophy.Unlocked ? gradeBrush : new SolidColorBrush(Color.Parse("#22FFFFFF")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var content = new Grid();
        content.Children.Add(strip);
        grid.Margin = new Thickness(22, 14, 18, 14);
        content.Children.Add(grid);

        var card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(0),
            ClipToBounds = true,
            Opacity = trophy.Unlocked ? 1.0 : 0.85,
            Child = content,
        };
        if (canReveal)
        {
            // Like the console: a hidden trophy stays a mystery until you choose to look.
            card.Cursor = new Cursor(StandardCursorType.Hand);
            card.PointerPressed += (_, _) =>
            {
                if (!_revealed.Remove(trophy.Id))
                {
                    _revealed.Add(trophy.Id);
                }

                RenderList();
            };
        }

        return card;
    }
}
