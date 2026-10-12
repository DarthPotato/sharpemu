// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

/// <summary>Lists a game's trophies with the progress the emulator saved for it.</summary>
public partial class TrophiesWindow : Window
{
    private readonly string _gameDirectory;
    private readonly string _titleId;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
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

        RefreshButton.Click += (_, _) => Reload();
        CloseButton.Click += (_, _) => Close();
        // The game may earn a trophy while this window is open.
        _refreshTimer.Tick += (_, _) => Reload();
        Opened += (_, _) =>
        {
            Reload();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void Reload()
    {
        var trophies = TrophyCatalog.Load(_gameDirectory, _titleId);
        var signature = string.Join(',', trophies.Where(t => t.Unlocked).Select(t => t.Id)) + "/" + trophies.Count;
        if (signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;
        Render(trophies);
    }

    private void Render(IReadOnlyList<TrophyEntry> trophies)
    {
        TrophyList.Children.Clear();
        var earned = trophies.Count(t => t.Unlocked);
        var percent = trophies.Count == 0 ? 0 : earned * 100 / trophies.Count;
        ProgressText.Text = Localization.Instance.Format("Trophies.Progress", earned, trophies.Count, percent);
        ProgressBar.Value = percent;
        GradeCountsText.Text = string.Join(
            "   ",
            Enumerable.Range(1, 4).Select(grade =>
                $"{TrophyUi.GradeName(grade)} {trophies.Count(t => t.Grade == grade && t.Unlocked)}" +
                $"/{trophies.Count(t => t.Grade == grade)}"));

        if (trophies.Count == 0)
        {
            TrophyList.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Get("Trophies.None"),
                Foreground = (IBrush)this.FindResource("MutedBrush")!,
                Margin = new Thickness(0, 12),
            });
            return;
        }

        // Earned trophies first, then by id, so progress is visible without scrolling.
        foreach (var trophy in trophies.OrderByDescending(t => t.Unlocked).ThenBy(t => t.Id))
        {
            TrophyList.Children.Add(BuildRow(trophy));
        }
    }

    private Control BuildRow(TrophyEntry trophy)
    {
        var muted = (IBrush)this.FindResource("MutedBrush")!;
        var hidden = trophy.Hidden && !trophy.Unlocked;
        var icon = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = (IBrush)this.FindResource("ElevatedBrush")!,
            Child = hidden ? null : new Image { Source = TrophyUi.LoadIcon(trophy.IconPng), Stretch = Stretch.UniformToFill },
        };

        var title = new TextBlock
        {
            Text = hidden ? Localization.Instance.Get("Trophies.Hidden") : trophy.Name,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var description = new TextBlock
        {
            Text = hidden ? Localization.Instance.Get("Trophies.HiddenDescription") : trophy.Description,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        var status = new TextBlock
        {
            Text = trophy.UnlockedAtUtc is { } at
                ? Localization.Instance.Format("Trophies.Earned", at.ToLocalTime().ToString("g"))
                : Localization.Instance.Get("Trophies.Locked"),
            Foreground = trophy.Unlocked ? (IBrush)this.FindResource("SuccessBrush")! : muted,
            FontSize = 12,
        };
        var grade = new Border
        {
            Background = TrophyUi.GradeBrush(trophy.Grade),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = TrophyUi.GradeName(trophy.Grade),
                Foreground = Brushes.Black,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, description, status } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(grade, 2);
        grid.Children.Add(grade);

        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(12),
            Opacity = trophy.Unlocked ? 1.0 : 0.55,
            Child = grid,
        };
    }
}
