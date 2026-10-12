// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Threading;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private IReadOnlyList<TrophyEntry> _trophyTestCatalog = [];
    private int _trophyTestGeneration;

    private void InitializeTrophies()
    {
        TrophiesButton.Click += (_, _) => OpenTrophyHub();
        TrophyTestGameBox.SelectionChanged += (_, _) => LoadTrophyTestCatalog();
        TrophyTestButton.Click += (_, _) => ShowTrophyTestNotification();
        TrophySoundToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.TrophySound = TrophySoundToggle.IsChecked == true;
            TrophySound.Enabled = _settings.TrophySound;
        };
        // The game list can change while the launcher is open, so refresh it when the page is shown.
        OptionsEnvironmentNav.Click += (_, _) => RefreshTrophyTestGames();
    }

    private void OpenTrophyHub()
    {
        var hub = new TrophyHubWindow(_allGames.Where(g => !string.IsNullOrWhiteSpace(g.TitleId)).ToArray());
        hub.Show(this);
    }

    private async void OpenSelectedGameTrophies()
    {
        if (GameList.SelectedItem is not GameEntry game ||
            string.IsNullOrWhiteSpace(game.TitleId) ||
            Path.GetDirectoryName(game.Path) is not { } gameDirectory)
        {
            return;
        }

        await new TrophiesWindow(game.Name, gameDirectory, game.TitleId).ShowDialog(this);
    }

    private static async void ShowTrophyToast(TrophyUnlockNotice notice, string? ebootPath)
    {
        TrophyEntry? entry = null;
        if (Path.GetDirectoryName(ebootPath) is { } gameDirectory)
        {
            var titleId = notice.TitleId;
            entry = (await Task.Run(() => TrophyCatalog.Load(gameDirectory, titleId)))
                .FirstOrDefault(t => t.Id == notice.TrophyId);
        }

        TrophyToast.Show(new TrophyToastContent(notice.Name, entry?.Description ?? string.Empty, notice.Grade, entry?.IconPng));
    }

    // Options > Environment > Trophies: pick a game and one of its trophies and show the unlock
    // notification for it, without having to earn anything.
    private void RefreshTrophyTestGames()
    {
        var selectedPath = (TrophyTestGameBox.SelectedItem as GameEntry)?.Path;
        var games = _allGames.Where(g => !string.IsNullOrWhiteSpace(g.TitleId)).OrderBy(g => g.Name).ToList();
        TrophyTestGameBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GameEntry>(
            (game, _) => new TextBlock { Text = game?.Name });
        TrophyTestGameBox.ItemsSource = games;
        TrophyTestGameBox.SelectedItem = games.FirstOrDefault(g => g.Path == selectedPath) ?? games.FirstOrDefault();
    }

    private async void LoadTrophyTestCatalog()
    {
        var generation = ++_trophyTestGeneration;
        _trophyTestCatalog = [];
        TrophyTestTrophyBox.ItemsSource = null;
        if (TrophyTestGameBox.SelectedItem is not GameEntry game ||
            Path.GetDirectoryName(game.Path) is not { } directory)
        {
            return;
        }

        var catalog = await Task.Run(() => TrophyCatalog.Load(directory, game.TitleId!));
        if (generation != _trophyTestGeneration)
        {
            return;
        }

        _trophyTestCatalog = catalog;
        TrophyTestTrophyBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TrophyEntry>(
            (trophy, _) => new TextBlock { Text = trophy is null ? string.Empty : $"{TrophyUi.GradeName(trophy.Grade)} · {trophy.Name}" });
        TrophyTestTrophyBox.ItemsSource = catalog;
        TrophyTestTrophyBox.SelectedIndex = catalog.Count > 0 ? 0 : -1;
    }

    private void ShowTrophyTestNotification()
    {
        if (TrophyTestTrophyBox.SelectedItem is TrophyEntry trophy)
        {
            TrophyToast.Show(new TrophyToastContent(trophy.Name, trophy.Description, trophy.Grade, trophy.IconPng));
        }
    }
}
