// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

/// <summary>
/// The launcher's trophy overview, laid out like the console's: every game with trophies, its
/// completion and how many of each grade were earned. Picking a game opens its trophy list.
/// </summary>
public partial class TrophyHubWindow : Window
{
    private sealed record GameSummary(GameEntry Game, string Directory, IReadOnlyList<TrophyEntry> Trophies)
    {
        public int Earned => Trophies.Count(t => t.Unlocked);

        public int Percent => Trophies.Count == 0 ? 0 : Earned * 100 / Trophies.Count;

        public int EarnedOf(int grade) => Trophies.Count(t => t.Grade == grade && t.Unlocked);
    }

    private static readonly IBrush RowBrush = new SolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IBrush RowSelectedBrush = new SolidColorBrush(Color.Parse("#26FFFFFF"));

    private readonly IReadOnlyList<GameEntry> _games;
    private readonly List<(Border Row, GameSummary Summary)> _rows = [];
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
        KeyDown += OnKeyDown;
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
            .OrderBy(summary => summary.Game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

        RenderTotals(summaries);
        GameList.Children.Clear();
        _rows.Clear();
        foreach (var summary in summaries)
        {
            var row = BuildRow(summary);
            _rows.Add((row, summary));
            GameList.Children.Add(row);
        }

        if (_rows.Count == 0)
        {
            GameList.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Get("Trophies.None"),
                Foreground = (IBrush)this.FindResource("MutedBrush")!,
                FontSize = 15,
            });
            return;
        }

        if (!_animated)
        {
            _animated = true;
            TrophyUi.StaggerIn(_rows.Select(r => (Control)r.Row).ToList());
        }

        Select(0);
    }

    private void RenderTotals(IReadOnlyList<GameSummary> summaries)
    {
        var earned = summaries.Sum(s => s.Earned);
        var total = summaries.Sum(s => s.Trophies.Count);
        // The totals count up the first time the overview opens.
        if (_animated)
        {
            TotalEarnedText.Text = earned.ToString();
        }
        else
        {
            TrophyUi.Animate(0, earned, TimeSpan.FromMilliseconds(900), value => TotalEarnedText.Text = ((int)Math.Round(value)).ToString());
        }

        TotalOfText.Text = $"{Localization.Instance.Get("Trophies.Total")} {total}";
        TotalGrades.Children.Clear();
        for (var grade = 1; grade <= 4; grade++)
        {
            TotalGrades.Children.Add(BuildCount(grade, summaries.Sum(s => s.EarnedOf(grade)), 26, 22, bold: true));
        }
    }

    private static Control BuildCount(int grade, int count, double cupSize, double textSize, bool bold)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                TrophyUi.Cup(grade, cupSize),
                new TextBlock
                {
                    Text = count.ToString(),
                    FontSize = textSize,
                    FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private Border BuildRow(GameSummary summary)
    {
        var muted = (IBrush)this.FindResource("MutedBrush")!;
        var cover = new Border
        {
            Width = 76,
            Height = 76,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = summary.Game.PlaceholderBrush,
            Child = summary.Game.Cover is { } bitmap
                ? new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
                : new TextBlock
                {
                    Text = summary.Game.Initials,
                    FontSize = 24,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
        };

        var name = new TextBlock
        {
            Text = summary.Game.Name,
            FontSize = 18,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = summary.Percent,
            Height = 4,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#33FFFFFF")),
        };
        if (!_animated)
        {
            // The bars fill as the rows come in.
            TrophyUi.Animate(0, summary.Percent, TimeSpan.FromMilliseconds(900), value => bar.Value = value);
        }

        var progress = new StackPanel
        {
            Spacing = 6,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = $"{summary.Percent} %",
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Right,
                },
                bar,
            },
        };

        // One column per grade, always present so the counts line up from row to row.
        var counts = new Grid { ColumnDefinitions = new ColumnDefinitions("72,72,72,72"), VerticalAlignment = VerticalAlignment.Center };
        for (var grade = 1; grade <= 4; grade++)
        {
            var count = BuildCount(grade, summary.EarnedOf(grade), 22, 16, bold: false);
            if (grade == 1 && summary.Trophies.All(t => t.Grade != 1))
            {
                count.Opacity = 0.25;
            }

            Grid.SetColumn(count, grade - 1);
            counts.Children.Add(count);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 28 };
        grid.Children.Add(cover);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(progress, 2);
        grid.Children.Add(progress);
        Grid.SetColumn(counts, 3);
        grid.Children.Add(counts);

        var row = new Border
        {
            Background = RowBrush,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(14, 12, 24, 12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        TrophyUi.SmoothHover(row);
        ToolTip.SetTip(row, Localization.Instance.Get("Trophies.Open"));
        row.PointerEntered += (_, _) => Select(_rows.FindIndex(r => r.Row == row));
        row.PointerPressed += (_, _) => Open(summary);
        _ = muted;
        return row;
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        _selected = index;
        for (var i = 0; i < _rows.Count; i++)
        {
            var selected = i == index;
            _rows[i].Row.Background = selected ? RowSelectedBrush : RowBrush;
            _rows[i].Row.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
        }

        _rows[index].Row.BringIntoView();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Select(Math.Min(_rows.Count - 1, _selected + 1));
                e.Handled = true;
                break;
            case Key.Up:
                Select(Math.Max(0, _selected - 1));
                e.Handled = true;
                break;
            case Key.Enter when _selected >= 0:
                Open(_rows[_selected].Summary);
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
        await new TrophiesWindow(summary.Game.Name, summary.Directory, summary.Game.TitleId!).ShowDialog(this);
        // Progress may have changed while the detail was open.
        await LoadAsync();
    }
}
