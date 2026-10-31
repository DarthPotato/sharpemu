// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.VideoOut;

internal static class ReuseProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_REUSE_PROBE") == "1";
    private static long _flips;
    internal static void Flip() => Interlocked.Increment(ref _flips);
    internal static long Flips => Volatile.Read(ref _flips);
}

internal readonly record struct ReuseKey(ulong Low, ulong High);

// Four slots: current plus three completed windows. Owned solely by the render thread.
internal sealed class ReuseWindowTable
{
    private readonly ReuseKey[][] _keys;
    private readonly bool[][] _used;
    private int _current;
    internal long Items, Unique, Previous, History, Repeats, Overflow;
    internal ReuseWindowTable(int capacity = 32768)
    {
        if (capacity < 1 || !BitOperations.IsPow2(capacity)) throw new ArgumentOutOfRangeException(nameof(capacity));
        _keys = new ReuseKey[4][]; _used = new bool[4][];
        for (var i = 0; i < 4; i++) { _keys[i] = new ReuseKey[capacity]; _used[i] = new bool[capacity]; }
    }
    private int Find(int w, ReuseKey key)
    {
        var mask = _keys[w].Length - 1;
        var slot = (int)(key.Low & (uint)mask);
        for (var n = 0; n < Math.Min(mask + 1, 64); n++, slot = (slot + 1) & mask)
            if (!_used[w][slot] || _keys[w][slot] == key) return slot;
        return -1;
    }
    internal bool Add(ReuseKey key)
    {
        Items++;
        var hit = false;
        for (var age = 1; age <= 3; age++)
        {
            var w = (_current + 4 - age) % 4;
            var s = Find(w, key);
            if (s >= 0 && _used[w][s]) { if (age == 1) Previous++; hit = true; }
        }
        if (hit) History++;
        var slot = Find(_current, key);
        if (slot < 0) Overflow++;
        else if (_used[_current][slot]) Repeats++;
        else { _used[_current][slot] = true; _keys[_current][slot] = key; Unique++; }
        return hit;
    }
    internal void Next()
    {
        _current = (_current + 1) % 4; Array.Clear(_used[_current]);
        Items = Unique = Previous = History = Repeats = Overflow = 0;
    }
}

// Explicit scalar serialization avoids padding; two independent multiply/rotate streams.
internal struct ReuseHash
{
    private ulong _a, _b;
    internal int Words;
    internal void Add(ulong word)
    {
        _a = BitOperations.RotateLeft((_a ^ word) * 0x9E3779B185EBCA87UL, 27);
        _b = BitOperations.RotateLeft((_b + word + 0xC2B2AE3D27D4EB4FUL) * 0x165667B19E3779F9UL, 31);
        Words++;
    }
    private static ulong Mix(ulong x) { x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; return x ^ (x >> 33); }
    internal ReuseKey Key => new(Mix(_a ^ (ulong)Words), Mix(_b ^ (ulong)Words));
}

internal sealed class ReuseCollisionSample
{
    private readonly ReuseKey[] _keys = new ReuseKey[64];
    private readonly ulong[][] _words = Enumerable.Range(0, 64).Select(_ => new ulong[8192]).ToArray();
    private readonly int[] _lengths = new int[64];
    private int _count, _next;
    internal long Compared, Collisions, Oversize;
    internal void Check(ReuseKey key, ReadOnlySpan<ulong> words)
    {
        if (words.Length > 8192) { Oversize++; return; }
        for (var i = 0; i < _count; i++)
            if (_keys[i] == key) { Compared++; if (!_words[i].AsSpan(0, _lengths[i]).SequenceEqual(words)) Collisions++; }
        _keys[_next] = key; _lengths[_next] = words.Length; words.CopyTo(_words[_next]);
        _next = (_next + 1) % 64; _count = Math.Min(64, _count + 1);
    }
}

internal sealed class ReuseProbeState
{
    private readonly ReuseWindowTable[] _tables = Enumerable.Range(0, 4).Select(_ => new ReuseWindowTable()).ToArray();
    private readonly long[,] _totals = new long[4, 6];
    private readonly ReuseCollisionSample _samples = new();
    private readonly ulong[] _exact = new ulong[8192];
    private readonly object?[] _identities = new object[8192];
    private readonly ulong[] _shaders = new ulong[4096];
    private readonly long[] _shaderItems = new long[4096], _shaderHits = new long[4096];
    private long _epoch = ReuseProbe.Flips, _report = Stopwatch.GetTimestamp(), _windows, _items, _bytes, _ticks, _timed, _identityOverflow, _shaderOverflow;
    private bool _sample;
    private ReuseHash _hash;
    internal ReuseWindowTable Table(bool compute, bool payload) => _tables[(compute ? 2 : 0) + (payload ? 1 : 0)];
    private long _draws, _dispatches;
    internal void BeginItem(bool compute)
    {
        Sync();
        if (compute) _dispatches++; else _draws++;
    }
    private void Add(ulong word) { if (_sample && _hash.Words < _exact.Length) _exact[_hash.Words] = word; _hash.Add(word); }
    private void Words(uint[][] groups)
    {
        Add((ulong)groups.Length);
        foreach (var words in groups) { Add((ulong)words.Length); foreach (var word in words) Add(word); }
    }
    private ulong Identity(object value)
    {
        var slot = RuntimeHelpers.GetHashCode(value) & 8191;
        for (var n = 0; n < 8192; n++, slot = (slot + 1) & 8191)
        {
            if (_identities[slot] is null) _identities[slot] = value;
            if (ReferenceEquals(_identities[slot], value)) return (ulong)slot + 1;
        }
        _identityOverflow++; return 0;
    }
    private void FinishSample()
    {
        if (!_sample) return;
        if (_hash.Words > _exact.Length) _samples.Oversize++;
        else _samples.Check(_hash.Key, _exact.AsSpan(0, _hash.Words));
    }
    internal void Record(ShaderStageResources stage, ReadOnlySpan<uint> payload)
        => Record<object>(stage, payload, null);

    internal void Record<T>(ShaderStageResources stage, ReadOnlySpan<uint> payload, RuntimeDescriptorRegistry<T>? runtime) where T : class
    {
        _sample = (_items++ & 255) == 0;
        var started = _sample ? Stopwatch.GetTimestamp() : 0;
        var program = stage.Program!;
        var identity = Identity(program); var layout = Identity(program.Bindings!);
        if (identity == 0 || layout == 0) return;
        _hash = default;
        Add(program.Hash); Add(identity); Add(layout); Add((ulong)program.Stage);
        Words(stage.Resources.Buffers); Words(stage.Resources.Images); Words(stage.Resources.Samplers);
        Add((ulong)stage.Resources.DeviceAddressRanges.Length);
        foreach (var r in stage.Resources.DeviceAddressRanges)
        { Add(r.Handle); Add(r.Base); Add(r.Size); Add(r.Planned ? 1UL : 0); Add(r.Written ? 1UL : 0); }
        Add((ulong)(runtime?.Images.Count ?? 0));
        if (runtime is not null)
            foreach (var entry in runtime.Images)
            { Add((ulong)entry.Value.Key.Length); foreach (var word in entry.Value.Key) Add(word); }
        Add((ulong)(runtime?.SamplerCount ?? 0));
        if (runtime is not null)
            foreach (var entry in runtime.SamplerEntries)
            { Add((ulong)entry.Value.Key.Length); foreach (var word in entry.Value.Key) Add(word); }
        var kind = program.Stage == ShaderStageKind.Compute ? 2 : 0;
        var hit = _tables[kind].Add(_hash.Key);
        FinishSample(); _bytes += _hash.Words * 8L;
        _hash = default; Add(identity); Add((ulong)payload.Length);
        foreach (var word in payload) Add(word);
        Add((ulong)stage.Resources.FlattenedResourceTable.Length);
        foreach (var word in stage.Resources.FlattenedResourceTable) Add(word);
        _tables[kind + 1].Add(_hash.Key); FinishSample(); _bytes += _hash.Words * 8L;
        var slot = (int)(program.Hash & 4095); var found = false;
        for (var n = 0; n < 4096; n++, slot = (slot + 1) & 4095)
            if (_shaderItems[slot] == 0 || _shaders[slot] == program.Hash)
            { _shaders[slot] = program.Hash; _shaderItems[slot]++; if (hit) _shaderHits[slot]++; found = true; break; }
        if (!found) _shaderOverflow++;
        if (_sample) { _ticks += Stopwatch.GetTimestamp() - started; _timed++; }
    }
    internal void Sync()
    {
        var epoch = ReuseProbe.Flips;
        if (epoch == _epoch) return;
        while (_epoch < epoch)
        {
            for (var i = 0; i < 4; i++)
            {
                var t = _tables[i];
                _totals[i, 0] += t.Items; _totals[i, 1] += t.Unique; _totals[i, 2] += t.Previous;
                _totals[i, 3] += t.History; _totals[i, 4] += t.Repeats; _totals[i, 5] += t.Overflow;
                t.Next();
            }
            _epoch++; _windows++;
        }
        var now = Stopwatch.GetTimestamp();
        if (now - _report < 5 * Stopwatch.Frequency || _windows == 0) return;
        var line = new StringBuilder(FormattableString.Invariant($"[PERF][REUSE_PROBE] windows={_windows} draw_items={(double)_draws / _windows:F2} dispatch_items={(double)_dispatches / _windows:F2} key_unit=stage bound=unavailable"));
        string[] names = ["draw_recipe", "draw_payload", "dispatch_recipe", "dispatch_payload"];
        string[] fields = ["items", "unique", "prev", "last3", "repeat", "overflow"];
        for (var i = 0; i < 4; i++) for (var f = 0; f < 6; f++)
            line.Append(FormattableString.Invariant($" {names[i]}_{fields[f]}={(double)_totals[i, f] / _windows:F2}"));
        line.Append(FormattableString.Invariant($" bytes_per_window={(double)_bytes / _windows:F0} probe_est_ms_per_window={(_timed == 0 ? 0 : (double)_ticks / _timed * _items / Stopwatch.Frequency * 1000 / _windows):F3} timed={_timed} collision_comparisons={_samples.Compared} collisions={_samples.Collisions} sample_oversize={_samples.Oversize} identity_overflow={_identityOverflow} shader_overflow={_shaderOverflow} top="));
        foreach (var slot in Enumerable.Range(0, 4096).Where(i => _shaderItems[i] != 0).OrderByDescending(i => _shaderItems[i]).Take(10))
            line.Append(FormattableString.Invariant($"{_shaders[slot]:X16}:{_shaderItems[slot]}:{(double)_shaderHits[slot] / _shaderItems[slot]:F4},"));
        Console.WriteLine(line);
        Array.Clear(_totals); Array.Clear(_shaderItems); Array.Clear(_shaderHits);
        _windows = _items = _bytes = _ticks = _timed = _identityOverflow = _shaderOverflow = 0; _report = now;
        _draws = _dispatches = 0;
    }
}
