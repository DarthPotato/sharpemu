// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SharpEmu.Libs.Np;

/// <summary>
/// The title's trophy package (<c>sce_sys/trophy2/trophy00.ucp</c>), the stat rules that unlock
/// its trophies (<c>sce_sys/uds/uds00.ucp</c>) and the emulated user's progress. A title never
/// asks for an unlock: it posts universal-data-system events, and the console compares the stat
/// each event feeds with the trophy's condition. This type does that comparison.
/// </summary>
/// <remarks>
/// Package layouts and the stat-rule evaluation follow KytyPS5's NpTrophy2 implementation.
/// </remarks>
internal static class TrophyService
{
    // SceNpTrophy2Details is 1312 bytes: id/grade/group at +0/+4/+8, hidden at +12, has-reward at
    // +13, progress target (type, value) at +16, then name[128], description[1024], reward[128].
    // SceNpTrophy2Data is 32 bytes: id at +0, unlocked flag at +4, progress at +8, timestamp at +24.
    internal const int DetailsSize = 1312;
    internal const int DataSize = 32;

    private const string UnlockTrophyEvent = "_UnlockTrophy";

    private enum Comparison
    {
        None,
        GreaterEqual,
        Greater,
        LessEqual,
        Less,
    }

    internal sealed class Trophy
    {
        public int Id { get; init; }
        public int Grade { get; init; }
        public int GroupId { get; init; }
        public bool Hidden { get; init; }
        public bool HasReward { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        internal ulong? StatId { get; init; }
        internal ulong? Target { get; init; }
        internal int Comparer { get; init; }
        public bool Unlocked { get; set; }

        /// <summary>SceRtcTick of the unlock: microseconds since 0001-01-01.</summary>
        public long UnlockedAt { get; set; }

        public bool IsPlatinum => Grade == 1;

        public void WriteDetails(Span<byte> destination)
        {
            destination.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(destination, Id);
            BinaryPrimitives.WriteInt32LittleEndian(destination[4..], Grade);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..], GroupId);
            destination[12] = Hidden ? (byte)1 : (byte)0;
            destination[13] = HasReward ? (byte)1 : (byte)0;
            WriteText(destination.Slice(32, 128), Name);
            WriteText(destination.Slice(160, 1024), Description);
        }

        public void WriteData(Span<byte> destination)
        {
            destination.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(destination, Id);
            destination[4] = Unlocked ? (byte)1 : (byte)0;
            if (Unlocked)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination[24..], UnlockedAt);
            }
        }

        private static void WriteText(Span<byte> field, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
        }
    }

    /// <summary>A stat fed by one named event property: <c>$.input</c> of the event goes to the stat.</summary>
    private readonly record struct StatRule(ulong StatId, string Input, bool IsUInt32);

    private static readonly object Gate = new();
    private static Trophy[]? _trophies;
    private static Dictionary<string, List<StatRule>> _rules = [];
    private static string? _unlockPath;

    /// <summary>Raised after a trophy is unlocked, on the thread that posted the event.</summary>
    internal static event Action<string, Trophy>? Unlocked;

    internal static Trophy[] Trophies
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _trophies!;
            }
        }
    }

    /// <summary>Forgets the loaded package so the next call reads the title's files again.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _trophies = null;
            _rules = [];
            _unlockPath = null;
        }
    }

    /// <summary>Applies one posted event to the trophy stats and unlocks what it satisfies.</summary>
    internal static void OnUdsEvent(string eventName, UdsObjectNode root)
    {
        List<Trophy> unlockedNow = [];
        lock (Gate)
        {
            EnsureLoaded();
            if (_trophies!.Length == 0)
            {
                return;
            }

            // Titles that unlock by id (Hades) post this event; the package's own rules for it
            // only copy the id into a per-trophy stat that the trophy then compares.
            if (eventName == UnlockTrophyEvent &&
                root.Values.TryGetValue("_trophy_id", out var idValue) &&
                idValue.Value is int unlockId)
            {
                var requested = _trophies.FirstOrDefault(t => t.Id == unlockId);
                if (requested is not null && !requested.Unlocked)
                {
                    Unlock(requested, unlockedNow);
                }
            }

            var rules = _rules.GetValueOrDefault(eventName) ?? [];
            foreach (var trophy in _trophies)
            {
                if (trophy.Unlocked || trophy.IsPlatinum || trophy.StatId is not { } statId ||
                    trophy.Target is not { } target)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    if (rule.StatId != statId || !TryReadNumber(root, rule, out var value))
                    {
                        continue;
                    }

                    var matched = (Comparison)trophy.Comparer switch
                    {
                        Comparison.GreaterEqual => value >= target,
                        Comparison.Greater => value > target,
                        Comparison.LessEqual => value <= target,
                        Comparison.Less => value < target,
                        _ => false,
                    };
                    if (matched)
                    {
                        Unlock(trophy, unlockedNow);
                        break;
                    }
                }
            }

            // The platinum comes with the last other trophy.
            var platinum = _trophies.FirstOrDefault(t => t.IsPlatinum && !t.Unlocked);
            if (platinum is not null && unlockedNow.Count > 0 &&
                _trophies.Where(t => !t.IsPlatinum).All(t => t.Unlocked))
            {
                Unlock(platinum, unlockedNow);
            }

            if (unlockedNow.Count > 0)
            {
                SaveUnlocks();
            }
        }

        foreach (var trophy in unlockedNow)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Trophy unlocked: {trophy.Name} ({GradeName(trophy.Grade)}, id {trophy.Id})");
            Unlocked?.Invoke(GradeName(trophy.Grade), trophy);
        }
    }

    private static void Unlock(Trophy trophy, List<Trophy> unlockedNow)
    {
        trophy.Unlocked = true;
        trophy.UnlockedAt = DateTime.UtcNow.Ticks / 10;
        unlockedNow.Add(trophy);
    }

    private static string GradeName(int grade) => grade switch
    {
        1 => "platinum",
        2 => "gold",
        3 => "silver",
        _ => "bronze",
    };

    private static bool TryReadNumber(UdsObjectNode root, StatRule rule, out ulong value)
    {
        value = 0;
        if (!root.Values.TryGetValue(rule.Input, out var property))
        {
            return false;
        }

        switch (property.Kind, property.Value)
        {
            case (UdsValueKind.UInt32, uint u32):
                value = u32;
                return true;
            case (UdsValueKind.UInt64, ulong u64) when !rule.IsUInt32:
                value = u64;
                return true;
            case (UdsValueKind.Int32, int i32) when i32 >= 0:
                value = (ulong)i32;
                return true;
            case (UdsValueKind.Int64, long i64) when i64 >= 0:
                value = (ulong)i64;
                return true;
            default:
                return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_trophies is not null)
        {
            return;
        }

        try
        {
            var trophyPath = KernelMemoryCompatExports.ResolveGuestPath("/app0/sce_sys/trophy2/trophy00.ucp");
            _trophies = File.Exists(trophyPath) ? ReadTrophies(File.ReadAllBytes(trophyPath)) : [];
            var udsPath = KernelMemoryCompatExports.ResolveGuestPath("/app0/sce_sys/uds/uds00.ucp");
            _rules = File.Exists(udsPath) ? ReadRules(File.ReadAllBytes(udsPath)) : [];
            _unlockPath = Path.Combine(
                Path.GetDirectoryName(SaveDataStorage.Root()) ?? SaveDataStorage.Root(),
                "trophies",
                KernelMemoryCompatExports.ApplicationTitleId,
                "unlocks.json");
            LoadUnlocks();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException
                                       or UnauthorizedAccessException)
        {
            _trophies ??= [];
        }
    }

    private static void LoadUnlocks()
    {
        if (_unlockPath is null || !File.Exists(_unlockPath))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(_unlockPath));
        if (!document.RootElement.TryGetProperty("unlocked", out var unlocked) ||
            unlocked.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in unlocked.EnumerateObject())
        {
            if (int.TryParse(entry.Name, out var id) && entry.Value.TryGetInt64(out var at))
            {
                var trophy = _trophies!.FirstOrDefault(t => t.Id == id);
                if (trophy is not null)
                {
                    trophy.Unlocked = true;
                    trophy.UnlockedAt = at;
                }
            }
        }
    }

    private static void SaveUnlocks()
    {
        if (_unlockPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_unlockPath)!);
            var unlocked = _trophies!
                .Where(t => t.Unlocked)
                .ToDictionary(t => t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), t => t.UnlockedAt);
            var temporary = _unlockPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { unlocked }));
            File.Move(temporary, _unlockPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Could not save trophy progress: {ex.Message}");
        }
    }

    // A .ucp is an unencrypted container: a big-endian header, a table of 0x40-byte entries
    // (name[0x20], offset, length), and JSON files for the contents.
    private static Dictionary<string, (int Offset, int Length)> ReadContainer(byte[] package)
    {
        var files = new Dictionary<string, (int Offset, int Length)>();
        if (package.Length < 0x40 || BinaryPrimitives.ReadUInt32BigEndian(package) != 0xB228C60Au)
        {
            return files;
        }

        var fileCount = BinaryPrimitives.ReadUInt32BigEndian(package.AsSpan(0x10));
        var tableOffset = BinaryPrimitives.ReadUInt32BigEndian(package.AsSpan(0x14));
        for (var i = 0; i < Math.Min(fileCount, 4096u); i++)
        {
            var entry = (long)tableOffset + 0x20 + i * 0x40L;
            if (entry + 0x40 > package.Length)
            {
                break;
            }

            var nameBytes = package.AsSpan((int)entry, 0x20);
            var nameLength = nameBytes.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(nameBytes[..(nameLength < 0 ? nameBytes.Length : nameLength)]);
            var offset = BinaryPrimitives.ReadUInt64BigEndian(package.AsSpan((int)entry + 0x20));
            var length = BinaryPrimitives.ReadUInt64BigEndian(package.AsSpan((int)entry + 0x28));
            if (offset <= (ulong)package.Length && length <= (ulong)package.Length - offset)
            {
                files[name] = ((int)offset, (int)length);
            }
        }

        return files;
    }

    private static JsonDocument? ReadJson(byte[] package, Dictionary<string, (int Offset, int Length)> files, string name) =>
        files.TryGetValue(name, out var file)
            ? JsonDocument.Parse(package.AsMemory(file.Offset, file.Length))
            : null;

    private static Trophy[] ReadTrophies(byte[] package)
    {
        var files = ReadContainer(package);
        using var conf = ReadJson(package, files, "tropconf.json");
        if (conf is null || !conf.RootElement.TryGetProperty("trophies", out var list))
        {
            return [];
        }

        var language = conf.RootElement.TryGetProperty("defaultLanguage", out var lang)
            ? lang.GetString() ?? "en-US"
            : "en-US";
        using var meta = ReadJson(package, files, "tropmeta_en-US.json") ?? ReadJson(package, files, $"tropmeta_{language}.json");
        var texts = new Dictionary<int, (string Name, string Detail)>();
        if (meta is not null &&
            meta.RootElement.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("trophyMetadata", out var trophyTexts))
        {
            foreach (var text in trophyTexts.EnumerateArray())
            {
                if (TryReadNumber(text, "id", out var id))
                {
                    texts[(int)id] = (
                        text.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                        text.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty);
                }
            }
        }

        var result = new SortedDictionary<int, Trophy>();
        foreach (var definition in list.EnumerateArray())
        {
            if (!TryReadNumber(definition, "id", out var idValue) ||
                !definition.TryGetProperty("grade", out var gradeElement) ||
                gradeElement.GetString() is not { Length: 1 } grade)
            {
                continue;
            }

            var gradeIndex = "PGSB".IndexOf(grade[0]);
            if (gradeIndex < 0)
            {
                continue;
            }

            var id = (int)idValue;
            ulong? statId = null;
            ulong? target = null;
            var comparison = Comparison.None;
            if (definition.TryGetProperty("unlockCondition", out var condition) &&
                condition.ValueKind == JsonValueKind.Object)
            {
                statId = TryReadNumber(condition, "udsStatId", out var stat) ? stat : null;
                target = TryReadNumber(condition, "targetValue", out var targetValue) ? targetValue : null;
                comparison = condition.TryGetProperty("comparator", out var comparator)
                    ? comparator.GetString() switch
                    {
                        "ge" => Comparison.GreaterEqual,
                        "gt" => Comparison.Greater,
                        "le" => Comparison.LessEqual,
                        "lt" => Comparison.Less,
                        _ => Comparison.None,
                    }
                    : Comparison.None;
            }

            texts.TryGetValue(id, out var trophyText);
            result[id] = new Trophy
            {
                Id = id,
                Grade = gradeIndex + 1,
                GroupId = TryReadNumber(definition, "groupId", out var group) ? (int)group : 0,
                Hidden = definition.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True,
                HasReward = definition.TryGetProperty("hasReward", out var reward) && reward.ValueKind == JsonValueKind.True,
                Name = trophyText.Name ?? string.Empty,
                Description = trophyText.Detail ?? string.Empty,
                StatId = statId,
                Target = target,
                Comparer = (int)comparison,
            };
        }

        return [.. result.Values];
    }

    // Only direct unsigned "latest value" stats are evaluated: the event property named by the
    // rule's input is the stat. Aggregated stats and filtered or nested extraction need event
    // history the emulator does not keep.
    private static Dictionary<string, List<StatRule>> ReadRules(byte[] package)
    {
        var rules = new Dictionary<string, List<StatRule>>(StringComparer.Ordinal);
        var files = ReadContainer(package);
        using var definitions = ReadJson(package, files, "stats_definition.json");
        using var extraction = ReadJson(package, files, "stats_extraction.json");
        if (definitions is null || extraction is null ||
            !definitions.RootElement.TryGetProperty("statDefinitionArray", out var statArray) ||
            !extraction.RootElement.TryGetProperty("statsExtractionRuleArray", out var ruleArray))
        {
            return rules;
        }

        var stats = new Dictionary<ulong, bool>();
        foreach (var definition in statArray.EnumerateArray())
        {
            if (!TryReadNumber(definition, "statId", out var id) ||
                !definition.TryGetProperty("aggregation", out var aggregation) || aggregation.GetString() != "latest" ||
                !definition.TryGetProperty("dataType", out var type) ||
                type.GetString() is not ("uint32" or "uint64"))
            {
                continue;
            }

            stats[id] = type.GetString() == "uint32";
        }

        foreach (var rule in ruleArray.EnumerateArray())
        {
            if (!rule.TryGetProperty("condition", out var condition) || condition.ValueKind != JsonValueKind.Object ||
                !condition.TryGetProperty("eventName", out var eventName) || eventName.GetString() is not { } name ||
                !rule.TryGetProperty("action", out var action) ||
                !action.TryGetProperty("input", out var input) || input.GetString() is not { } path ||
                !path.StartsWith("$.", StringComparison.Ordinal) || path.Length == 2 ||
                !action.TryGetProperty("output", out var output) ||
                !TryReadNumber(output, "statId", out var statId) ||
                !stats.TryGetValue(statId, out var isUInt32))
            {
                continue;
            }

            if (!rules.TryGetValue(name, out var forEvent))
            {
                rules[name] = forEvent = [];
            }

            forEvent.Add(new StatRule(statId, path[2..], isUInt32));
        }

        return rules;
    }

    private static bool TryReadNumber(JsonElement element, string property, out ulong value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var field))
        {
            return false;
        }

        return field.ValueKind == JsonValueKind.Number
            ? field.TryGetUInt64(out value)
            : ulong.TryParse(field.GetString(), out value);
    }
}
