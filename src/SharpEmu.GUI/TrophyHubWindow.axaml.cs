// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SharpEmu.GUI.Controls;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

/// <summary>
/// The launcher's trophy collection: every game with trophies as a card with its cover,
/// completion and earned medals, under totals for the whole library. Picking a game opens its list.
/// </summary>
public partial class TrophyHubWindow : Window
{
    private sealed record GameSummary(GameEntry Game, string Directory, IReadOnlyList<TrophyEntry> Trophies)
    {
        public int Earned => Trophies.Count(t => t.Unlocked);

        public double Percent => Trophies.Count == 0 ? 0 : Earned * 100.0 / Trophies.Count;

        public int EarnedOf(int grade) => Trophies.Count(t => t.Grade == grade && t.Unlocked);

        public int TotalOf(int grade) => Trophies.Count(t => t.Grade == grade);
    }

    private readonly IReadOnlyList<GameEntry> _games;
    private readonly List<(Border Card, GameSummary Summary)> _cards = [];
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _selected = -1;
    private bool _animated;

    public TrophyHubWindow()
        : this([])
    {
    }

    public TrophyHubWindow(IReadOnlyList<GameEntry> games)
    {
        InitializeComponent();
        _games = games;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _idle.Stop();
        KeyDown += OnKeyDown;

        // The collection's medal turns to face the viewer, then catches the light now and then.
        _idle.Tick += (_, _) =>
        {
            var t = _clock.Elapsed.TotalSeconds;
            HeroMedal.Spin = t < 1.4 ? (1 - (1 - Math.Pow(1 - t / 1.4, 4))) * Math.PI * 4 : 0;
            var cycle = (t - 1.2) % 4.5;
            HeroMedal.Glint = t > 1.2 && cycle < 0.9 ? cycle / 0.9 : -1;
        };
        _idle.Start();
    }

    private async Task LoadAsync()
    {
        var summaries = await Task.Run(() => _games
            .Select(game => (Game: game, Directory: Path.GetDirectoryName(game.Path)))
            .Where(entry => entry.Directory is not null)
            .AsParallel()
            .Select(entry => new GameSummary(
                entry.Game,
                entry.Directory!,
                TrophyCatalog.Load(entry.Directory!, entry.Game.TitleId!, includeIcons: false)))
            .Where(summary => summary.Trophies.Count > 0)
            .OrderByDescending(summary => summary.Trophies.Where(t => t.UnlockedAtUtc is not null).Select(t => t.UnlockedAtUtc).DefaultIfEmpty().Max())
            .ThenBy(summary => summary.Game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

        RenderTotals(summaries);
        GameList.Children.Clear();
        _cards.Clear();
        foreach (var summary in summaries)
        {
            var card = BuildCard(summary);
            _cards.Add((card, summary));
            GameList.Children.Add(card);
        }

        if (_cards.Count == 0)
        {
            GameList.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Get("Trophies.None"),
                Foreground = new SolidColorBrush(Color.Parse("#8E89A6")),
                FontSize = 15,
            });
            return;
        }

        if (!_animated)
        {
            TrophyUi.StaggerIn(_cards.Select(c => (Control)c.Card).ToList());
        }

        _animated = true;
        Select(Math.Max(0, _selected));
    }

    private void RenderTotals(IReadOnlyList<GameSummary> summaries)
    {
        var earned = summaries.Sum(s => s.Earned);
        var total = summaries.Sum(s => s.Trophies.Count);
        var percent = total == 0 ? 0 : earned * 100.0 / total;
        TotalOfText.Text = Localization.Instance.Format("Trophies.HubSummary", earned, total, summaries.Count);
        var duration = TimeSpan.FromMilliseconds(_animated ? 400 : 1300);
        TrophyUi.Animate(_animated ? earned : 0, earned, duration, value => TotalEarnedText.Text = ((int)Math.Round(value)).ToString());
        TrophyUi.Animate(_animated ? percent : 0, percent, duration, value => TotalRing.Value = value);
        TotalGrades.Children.Clear();
        for (var grade = 1; grade <= 4; grade++)
        {
            TotalGrades.Children.Add(TrophyUi.GradeChip(grade, summaries.Sum(s => s.EarnedOf(grade)), summaries.Sum(s => s.TotalOf(grade))));
        }
    }

    private Border BuildCard(GameSummary summary)
    {
        var cover = new Border
        {
            Width = 92,
            Height = 92,
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Background = summary.Game.PlaceholderBrush,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 8, Blur = 18, Color = Color.FromArgb(120, 0, 0, 0) }),
            Child = summary.Game.Cover is { } bitmap
                ? new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
                : new TextBlock
                {
                    Text = summary.Game.Initials,
                    FontSize = 26,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
        };

        var name = new TextBlock
        {
            Text = summary.Game.Name,
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var counts = new TextBlock
        {
            Text = Localization.Instance.Format("Trophies.Progress", summary.Earned, summary.Trophies.Count, (int)Math.Round(summary.Percent)),
            Foreground = new SolidColorBrush(Color.Parse("#B8B4CC")),
            FontSize = 13,
        };
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            MinHeight = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Foreground = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#8FE3FF"), 0),
                    new GradientStop(Color.Parse("#9B7CFF"), 0.6),
                    new GradientStop(Color.Parse("#FF8FD8"), 1),
                },
            },
        };
        TrophyUi.Animate(_animated ? summary.Percent : 0, summary.Percent, TimeSpan.FromMilliseconds(_animated ? 1 : 1100), value => bar.Value = value);

        var medals = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        for (var grade = 1; grade <= 4; grade++)
        {
            var total = summary.TotalOf(grade);
            if (total == 0)
            {
                continue;
            }

            var earned = summary.EarnedOf(grade);
            medals.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new TrophyMedal { Width = 22, Height = 22, Grade = grade, Opacity = earned == 0 ? 0.55 : 1 },
                    new TextBlock { Text = earned.ToString(), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }

        var middle = new StackPanel
        {
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, counts, bar, medals },
        };

        var ring = new Grid { Width = 74, Height = 74, VerticalAlignment = VerticalAlignment.Center };
        var progressRing = new TrophyProgressRing { Thickness = 7 };
        TrophyUi.Animate(_animated ? summary.Percent : 0, summary.Percent, TimeSpan.FromMilliseconds(_animated ? 1 : 1100), value => progressRing.Value = value);
        ring.Children.Add(progressRing);
        ring.Children.Add(new TextBlock
        {
            Text = $"{Math.Round(summary.Percent)}%",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 22 };
        grid.Children.Add(cover);
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);
        Grid.SetColumn(ring, 2);
        grid.Children.Add(ring);

        var card = new Border
        {
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(30, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(12, 255, 255, 255), 1),
                },
            },
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        TrophyUi.Lift(card);
        ToolTip.SetTip(card, Localization.Instance.Get("Trophies.Open"));
        card.PointerEntered += (_, _) => _selected = _cards.FindIndex(c => c.Card == card);
        card.PointerPressed += (_, _) => Open(summary);
        return card;
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _cards.Count)
        {
            return;
        }

        _selected = index;
        for (var i = 0; i < _cards.Count; i++)
        {
            _cards[i].Card.BorderBrush = new SolidColorBrush(i == index ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(30, 255, 255, 255));
        }

        _cards[index].Card.BringIntoView();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Select(Math.Min(_cards.Count - 1, _selected + 1));
                e.Handled = true;
                break;
            case Key.Up:
                Select(Math.Max(0, _selected - 1));
                e.Handled = true;
                break;
            case Key.Enter when _selected >= 0 && _selected < _cards.Count:
                Open(_cards[_selected].Summary);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private async void Open(GameSummary summary)
    {
        await new TrophiesWindow(summary.Game.Name, summary.Directory, summary.Game.TitleId!, summary.Game).ShowDialog(this);
        // Progress may have changed while the list was open.
        await LoadAsync();
    }
}
