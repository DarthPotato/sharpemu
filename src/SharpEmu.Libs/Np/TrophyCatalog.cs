// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

/// <summary>One trophy of a title, as the launcher lists it.</summary>
/// <param name="Grade">1 platinum, 2 gold, 3 silver, 4 bronze.</param>
public sealed record TrophyEntry(
    int Id,
    int Grade,
    string Name,
    string Description,
    bool Hidden,
    bool Unlocked,
    DateTime? UnlockedAtUtc,
    byte[]? IconPng);

/// <summary>A trophy the running title just earned, parsed from the emulator's log.</summary>
public sealed record TrophyUnlockNotice(string TitleId, int TrophyId, int Grade, string Name);

/// <summary>Read-only view of a title's trophies for the launcher, which runs in another process.</summary>
public static class TrophyCatalog
{
    /// <summary>Lists the title trophies with the progress saved for it; empty without a trophy package. Icons are the large part, so overviews pass false.</summary>
    public static IReadOnlyList<TrophyEntry> Load(string gameDirectory, string titleId, bool includeIcons = true)
    {
        try
        {
            var packagePath = Path.Combine(gameDirectory, "sce_sys", "trophy2", "trophy00.ucp");
            if (!File.Exists(packagePath))
            {
                return [];
            }

            var trophies = TrophyService.ReadTrophies(File.ReadAllBytes(packagePath), includeIcons);
            TrophyService.ApplyUnlocks(trophies, TrophyService.UnlockFilePath(titleId.ToUpperInvariant()));
            return trophies
                .Select(t => new TrophyEntry(
                    t.Id,
                    t.Grade,
                    t.Name,
                    t.Description,
                    t.Hidden,
                    t.Unlocked,
                    t.Unlocked ? new DateTime(t.UnlockedAt * 10, DateTimeKind.Utc) : null,
                    t.IconPng))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Recognizes the log line the emulator prints when a trophy is earned.</summary>
    public static bool TryParseUnlockLine(string line, out TrophyUnlockNotice notice)
    {
        notice = null!;
        var start = line.IndexOf(TrophyService.UnlockMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var parts = line[(start + TrophyService.UnlockMarker.Length)..].Split('|', 4);
        if (parts.Length != 4 || !int.TryParse(parts[1], out var id) || !int.TryParse(parts[2], out var grade))
        {
            return false;
        }

        notice = new TrophyUnlockNotice(parts[0], id, grade, parts[3].TrimEnd());
        return true;
    }
}
