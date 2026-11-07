// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// Stage failures are unique permutations; dependent counts are pipeline attempts. A pipeline
// with two failed stages counts once in dependent_pipelines and once against each stage.
internal sealed class ShaderPrewarmStatistics
{
    private readonly Action<string> _log;
    internal ShaderPrewarmStatistics() : this(Console.Error.WriteLine) { }
    internal ShaderPrewarmStatistics(Action<string> log) => _log = log;
    private readonly object _gate = new();
    private readonly int[] _compiled = new int[2];
    private readonly int[] _missing = new int[2];
    private readonly int[] _dependent = new int[2];
    private readonly HashSet<(bool Compute, string Kind, string Key)> _missingRecords = [];
    private readonly Dictionary<(bool Compute, ulong Identity), string> _stages = [];
    private readonly Dictionary<(bool Compute, string Reason), int> _reasons = [];
    private readonly HashSet<string> _loggedReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<(bool Compute, ulong Identity), int> _dependents = [];
    private readonly Dictionary<(bool Compute, Result Result), int> _native = [];
    private readonly Dictionary<(bool Compute, string Reason), int> _other = [];
    private static readonly Regex ShaderLocation = new(@"\b(?:hash|shader|pc)=0x[0-9A-Fa-f]+\s*", RegexOptions.Compiled);
    private static readonly Regex InstructionLocation = new(@"@0x[0-9A-Fa-f]+", RegexOptions.Compiled);
    private static readonly Regex ValueIdentity = new(@"#\d+", RegexOptions.Compiled);

    private static int Index(bool compute) => compute ? 0 : 1;
    internal void Compiled(bool compute) { lock (_gate) _compiled[Index(compute)]++; }
    internal void MissingPipelines(bool compute, int count) { lock (_gate) _missing[Index(compute)] += count; }
    internal void MissingRecord(bool compute, string kind, string key)
    {
        lock (_gate) _missingRecords.Add((compute, kind, key));
    }

    internal void StageFailure(bool compute, ulong identity, string stage, ulong hash, ulong address, string error)
    {
        // The outer resource-plan message contains per-shader addresses; group by its underlying reason.
        var marker = error.IndexOf(" error=", StringComparison.Ordinal);
        var reason = marker < 0 ? error : error[(marker + 7)..];
        reason = ValueIdentity.Replace(InstructionLocation.Replace(ShaderLocation.Replace(reason, ""), "@<pc>"), "#<id>");
        lock (_gate)
        {
            if (!_stages.TryAdd((compute, identity), $"{stage}:0x{hash:X16}:0x{identity:X16}")) return;
            var key = (compute, reason);
            _reasons[key] = _reasons.GetValueOrDefault(key) + 1;
            if (_loggedReasons.Add(reason))
                _log($"[LOADER][WARN] Shader prewarm stage failed: {stage}=0x{hash:X16} shader=0x{address:X16} identity=0x{identity:X16} error={error}");
        }
    }

    internal void DependentFailure(bool compute, params ulong[] stages)
    {
        lock (_gate)
        {
            _dependent[Index(compute)]++;
            foreach (var stage in stages.Distinct())
            {
                var key = (compute, stage);
                _dependents[key] = _dependents.GetValueOrDefault(key) + 1;
            }
        }
    }

    internal void PipelineFailure(bool compute, Result? nativeResult, string error)
    {
        lock (_gate)
        {
            if (nativeResult is { } result && result != Result.Success)
                _native[(compute, result)] = _native.GetValueOrDefault((compute, result)) + 1;
            else
                _other[(compute, error)] = _other.GetValueOrDefault((compute, error)) + 1;
        }
    }

    internal string Summary()
    {
        lock (_gate)
        {
            string Kind(bool compute) => compute ? "compute" : "graphics";
            string Counts(int[] values) => $"compute={values[0]},graphics={values[1]}";
            string Map(IEnumerable<(string Key, int Count)> items) => JsonSerializer.Serialize(
                items.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Count));
            return $"compiled=[{Counts(_compiled)}] missing_pipelines=[{Counts(_missing)}] " +
                $"missing_records={Map(_missingRecords.GroupBy(item => $"{Kind(item.Compute)}:{item.Kind}").Select(group => (group.Key, group.Count())))} " +
                $"unique_stage_failures=[compute={_stages.Keys.Count(key => key.Compute)},graphics={_stages.Keys.Count(key => !key.Compute)}] " +
                $"stage_reasons={Map(_reasons.Select(pair => ($"{Kind(pair.Key.Compute)}:{pair.Key.Reason}", pair.Value)))} " +
                $"dependent_pipelines=[{Counts(_dependent)}] " +
                $"dependents_by_stage={Map(_dependents.Select(pair => ($"{Kind(pair.Key.Compute)}:{_stages[pair.Key]}", pair.Value)))} " +
                $"native_pipeline_failures={Map(_native.Select(pair => ($"{Kind(pair.Key.Compute)}:{pair.Key.Result}", pair.Value)))} " +
                $"other_pipeline_failures={Map(_other.Select(pair => ($"{Kind(pair.Key.Compute)}:{pair.Key.Reason}", pair.Value)))}";
        }
    }
}
