// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json;

internal static class SemanticCheck
{
    private sealed record Result(string Id, string Hash, string Kind, int LeftBytes, int RightBytes,
        int Passed, int? MismatchSeed, string? Skip, object? Left, object? Right);

    public static void Run(string[] args)
    {
        if (args.Length is < 4 or > 7) throw new ArgumentException("--semantic-check <left-dump> <right-dump> <output-prefix> [samples=16] [max-bytes=131072] [fragment-helper-mask-hex=0]");
        var samples = args.Length > 4 ? int.Parse(args[4]) : 16;
        var maxBytes = args.Length > 5 ? int.Parse(args[5]) : 131072;
        var helperMask = args.Length > 6 ? Convert.ToUInt32(args[6], 16) : 0u;
        if (samples < 1 || maxBytes < 1) throw new ArgumentException("positive sample and byte limits required");
        var left = JsonSerializer.Deserialize<DumpManifest>(File.ReadAllText(Path.Combine(args[1], "manifest.json")))!;
        var right = JsonSerializer.Deserialize<DumpManifest>(File.ReadAllText(Path.Combine(args[2], "manifest.json")))!;
        if (left.HostCapabilities != right.HostCapabilities) throw new InvalidDataException("Host profiles differ");
        var counterparts = right.Stages.ToDictionary(b => b.Id);
        if (!left.Stages.Select(b => b.Id).ToHashSet().SetEquals(counterparts.Keys)) throw new InvalidDataException("Stage sets differ");
        var results = new List<Result>();
        foreach (var a in left.Stages.OrderByDescending(b => b.Bytes))
        {
            var b = counterparts[a.Id];
            if (a.Hash != b.Hash || a.Kind != b.Kind) throw new InvalidDataException("Stage identities differ");
            if (a.Bytes > maxBytes || b.Bytes > maxBytes)
            { results.Add(new(a.Id, a.Hash, a.Kind, a.Bytes, b.Bytes, 0, null, "size limit", null, null)); continue; }
            var passed = 0;
            int? mismatch = null;
            string? skip = null;
            object? outputA = null, outputB = null;
            try
            {
                byte[] Read(string dir, Blob blob)
                {
                    var bytes = File.ReadAllBytes(Path.Combine(dir, blob.File));
                    if (Convert.ToHexString(SHA256.HashData(bytes)) != blob.Sha256) throw new InvalidDataException("SHA mismatch");
                    return bytes;
                }
                var stageHelpers = a.Kind == "Pixel" ? helperMask : 0u;
                var cpuA = new SpirvCpu(Read(args[1], a), stageHelpers);
                var cpuB = new SpirvCpu(Read(args[2], b), stageHelpers);
                for (var seed = 0; seed < samples; seed++)
                {
                    var x = cpuA.Execute(seed);
                    var y = cpuB.Execute(seed);
                    if (!x.SequenceEqual(y)) { mismatch = seed; outputA = x; outputB = y; break; }
                    passed++;
                }
            }
            catch (NotSupportedException e) { skip = e.Message; }
            catch (OverflowException) { skip = "index/address overflow"; }
            results.Add(new(a.Id, a.Hash, a.Kind, a.Bytes, b.Bytes, passed, mismatch, skip, outputA, outputB));
            if (results.Count % 100 == 0) Console.WriteLine($"Interpreted {results.Count} stages; mismatches {results.Count(r => r.MismatchSeed.HasValue)}");
        }
        var prefix = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(results, SpirvDump.Json));
        using var report = new StreamWriter(prefix + ".md");
        void Write(string text) { report.WriteLine(text); Console.WriteLine(text); }
        Write($"Bounded CPU interpretation, {samples} deterministic random inputs/stage; byte limit {maxBytes}.");
        Write($"Fragment helper mask 0x{helperMask:X8}; helpers excluded from ballots, retained for fine derivatives and quad operations. Other helper collectives reject the sample.");
        Write($"Stages {results.Count}; fully compared {results.Count(r => r.Passed == samples)}; matching samples {results.Sum(r => r.Passed)}; mismatches {results.Count(r => r.MismatchSeed.HasValue)}.");
        foreach (var group in results.GroupBy(r => r.Kind))
            Write($"{group.Key}: {group.Count(r => r.Passed == samples)}/{group.Count()} fully compared; {group.Count(r => r.MismatchSeed.HasValue)} mismatches.");
        foreach (var group in results.Where(r => r.Skip is not null).GroupBy(r => r.Skip).OrderByDescending(g => g.Count()))
            Write($"Skipped {group.Count()}: {group.Key}");
        foreach (var row in results.Where(r => r.MismatchSeed.HasValue)) Write($"MISMATCH {row.Id} {row.Hash} {row.Kind} seed={row.MismatchSeed}");
        Environment.ExitCode = results.Any(r => r.MismatchSeed.HasValue) ? 1 : 0;
    }
}
