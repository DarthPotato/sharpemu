// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed record BenchInput(Blob Metadata, string Path);
internal sealed record BenchJob(string Mode, string Id, uint PushBytes, BenchInput[] Stages);
internal sealed record WorkerJob(BenchJob Job, string Nonce);
internal sealed record WorkerResult(double Milliseconds, string Device);
internal sealed record BenchSample(string Mode, string Id, string Hashes, int Repeat, string Phase, string Nonce, int Bytes, double Ms);

internal static class NativeBench
{
    private static void RequireIdle()
    {
        var processes = Process.GetProcessesByName("SharpEmu");
        try
        {
            if (processes.Length != 0) throw new InvalidOperationException("SharpEmu.exe is running; native benchmark skipped. Wait for the game run to finish.");
        }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    public static void Worker(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Internal worker expects job/result paths");
        RequireIdle();
        var job = JsonSerializer.Deserialize<WorkerJob>(File.ReadAllText(args[1]))!;
        var shaders = job.Job.Stages.Select(s => (s.Metadata, SpirvNonce.Inject(File.ReadAllBytes(s.Path), job.Nonce))).ToArray();
        using var native = new NativeDevice();
        var ms = native.Compile(shaders, job.Job.PushBytes);
        File.WriteAllText(args[2], JsonSerializer.Serialize(new WorkerResult(ms, native.Description)));
    }

    public static void Run(ReplayOptions options)
    {
        if (options.Positional.Count != 1 || options.Modes.Count == 0)
            throw new ArgumentException("--native-bench <output-prefix> --mode name=dump-directory [--mode name=directory ...]");
        if (!options.ValidateOnly) RequireIdle();
        var prefix = Path.GetFullPath(options.Positional[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        var modeJobs = new List<Dictionary<string, BenchJob>>();
        foreach (var mode in options.Modes)
        {
            var manifest = JsonSerializer.Deserialize<DumpManifest>(File.ReadAllText(Path.Combine(mode.Directory, "manifest.json")))!;
            if (manifest.Schema != 1) throw new InvalidDataException("Unsupported dump schema");
            var blobs = manifest.Stages.ToDictionary(s => s.Id);
            var jobs = new Dictionary<string, BenchJob>();
            BenchInput Input(Blob blob)
            {
                var path = Path.GetFullPath(Path.Combine(mode.Directory, blob.File));
                if (!path.StartsWith(mode.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Blob path leaves dump directory");
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length != blob.Bytes || Convert.ToHexString(SHA256.HashData(bytes)) != blob.Sha256)
                    throw new InvalidDataException($"Blob size/SHA mismatch: {path}");
                SpirvReflection.Reflect(bytes);
                return new(blob, path);
            }
            bool Selected(Blob b) => options.Selected(b.Id, ulong.Parse(b.Hash, NumberStyles.HexNumber));
            foreach (var blob in blobs.Values.Where(b => b.Kind == "Compute" && Selected(b)))
                jobs.Add(blob.Id, new(mode.Name, blob.Id, manifest.PushConstantBytes, [Input(blob)]));
            foreach (var pair in manifest.Graphics.DistinctBy(p => p.Id))
            {
                if (!blobs.TryGetValue(pair.Vertex, out var vs) || pair.Pixel is not null && !blobs.ContainsKey(pair.Pixel))
                    throw new InvalidDataException($"Incomplete graphics pair: {pair.Id}");
                var ps = pair.Pixel is null ? null : blobs[pair.Pixel];
                if (!Selected(vs) && !(ps is not null && Selected(ps))) continue;
                if (vs.Kind != "Vertex")
                {
                    Console.Error.WriteLine($"Excluded {mode.Name}/{pair.Id}: {vs.Kind} (mesh/tessellation not supported by minimal graphics state)");
                    continue;
                }
                jobs.Add(pair.Id, new(mode.Name, pair.Id, manifest.PushConstantBytes, ps is null ? [Input(vs)] : [Input(vs), Input(ps)]));
            }
            modeJobs.Add(jobs);
        }
        var ids = modeJobs[0].Keys.Order(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) throw new InvalidDataException("No benchmark jobs selected");
        foreach (var jobs in modeJobs.Skip(1))
        {
            if (!jobs.Keys.ToHashSet().SetEquals(ids))
                throw new InvalidDataException("Modes have different selected variants; regenerate dumps with the same recording and selection");
            foreach (var id in ids)
                if (!jobs[id].Stages.Select(s => (s.Metadata.Id, s.Metadata.Hash, s.Metadata.Kind)).SequenceEqual(
                        modeJobs[0][id].Stages.Select(s => (s.Metadata.Id, s.Metadata.Hash, s.Metadata.Kind))))
                    throw new InvalidDataException($"Variant mismatch across modes: {id}");
        }
        if (options.Smoke)
        {
            if (options.Modes.Count != 1) throw new ArgumentException("Smoke test takes one mode only");
            ids = ids.Where(id => modeJobs[0][id].Stages.Length == 1 && modeJobs[0][id].Stages[0].Metadata.Kind == "Compute")
                .OrderBy(id => modeJobs[0][id].Stages[0].Metadata.Bytes).Take(2).ToArray();
            if (ids.Length != 2 || ids.Any(id => modeJobs[0][id].Stages[0].Metadata.Bytes > 65536))
                throw new ArgumentException("Smoke test requires two compute shaders <=64 KiB; select small hashes explicitly");
            options.Repeats = 2;
        }
        if (options.ValidateOnly)
        {
            File.WriteAllText(prefix + ".plan.json", JsonSerializer.Serialize(modeJobs.SelectMany(j => ids.Select(id => j[id])), SpirvDump.Json));
            Console.WriteLine($"Validated {ids.Length} variants across {modeJobs.Count} modes; no Vulkan instance/device created. Plan: {prefix}.plan.json");
            return;
        }
        var scratch = Path.Combine(Path.GetDirectoryName(prefix)!, "native-workers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var csv = new StreamWriter(prefix + ".csv");
        csv.WriteLine("mode,id,hashes,repeat,phase,nonce,spirv_bytes,native_ms");
        var samples = new List<BenchSample>();
        string? device = null;
        // Rotate mode order for each variant and reverse it on alternate repeats.
        // Each call is a NEW PROCESS/device, so a same-nonce hit cannot be just a
        // surviving pipeline object or the device's in-memory compiler cache.
        for (var repeat = 0; repeat < options.Repeats; repeat++)
            for (var variant = 0; variant < ids.Length; variant++)
                for (var slot = 0; slot < modeJobs.Count; slot++)
                {
                    var index = (variant + repeat / 2 + (repeat % 2 == 0 ? slot : modeJobs.Count - 1 - slot)) % modeJobs.Count;
                    var job = modeJobs[index][ids[variant]];
                    var nonce = Guid.NewGuid().ToString("N");
                    foreach (var phase in new[] { "fresh-nonce", "same-nonce" })
                    {
                        RequireIdle();
                        var jobPath = Path.Combine(scratch, "job.json");
                        var resultPath = Path.Combine(scratch, "result.json");
                        File.WriteAllText(jobPath, JsonSerializer.Serialize(new WorkerJob(job, nonce)));
                        if (File.Exists(resultPath)) File.Delete(resultPath);
                        var worker = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                            worker.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                        worker.ArgumentList.Add("--native-worker"); worker.ArgumentList.Add(jobPath); worker.ArgumentList.Add(resultPath);
                        Console.WriteLine($"{job.Mode}/{job.Id} repeat={repeat + 1} {phase}: compiling...");
                        using var process = Process.Start(worker) ?? throw new IOException("Cannot launch native worker");
                        process.WaitForExit();
                        if (process.ExitCode != 0 || !File.Exists(resultPath))
                            throw new InvalidOperationException($"Native worker failed ({process.ExitCode}); partial CSV retained at {prefix}.csv");
                        var result = JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(resultPath))!;
                        device ??= result.Device;
                        if (device != result.Device) throw new InvalidOperationException("Device profile changed between workers");
                        var hashes = string.Join('+', job.Stages.Select(s => s.Metadata.Hash));
                        var sample = new BenchSample(job.Mode, job.Id, hashes, repeat + 1, phase, nonce, job.Stages.Sum(s => s.Metadata.Bytes), result.Milliseconds);
                        samples.Add(sample);
                        csv.WriteLine($"{sample.Mode},{sample.Id},{hashes},{sample.Repeat},{phase},{nonce},{sample.Bytes},{sample.Ms:F6}");
                        csv.Flush();
                        Console.WriteLine($"  {sample.Ms:F3} ms");
                    }
                }
        File.WriteAllText(prefix + ".device.json", device);
        WriteSummary(prefix, samples, device!);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private static void WriteSummary(string prefix, List<BenchSample> samples, string device)
    {
        using var report = new StreamWriter(prefix + ".md");
        void Write(string s) { Console.WriteLine(s); report.WriteLine(s); }
        Write("Native pipeline compilation; no VkPipelineCache; each observation uses a new process/device. No commands submitted.");
        Write("Fresh-nonce times are cold candidates. Debug nonces may be ignored by a driver; fast same-nonce + slow fresh-nonce evidence is required. This does not prove a cache miss via a driver API.");
        Write("\n| Mode | Variant | Hash(es) | SPIR-V bytes | Fresh median ms | Same median ms | Fresh/same | Cache probe |\n|---|---|---|---:|---:|---:|---:|---|");
        var aggregates = new List<(string Mode, double Fresh, double Same, int Bytes, bool Supported)>();
        foreach (var group in samples.GroupBy(s => (s.Mode, s.Id)))
        {
            var fresh = Median(group.Where(s => s.Phase == "fresh-nonce").Select(s => s.Ms));
            var same = Median(group.Where(s => s.Phase == "same-nonce").Select(s => s.Ms));
            var supported = group.GroupBy(s => s.Repeat).All(repeat =>
            {
                var firstCompile = repeat.Single(s => s.Phase == "fresh-nonce").Ms;
                var sameCompile = repeat.Single(s => s.Phase == "same-nonce").Ms;
                return firstCompile >= 10 && firstCompile >= sameCompile * 2;
            });
            var first = group.First();
            Write($"| {first.Mode} | {first.Id} | {first.Hashes} | {first.Bytes} | {fresh:F3} | {same:F3} | {fresh / Math.Max(same, .000001):F2} | {(supported ? "supports cold/hit distinction" : "INCONCLUSIVE; do not claim cold")} |");
            aggregates.Add((first.Mode, fresh, same, first.Bytes, supported));
        }
        Write("\nPer-mode totals sum per-variant medians; median column gives the median across variants (not weighted by runtime frequency).\n");
        Write("| Mode | Variants | SPIR-V bytes | Sum fresh ms | Median variant fresh ms | Sum same ms | Inconclusive variants |\n|---|---:|---:|---:|---:|---:|---:|");
        foreach (var mode in aggregates.GroupBy(a => a.Mode))
            Write($"| {mode.Key} | {mode.Count()} | {mode.Sum(a => (long)a.Bytes)} | {mode.Sum(a => a.Fresh):F3} | {Median(mode.Select(a => a.Fresh)):F3} | {mode.Sum(a => a.Same):F3} | {mode.Count(a => !a.Supported)} |");
        Write("\nOnly vkCreateComputePipelines/vkCreateGraphicsPipelines is timed. Module creation, reflection, layouts, process/device setup and nonce insertion are excluded. Raw CSV retains all repeats/nonces. Graphics uses triangle-list, one sample, numeric output formats and disabled depth/blend; its timings are not the game's render-state timings.");
        report.WriteLine("\nDevice profile:\n```json\n" + device + "\n```");
    }
}
