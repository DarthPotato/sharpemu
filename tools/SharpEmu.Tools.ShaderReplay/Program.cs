// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length != 2)
    throw new ArgumentException("Usage: ShaderReplay <recording-directory> <output-prefix>");

// Open is allowed to truncate corrupt tails. Always isolate it from the caller's recordings.
var scratch = Path.Combine(Path.GetTempPath(), "SharpEmuShaderReplay", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
foreach (var name in new[] { "shader-prewarm.bin", "shader-prewarm-graphics.bin" })
    File.Copy(Path.Combine(args[0], name), Path.Combine(scratch, name));
var linked = Path.Combine(args[0], "vulkan-pipeline-cache.bin.linked", "callees.bin");
if (File.Exists(linked))
{
    Directory.CreateDirectory(Path.Combine(scratch, "vulkan-pipeline-cache.bin.linked"));
    File.Copy(linked, Path.Combine(scratch, "vulkan-pipeline-cache.bin.linked", "callees.bin"));
}
Environment.SetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH", Path.Combine(scratch, "vulkan-pipeline-cache.bin"));
var prefix = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
var compiler = new CaptureCompiler();
var host = new Rtx3080Host();
var rows = new List<Row>();
var failures = new List<string>();
var warmed = new HashSet<ShaderStage>();
Console.WriteLine($"Host: {ShaderPrewarmHost.CapabilityStamp(host)}");
using var list = ShaderPrewarmList.Open(scratch) ?? throw new IOException("Cannot load recordings");
using var csv = new StreamWriter(prefix + ".csv");
csv.WriteLine("id,hash,kind,guest_bytes,blocks,translation_ms,spirv_bytes,sha256,allocated_bytes");

void Measure(string id, uint codeBytes, Action capture)
{
    compiler.Requests.Clear();
    try
    {
        capture();
        var request = compiler.Requests.Single();
        // Untimed stage-specific JIT warmup. Run with DOTNET_TieredCompilation=0 for comparisons.
        if (warmed.Add(request.Stage))
            for (var i = 0; i < 3; i++)
                if (!Gen5SpirvTranslator.TryCompileProgram(request, out _, out var warmError))
                    throw new InvalidOperationException(warmError);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var success = Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error);
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (!success) throw new InvalidOperationException(error);
        var blocks = IrControlFlowGraph.Build(request.Program.Instructions, new Gen5IrBranchResolver()).Blocks.Count;
        var row = new Row(id, request.Hash, request.Stage, codeBytes, blocks, ms,
            shader.Spirv.Length, Convert.ToHexString(SHA256.HashData(shader.Spirv)), allocated);
        rows.Add(row);
        csv.WriteLine(row.Csv());
        csv.Flush();
        if (rows.Count % 100 == 0) Console.WriteLine($"Translated {rows.Count}, total {rows.Sum(r => r.Ms):F1} ms");
    }
    catch (Exception exception)
    {
        failures.Add($"{id}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

var computes = list.LoadedComputes((kind, key) => failures.Add($"Missing {kind}: {key}"));
for (var i = 0; i < computes.Count; i++)
{
    var (record, code) = computes[i];
    Measure($"compute-{i:D5}", code.CodeSize, () =>
    {
        if (!ShaderProgramCache.TryCompilePrewarm(record, code, compiler, host, out _, out _, out var error))
            throw new InvalidOperationException(error);
    });
}
var graphics = list.LoadedGraphics((kind, key) => failures.Add($"Missing {kind}: {key}"));
foreach (var stage in graphics.SelectMany(item => item.Pixel is { } pixel
             ? new[] { item.Vertex, pixel } : new[] { item.Vertex }).DistinctBy(stage => stage.Identity))
{
    Measure($"graphics-{stage.Identity:X16}", stage.Code.CodeSize, () =>
    {
        var record = stage.Record;
        if (stage.Code.FrontendDomain != 0) throw new NotSupportedException("Non-Gen5 frontend recording");
        var source = new ShaderSource(record.Registered, record.Hash, new uint[record.UserDataCount], record.UserDataBase, record.Stage);
        var programs = new ShaderProgramCache(stage.Code.CreateContext(), compiler, host);
        programs.CompilePrewarmStage(source, record.Options, record.Specialization, record.PushDataCursor, record.RuntimeDescriptors);
    });
}

var ordered = rows.OrderBy(row => row.Ms).ToArray();
using var summary = new StreamWriter(prefix + ".md");
void Write(string value) { summary.WriteLine(value); Console.WriteLine(value); }
Write($"Recorded computes: {computes.Count}; graphics pipelines: {graphics.Count}; translated stages: {rows.Count}; failures: {failures.Count}.");
Write($"Host: `{ShaderPrewarmHost.CapabilityStamp(host)}`");
Write($"Translation total {rows.Sum(r => r.Ms):F3} ms; avg {rows.Average(r => r.Ms):F3}; p95 {ordered[(int)Math.Ceiling(ordered.Length * .95) - 1].Ms:F3}; max {ordered[^1].Ms:F3}.");
Write($"SPIR-V total {rows.Sum(r => (long)r.Bytes)} bytes; max {rows.Max(r => r.Bytes)}; allocated {rows.Sum(r => r.Allocated)} bytes.");
foreach (var (title, top) in new[] { ("Slowest", rows.OrderByDescending(r => r.Ms).Take(20)), ("Largest", rows.OrderByDescending(r => r.Bytes).Take(20)) })
{
    Write($"\n{title} (id, hash, kind, guest bytes, blocks, ms, SPIR-V bytes, SHA-256, allocated bytes):\n");
    foreach (var row in top) Write(row.Csv());
}
foreach (var failure in failures) Write(failure);
// Retain the small isolated inputs for audit; no generated shader binaries are persisted.
Write($"\nIsolated recordings: {scratch}");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

record Row(string Id, ulong Hash, ShaderStage Kind, uint GuestBytes, int Blocks, double Ms, int Bytes, string Sha, long Allocated)
{
    public string Csv() => $"{Id},{Hash:X16},{Kind},{GuestBytes},{Blocks},{Ms:F6},{Bytes},{Sha},{Allocated}";
}
