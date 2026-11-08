// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;

internal sealed class ReplayOptions
{
    public const string KnownSlow = "54904FB419D79E49,DA4FF1222C09F131,FC6F8C56EB7E168F,1158E78FA2C7A20E,174082508ACF6307,40395313615ABCC8,6CC64DEE32DC7094";
    public bool Native, Smoke, ValidateOnly;
    public string? Dump, SelectionCsv;
    public string Rank = "spirv_bytes";
    public int Top, Repeats = 2;
    public readonly List<string> Positional = [];
    public readonly List<(string Name, string Directory)> Modes = [];
    public readonly HashSet<ulong> Hashes = [];
    public readonly HashSet<string> Ids = [];
    public bool Selected(string id, ulong hash) => Hashes.Count == 0 && Ids.Count == 0 || Hashes.Contains(hash) || Ids.Contains(id);

    public static ReplayOptions Parse(string[] args)
    {
        var o = new ReplayOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}");
            switch (args[i])
            {
                case "--native-bench": o.Native = true; break;
                case "--smoke": o.Smoke = true; break;
                case "--validate-only": o.ValidateOnly = true; break;
                case "--dump-spirv": o.Dump = Path.GetFullPath(Value()); break;
                case "--csv": o.SelectionCsv = Value(); break;
                case "--rank": o.Rank = Value(); break;
                case "--top": o.Top = int.Parse(Value()); break;
                case "--repeat": o.Repeats = int.Parse(Value()); break;
                case "--hash":
                    foreach (var hash in Value().Split(','))
                        o.Hashes.Add(ulong.Parse(hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hash[2..] : hash, NumberStyles.HexNumber));
                    break;
                case "--mode":
                    var parts = Value().Split('=', 2);
                    if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || parts[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                        throw new ArgumentException("Use --mode name=directory; names may contain letters, digits, dash and underscore");
                    o.Modes.Add((parts[0], Path.GetFullPath(parts[1]))); break;
                default:
                    if (args[i].StartsWith("--")) throw new ArgumentException($"Unknown option: {args[i]}");
                    o.Positional.Add(args[i]); break;
            }
        }
        if (o.Top < 0 || o.Repeats < 2 || o.Rank is not ("spirv_bytes" or "translation_ms"))
            throw new ArgumentException("--top must be nonnegative; --repeat >= 2; --rank spirv_bytes|translation_ms");
        if (o.Top > 0)
        {
            if (o.SelectionCsv is null) throw new ArgumentException("--top requires --csv <replay.csv>");
            var lines = File.ReadAllLines(o.SelectionCsv);
            var header = lines[0].Split(',');
            var column = Array.IndexOf(header, o.Rank);
            var idColumn = Array.IndexOf(header, "id");
            if (column < 0 || idColumn < 0) throw new InvalidDataException("Selection CSV lacks id/rank columns");
            foreach (var row in lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Split(','))
                         .OrderByDescending(r => double.Parse(r[column], CultureInfo.InvariantCulture)).Take(o.Top))
                o.Ids.Add(row[idColumn]);
        }
        if (o.Native && o.Hashes.Count == 0 && o.Ids.Count == 0)
            foreach (var hash in KnownSlow.Split(',')) o.Hashes.Add(ulong.Parse(hash, NumberStyles.HexNumber));
        if (o.Modes.Select(m => m.Name).Distinct().Count() != o.Modes.Count)
            throw new ArgumentException("Mode names must be unique");
        return o;
    }
}
