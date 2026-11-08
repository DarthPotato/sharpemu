// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using System.Text;

// A deliberately bounded, single-invocation SPIR-V interpreter. Unsupported operations,
// observed undefined values, invalid indices and undefined arithmetic reject the sample; they
// never turn into zeroes or an equivalence claim. Subgroups use 32 actual interpreter
// lanes with identical control traces at collectives, never guessed participation.
// Textures are unsupported. Fine f32 derivatives and quad operations use four
// consecutive lanes per 2x2 quad. An optional fragment helper mask tests the
// permitted policy that excludes helpers from ballots but retains them for
// derivatives/quad operations. Other helper collectives reject the sample.
// External inputs use stable keys.
internal sealed class SpirvCpu
{
    private readonly record struct Instruction(ushort Op, uint[] A);
    private readonly record struct Pointer(uint Root, int Offset, uint Type, bool Uniform = true, ulong? Address = null);
    private readonly List<Instruction> code = [];
    private readonly Dictionary<uint, Instruction> types = [];
    private readonly Dictionary<uint, ulong[]> constants = [];
    private readonly Dictionary<uint, uint> resultTypes = [];
    private readonly Dictionary<uint, int> labels = [];
    private readonly Dictionary<uint, string> names = [];
    private readonly Dictionary<(uint, uint), uint> decorations = [];
    private readonly List<uint[]> globals = [];
    private readonly Dictionary<uint, int> functions = [];
    private uint entry;
    private readonly uint fragmentHelperMask;
    private sealed class Collective(int pc, ushort op, ulong trace, ulong[] value, ulong argument) : Exception
    {
        public int Pc = pc;
        public ushort Op = op;
        public ulong Trace = trace;
        public ulong[] Value = value;
        public ulong Argument = argument;
    }

    public SpirvCpu(byte[] source, uint fragmentHelperMask = 0)
    {
        this.fragmentHelperMask = fragmentHelperMask;
        var words = SpirvNonce.Read(source);
        var inFunction = false;
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var op = (ushort)words[i];
            var a = words.AsSpan(i + 1, (int)(words[i] >> 16) - 1).ToArray();
            if (op == 5) names[a[0]] = Encoding.UTF8.GetString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan(1))).TrimEnd('\0');
            if (op == 71) decorations[(a[0], a[1])] = a.Length > 2 ? a[2] : 0;
            if (op == 15) entry = a[1];
            if (op is >= 19 and <= 39) types[a[0]] = new(op, a);
            if (op == 54) { inFunction = true; functions[a[1]] = code.Count; }
            if (op == 59 && !inFunction) globals.Add(a);
            if (op == 248) labels[a[0]] = code.Count;
            if (op is 41 or 42 or 43 or 44 or 46)
            {
                resultTypes[a[1]] = a[0];
                constants[a[1]] = op switch
                {
                    41 => [1], 42 => [0],
                    43 => [a[2] | (a.Length > 3 ? (ulong)a[3] << 32 : 0)],
                    44 => a.Skip(2).SelectMany(id => constants[id]).ToArray(),
                    _ => new ulong[Size(a[0])],
                };
            }
            if (inFunction) code.Add(new(op, a));
            if (op == 56) inFunction = false;
        }
    }

    private int Size(uint type)
    {
        var (op, a) = types[type];
        return op switch
        {
            20 or 21 or 22 or 32 => 1,
            23 or 24 => checked(Size(a[1]) * (int)a[2]),
            28 => checked(Size(a[1]) * (int)constants[a[2]][0]),
            30 => a.Skip(1).Sum(Size),
            _ => throw new NotSupportedException($"type {op}"),
        };
    }
    private uint Scalar(uint type) => types[type].Op is 23 or 24 ? Scalar(types[type].A[1]) : type;
    private int Width(uint type) => types[Scalar(type)].Op == 20 ? 1 : (int)types[Scalar(type)].A[1];
    private string Key(uint root)
    {
        string Decor(uint kind) => decorations.TryGetValue((root, kind), out var value) ? value.ToString() : "-";
        // Include output index, and descriptor set/binding. Names are a fallback
        // only for interfaces without semantic decorations (e.g. push constants).
        var semantic = $"builtin={Decor(11)}:location={Decor(30)}:index={Decor(32)}:set={Decor(34)}:binding={Decor(33)}";
        return decorations.Keys.Any(k => k.Item1 == root && k.Item2 is 11 or 30 or 33)
            ? semantic : names.GetValueOrDefault(root, "anonymous");
    }
    private static uint Mix(string key, int component, int seed)
    {
        var h = unchecked(2166136261u ^ (uint)seed);
        foreach (var c in key) h = unchecked((h ^ c) * 16777619);
        h = unchecked((h ^ (uint)component) * 16777619);
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15;
        return h;
    }

    public SortedDictionary<string, string> Execute(int seed, int maxSteps = 100000)
    {
        if (fragmentHelperMask == 0 && !code.Any(i => i.Op is >= 333 and <= 366 or >= 207 and <= 215))
            return ExecuteLane(seed, 0, [], maxSteps);
        var history = new List<ulong[][]>();
        for (var round = 0; round < 64; round++)
        {
            var events = new Collective?[32];
            var outputs = new SortedDictionary<string, string>?[32];
            for (var lane = 0; lane < 32; lane++)
                try { outputs[lane] = ExecuteLane(seed, lane, history, maxSteps); }
                catch (Collective e) { events[lane] = e; }
            if (events.All(e => e is null))
            {
                var result = new SortedDictionary<string, string>();
                for (var lane = 0; lane < 32; lane++)
                {
                    if ((fragmentHelperMask & (1u << lane)) != 0) continue;
                    foreach (var (key, value) in outputs[lane]!) result[$"lane:{lane}:{key}"] = value;
                }
                return result;
            }
            var first = events[0];
            if (first is null || events.Any(e => e is null || e.Pc != first.Pc || e.Op != first.Op || e.Trace != first.Trace))
                throw new NotSupportedException("divergent collective/control trace");
            var ballot = events.Select((e, lane) => e!.Value[0] != 0 && (fragmentHelperMask & (1u << lane)) == 0 ? 1u << lane : 0).Aggregate(0u, (a, b) => a | b);
            var results = new ulong[32][];
            for (var lane = 0; lane < 32; lane++)
            {
                var e = events[lane]!;
                results[lane] = e.Op switch
                {
                    339 => [ballot, 0, 0, 0],
                    334 => [ballot == uint.MaxValue ? 1UL : 0], 335 => [ballot != 0 ? 1UL : 0],
                    336 => [events.All(x => x!.Value.SequenceEqual(first.Value)) ? 1UL : 0],
                    338 => first.Value,
                    337 or 345 when e.Argument < 32 => events[(int)e.Argument]!.Value,
                    365 when e.Argument < 4 => events[(lane & ~3) | (int)e.Argument]!.Value,
                    366 when e.Argument < 3 => events[lane ^ (e.Argument == 0 ? 1 : e.Argument == 1 ? 2 : 3)]!.Value,
                    210 or 211 => Difference(events, lane, e.Op == 210 ? 1 : 2),
                    _ => throw new NotSupportedException($"subgroup opcode {e.Op}"),
                };
            }
            history.Add(results);
        }
        throw new NotSupportedException("collective limit");
    }

    private static ulong[] Difference(Collective?[] events, int lane, int axis)
    {
        var low = events[lane & ~axis]!.Value;
        var high = events[lane | axis]!.Value;
        return low.Select((v, i) => (ulong)BitConverter.SingleToUInt32Bits(
            BitConverter.UInt32BitsToSingle((uint)high[i]) - BitConverter.UInt32BitsToSingle((uint)v))).ToArray();
    }

    private SortedDictionary<string, string> ExecuteLane(int seed, int lane, IReadOnlyList<ulong[][]> collectives, int maxSteps)
    {
        var values = new Dictionary<uint, ulong[]>(constants);
        var valueTypes = new Dictionary<uint, uint>(resultTypes);
        var pointers = new Dictionary<uint, Pointer>();
        var texelBuffers = new Dictionary<uint, uint>();
        var variables = new Dictionary<uint, (uint Type, uint Storage)>();
        var memory = new Dictionary<(uint Root, int Offset), ulong>();
        var writtenOutputs = new HashSet<(uint Root, int Offset)>();
        var uniform = constants.Keys.ToHashSet();
        var uniformMemory = new HashSet<(uint Root, int Offset)>();
        var physicalMemory = new Dictionary<ulong, byte>();
        var uniformControl = true;
        var undefinedValues = new HashSet<uint>();
        var undefinedLoad = false;
        var collectiveIndex = 0;
        ulong controlTrace = 14695981039346656037;
        void Trace(int at, uint target) => controlTrace = unchecked(((controlTrace ^ (uint)at) * 1099511628211) ^ target);
        ushort activeOpcode = 0;
        ulong[] V(uint id)
        {
            if (undefinedValues.Contains(id) && activeOpcode != 169) throw new NotSupportedException("observed uninitialized value");
            return values.TryGetValue(id, out var v) ? v : throw new NotSupportedException($"undefined ID {id} in opcode {activeOpcode}");
        }
        ulong U(uint id) => V(id)[0];
        void Store(Pointer p, ulong[] value, bool invariant = false)
        {
            if (p.Address.HasValue) throw new NotSupportedException("physical store");
            if (variables[p.Root].Storage is not (3 or 6 or 7)) throw new NotSupportedException("shared/external store");
            for (var i = 0; i < value.Length; i++)
            {
                memory[(p.Root, p.Offset + i)] = value[i];
                if (invariant) uniformMemory.Add((p.Root, p.Offset + i));
                else uniformMemory.Remove((p.Root, p.Offset + i));
                if (variables[p.Root].Storage == 3) writtenOutputs.Add((p.Root, p.Offset + i));
            }
        }
        ulong[] Load(Pointer p, uint type)
        {
            var result = new ulong[Size(type)];
            if (p.Address is { } address)
            {
                var bytes = Width(type) / 8;
                if (bytes is not (4 or 8)) throw new NotSupportedException("physical scalar width");
                if (address < 4096 || address > ulong.MaxValue - (ulong)(result.Length * bytes) || address % 4 != 0)
                    throw new NotSupportedException("outside synthetic physical heap");
                for (var i = 0; i < result.Length; i++)
                    for (var j = 0; j < bytes; j++)
                    {
                        var at = address + (ulong)(i * bytes + j);
                        if (!physicalMemory.TryGetValue(at, out var octet))
                        {
                            // Sparse, byte-addressed synthetic heap. Aligned 64-bit cells
                            // contain valid synthetic pointers, also usable as numeric data.
                            var wordAddress = at & ~3UL;
                            var word = (wordAddress & 4) != 0 ? 0u : 0x10000000u + (Mix("physical", (int)(wordAddress / 4), seed) % 0x100000) * 8;
                            octet = (byte)(word >> ((int)(at & 3) * 8));
                            physicalMemory[at] = octet;
                        }
                        result[i] |= (ulong)octet << (j * 8);
                    }
                return result;
            }
            var storage = variables[p.Root].Storage;
            for (var i = 0; i < result.Length; i++)
            {
                if (memory.TryGetValue((p.Root, p.Offset + i), out var value)) { result[i] = value; continue; }
                if (storage is not (1 or 2 or 9 or 12)) { undefinedLoad = true; continue; }
                // Struct/array leaves get their true scalar type through AccessChain.
                var scalar = types[Scalar(type)];
                if (scalar.Op is not (20 or 21 or 22)) throw new NotSupportedException("composite external load");
                var hash = Mix($"{storage}:{Key(p.Root)}", p.Offset + i, storage == 1 ? seed + lane * 7919 : seed);
                result[i] = scalar.Op == 22
                    ? BitConverter.SingleToUInt32Bits((hash % 8193) / 2048f - 2f)
                    : scalar.Op == 20 ? hash & 1u : Width(type) == 64 ? 0x10000000UL + (hash % 0x100000) * 8 : hash % 32;
                if (storage is 2 or 9 or 12 && scalar.Op == 21 && Width(type) == 32)
                    result[i] = ((p.Offset + i) & 1) == 0 ? 0x10000000UL + (hash % 0x100000) * 8 : 0;
                if (decorations.TryGetValue((p.Root, 11), out var builtin))
                {
                    if (builtin == 23) // HelperInvocation
                        result[i] = (fragmentHelperMask >> lane) & 1u;
                    var pixelX = (lane / 4 % 4) * 2 + lane % 2;
                    var pixelY = lane / 16 * 2 + lane / 2 % 2;
                    if (builtin == 15) // FragCoord; pixel centers, positive w.
                        result[i] = BitConverter.SingleToUInt32Bits(i == 0 ? (seed % 8) * 8 + pixelX + .5f : i == 1 ? (seed / 8 % 8) * 4 + pixelY + .5f : i == 3 ? 1f : .5f);
                    if (builtin == 18) result[i] = 0; // One sample at the pixel center.
                    if (builtin is 5286 or 5287)
                    {
                        // Triangle (0,0), (256,0), (0,256), with equal clip w.
                        var bx = ((seed % 8) * 8 + pixelX + .5f) / 256f;
                        var by = ((seed / 8 % 8) * 4 + pixelY + .5f) / 256f;
                        result[i] = BitConverter.SingleToUInt32Bits(i == 0 ? 1f - bx - by : i == 1 ? bx : by);
                    }
                    if (builtin is 29 or 41) result[i] = (uint)lane; // LocalInvocationIndex/SubgroupLocalInvocationId
                    if (builtin == 36) result[i] = 32; // SubgroupSize
                    if (builtin == 40) result[i] = 0; // SubgroupId
                }
                memory[(p.Root, p.Offset + i)] = result[i];
            }
            return result;
        }
        void Variable(uint[] a)
        {
            var type = types[a[0]].A[2];
            pointers[a[1]] = new(a[1], 0, type);
            variables[a[1]] = (type, a[2]);
            if (a.Length > 3) Store(pointers[a[1]], V(a[3]), uniform.Contains(a[3]));
        }
        foreach (var global in globals) Variable(global);
        var pc = functions[entry] + 1;
        uint block = 0, previous = 0;
        var killed = false;
        for (var steps = 0; ; steps++)
        {
            if (steps >= maxSteps) throw new NotSupportedException("step limit");
            var (op, a) = code[pc++];
            activeOpcode = op;
            switch (op)
            {
                case 0: case 246: case 247: continue;
                case 248: block = a[0]; continue;
                case 59: Variable(a); continue;
                case 62: Store(pointers[a[0]], V(a[1]), uniform.Contains(a[1])); continue;
                case 249: Trace(pc - 1, a[0]); previous = block; pc = labels[a[0]]; continue;
                case 250:
                    var destination = a[U(a[0]) != 0 ? 1 : 2];
                    Trace(pc - 1, destination); uniformControl &= uniform.Contains(a[0]); previous = block; pc = labels[destination]; continue;
                case 251:
                    uniformControl &= uniform.Contains(a[0]);
                    var target = a[1];
                    for (var j = 2; j < a.Length; j += 2) if (U(a[0]) == a[j]) target = a[j + 1];
                    Trace(pc - 1, target);
                    previous = block; pc = labels[target]; continue;
                case 252: killed = true; break;
                case 253: break;
                case 255: throw new NotSupportedException("unreachable executed");
                case 65:
                    var pointer = pointers[a[2]];
                    for (var j = 3; j < a.Length; j++)
                    {
                        var index = checked((int)U(a[j]));
                        var t = types[pointer.Type];
                        uint child;
                        int offset;
                        if (t.Op == 30)
                        {
                            if (index >= t.A.Length - 1) throw new NotSupportedException("struct index");
                            child = t.A[index + 1]; offset = t.A.Skip(1).Take(index).Sum(Size);
                        }
                        else if (t.Op is 23 or 24 or 28 or 29)
                        {
                            var count = t.Op == 28 ? checked((int)constants[t.A[2]][0]) : t.Op == 29 ? int.MaxValue : (int)t.A[2];
                            if (index < 0 || index >= count) throw new NotSupportedException("out of bounds access");
                            child = t.A[1]; offset = checked(index * Size(child));
                        }
                        else throw new NotSupportedException("access chain type");
                        pointer = pointer with { Offset = checked(pointer.Offset + offset), Type = child, Uniform = pointer.Uniform && uniform.Contains(a[j]) };
                    }
                    pointers[a[1]] = pointer; continue;
                case 120:
                    pointers[a[1]] = new(0, 0, types[a[0]].A[2], uniform.Contains(a[2]), U(a[2])); continue;
                case 67:
                    var basePointer = pointers[a[2]];
                    if (!basePointer.Address.HasValue || a.Length != 4) throw new NotSupportedException("nonphysical/aggregate PtrAccessChain");
                    pointers[a[1]] = basePointer with { Address = checked(basePointer.Address.Value + U(a[3]) * (ulong)(Size(basePointer.Type) * Width(basePointer.Type) / 8)), Uniform = basePointer.Uniform && uniform.Contains(a[3]) }; continue;
                case 68:
                    // All synthetic runtime descriptor buffers contain 1024 elements.
                    var structure = types[pointers[a[2]].Type];
                    if (structure.Op != 30 || types[structure.A[a[3] + 1]].Op != 29) throw new NotSupportedException("ArrayLength type");
                    values[a[1]] = [1024]; valueTypes[a[1]] = a[0]; continue;
                default:
                    var type = a[0]; var id = a[1];
                    uniform.Remove(id);
                    undefinedValues.Remove(id);
                    valueTypes[id] = type;
                    if (op == 61)
                    {
                        var p = pointers[a[2]];
                        if (types[type].Op == 25)
                        {
                            if (types[type].A[2] != 5 || names.GetValueOrDefault(p.Root) != "shaderData")
                                throw new NotSupportedException("texture/image load (only shaderData R32Uint texel buffer supported)");
                            texelBuffers[id] = p.Root; continue;
                        }
                        undefinedLoad = false;
                        values[id] = Load(p, type);
                        if (undefinedLoad) undefinedValues.Add(id);
                        if (p.Uniform && !p.Address.HasValue && Enumerable.Range(0, Size(type)).All(i => uniformMemory.Contains((p.Root, p.Offset + i)))) uniform.Add(id);
                        else uniform.Remove(id);
                        continue;
                    }
                    if (op == 104 && texelBuffers.ContainsKey(a[2])) { values[id] = [4096]; continue; }
                    if (op == 95 && texelBuffers.TryGetValue(a[2], out var texelRoot))
                    {
                        var index = U(a[3]);
                        if (index >= 4096) throw new NotSupportedException("outside synthetic shaderData texel buffer");
                        var word = (index & 1) == 0 ? 0x10000000u + (Mix(Key(texelRoot), (int)(index / 2), seed) % 0x100000) * 8 : 0;
                        // R32Uint format expansion is (r, 0, 0, 1).
                        values[id] = [word, 0, 0, 1]; continue;
                    }
                    if (op == 245)
                    {
                        // Phi reads must be simultaneous on loop backedges.
                        var pending = new List<(uint Id, uint Type, ulong[] Value)>();
                        pc--;
                        while (pc < code.Count && code[pc].Op == 245)
                        {
                            var phi = code[pc++].A;
                            var found = false;
                            for (var j = 2; j < phi.Length; j += 2)
                                if (phi[j + 1] == previous) { pending.Add((phi[1], phi[0], V(phi[j]).ToArray())); found = true; break; }
                            if (!found) throw new InvalidDataException("missing phi predecessor");
                        }
                        foreach (var item in pending) { values[item.Id] = item.Value; valueTypes[item.Id] = item.Type; uniform.Remove(item.Id); }
                        continue;
                    }
                    if (op is >= 333 and <= 366 or 210 or 211)
                    {
                        var derivative = op is 210 or 211;
                        if (derivative && (types[Scalar(type)].Op != 22 || Width(type) != 32))
                            throw new NotSupportedException("only f32 fine derivatives modeled");
                        if (!derivative && op is not (334 or 335 or 336 or 337 or 338 or 339 or 345 or 365 or 366)) throw new NotSupportedException($"subgroup opcode {op}");
                        if (!derivative && U(a[2]) != 3) throw new NotSupportedException("non-subgroup collective");
                        if (fragmentHelperMask != 0 && op is not (339 or 365 or 366 or 210 or 211))
                            throw new NotSupportedException("helper participation only modeled for ballot and quad/derivative operations");
                        if (collectiveIndex == collectives.Count)
                            throw new Collective(pc - 1, op, controlTrace, V(a[derivative ? 2 : 3]), !derivative && a.Length > 4 ? U(a[4]) : 0);
                        values[id] = collectives[collectiveIndex++][lane]; continue;
                    }
                    if (op == 80) { values[id] = a.Skip(2).SelectMany(V).ToArray(); if (a.Skip(2).All(uniform.Contains)) uniform.Add(id); continue; }
                    if (op == 81)
                    {
                        var sourceType = valueTypes[a[2]]; var offset = 0;
                        foreach (var index in a.Skip(3))
                        {
                            var t = types[sourceType];
                            if (t.Op == 30) { offset += t.A.Skip(1).Take((int)index).Sum(Size); sourceType = t.A[index + 1]; }
                            else { sourceType = t.A[1]; offset += checked((int)index * Size(sourceType)); }
                        }
                        values[id] = V(a[2]).AsSpan(offset, Size(type)).ToArray(); if (uniform.Contains(a[2])) uniform.Add(id); continue;
                    }
                    if (op == 79)
                    {
                        var concat = V(a[2]).Concat(V(a[3])).ToArray();
                        if (a.Skip(4).Any(x => x >= concat.Length)) throw new NotSupportedException("undefined shuffle component");
                        values[id] = a.Skip(4).Select(x => concat[x]).ToArray(); if (uniform.Contains(a[2]) && uniform.Contains(a[3])) uniform.Add(id); continue;
                    }
                    if (op == 83)
                    { values[id] = V(a[2]).ToArray(); if (uniform.Contains(a[2])) uniform.Add(id); continue; }
                    if (op == 124)
                    {
                        var fromWidth = Width(valueTypes[a[2]]); var toWidth = Width(type);
                        if (fromWidth * V(a[2]).Length != toWidth * Size(type)) throw new NotSupportedException("bitcast width mismatch");
                        BigInteger bits = 0;
                        for (var j = 0; j < V(a[2]).Length; j++) bits |= (BigInteger)V(a[2])[j] << (j * fromWidth);
                        var components = new ulong[Size(type)];
                        var componentMask = (BigInteger.One << toWidth) - 1;
                        for (var j = 0; j < components.Length; j++) components[j] = (ulong)((bits >> (j * toWidth)) & componentMask);
                        values[id] = components; if (uniform.Contains(a[2])) uniform.Add(id); continue;
                    }
                    if (op == 169)
                    {
                        if (undefinedValues.Contains(a[2])) throw new NotSupportedException("uninitialized select condition");
                        var condition = V(a[2]); var yes = V(a[3]); var no = V(a[4]);
                        values[id] = yes.Select((x, i) => condition[condition.Length == 1 ? 0 : i] != 0 ? x : no[i]).ToArray();
                        if (Enumerable.Range(0, yes.Length).Any(i => undefinedValues.Contains(condition[condition.Length == 1 ? 0 : i] != 0 ? a[3] : a[4]))) undefinedValues.Add(id);
                        if (uniform.Contains(a[2]) && (condition.Length == 1 ? uniform.Contains(condition[0] != 0 ? a[3] : a[4]) : uniform.Contains(a[3]) && uniform.Contains(a[4]))) uniform.Add(id);
                        continue;
                    }
                    if (op == 12)
                    {
                        if (a[3] is 76 or 77 or 78)
                        {
                            // Fully covered, single-sample synthetic triangle: centroid
                            // and sample 0 are at center. Other sample/offset modes reject.
                            if (a[3] == 77 && U(a[5]) != 0 || a[3] == 78 && V(a[5]).Any(v => (v & 0x7fffffff) != 0))
                                throw new NotSupportedException("noncenter interpolation");
                            var p = pointers[a[4]];
                            if (!decorations.TryGetValue((p.Root, 11), out var builtin) || builtin is not (5286 or 5287))
                                throw new NotSupportedException("non-barycentric interpolation");
                            values[id] = Load(p, type); continue;
                        }
                        values[id] = Ext(type, a, V); if (a.Skip(4).All(uniform.Contains)) uniform.Add(id); continue;
                    }
                    var left = V(a[2]);
                    if (op is 154 or 155) { values[id] = [op == 154 ? left.Any(x => x != 0) ? 1UL : 0 : left.All(x => x != 0) ? 1UL : 0]; continue; }
                    var right = a.Length > 3 ? V(a[3]) : left;
                    var width = Width(type);
                    var operandWidth = Width(valueTypes[a[2]]);
                    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
                    var result = new ulong[Size(type)];
                    for (var i = 0; i < result.Length; i++)
                    {
                        var x = left[i]; var y = right[right.Length == 1 ? 0 : i];
                        long Signed(ulong z) => operandWidth == 64 ? (long)z : unchecked((long)(z << (64 - operandWidth))) >> (64 - operandWidth);
                        ulong Bool(bool b) => b ? 1UL : 0UL;
                        float F(ulong z) => operandWidth == 16 ? (float)BitConverter.UInt16BitsToHalf((ushort)z) : BitConverter.UInt32BitsToSingle((uint)z);
                        ulong Float(float f) => width == 16 ? BitConverter.HalfToUInt16Bits((Half)f) : BitConverter.SingleToUInt32Bits(f);
                        ulong FieldMask(ulong count) => count == 64 ? ulong.MaxValue : (1UL << (int)count) - 1;
                        result[i] = op switch
                        {
                            113 => x, // UConvert
                            114 => unchecked((ulong)Signed(x)),
                            115 => Float(F(x)),
                            111 => Float(Signed(x)), 112 => Float(x),
                            109 when float.IsFinite(F(x)) && F(x) >= 0 && F(x) <= uint.MaxValue => (uint)F(x),
                            110 when float.IsFinite(F(x)) && F(x) >= int.MinValue && F(x) <= int.MaxValue => unchecked((uint)(int)F(x)),
                            126 => unchecked(0UL - x), 127 => Float(-F(x)),
                            128 => unchecked(x + y), 130 => unchecked(x - y), 132 => unchecked(x * y),
                            134 when y != 0 => x / y, 137 when y != 0 => x % y,
                            129 => Float(F(x) + F(y)), 131 => Float(F(x) - F(y)), 133 => Float(F(x) * F(y)), 136 => Float(F(x) / F(y)),
                            164 or 170 => Bool(x == y), 165 or 171 => Bool(x != y),
                            166 => Bool(x != 0 || y != 0), 167 => Bool(x != 0 && y != 0), 168 => Bool(x == 0),
                            172 => Bool(x > y), 173 => Bool(Signed(x) > Signed(y)), 174 => Bool(x >= y), 175 => Bool(Signed(x) >= Signed(y)),
                            176 => Bool(x < y), 177 => Bool(Signed(x) < Signed(y)), 178 => Bool(x <= y), 179 => Bool(Signed(x) <= Signed(y)),
                            180 => Bool(F(x) == F(y)), 182 => Bool(F(x) != F(y) && !float.IsNaN(F(x)) && !float.IsNaN(F(y))),
                            183 => Bool(F(x) != F(y)),
                            185 => Bool(float.IsNaN(F(x)) || float.IsNaN(F(y)) || F(x) < F(y)),
                            189 => Bool(float.IsNaN(F(x)) || float.IsNaN(F(y)) || F(x) <= F(y)),
                            184 => Bool(F(x) < F(y)), 186 => Bool(F(x) > F(y)), 188 => Bool(F(x) <= F(y)), 190 => Bool(F(x) >= F(y)),
                            194 when y < (ulong)operandWidth => x >> (int)y,
                            195 when y < (ulong)operandWidth => unchecked((ulong)(Signed(x) >> (int)y)),
                            196 when y < (ulong)operandWidth => x << (int)y,
                            197 => x | y, 198 => x ^ y, 199 => x & y, 200 => ~x,
                            201 when U(a[4]) + U(a[5]) <= (ulong)operandWidth =>
                                (x & ~(FieldMask(U(a[5])) << (int)U(a[4]))) | ((y & FieldMask(U(a[5]))) << (int)U(a[4])),
                            203 when y + U(a[4]) <= (ulong)operandWidth => U(a[4]) == 0 ? 0 : (x >> (int)y) & (U(a[4]) == 64 ? ulong.MaxValue : (1UL << (int)U(a[4])) - 1),
                            205 => (ulong)BitOperations.PopCount(x),
                            156 => Bool(float.IsNaN(F(x))), 157 => Bool(float.IsInfinity(F(x))),
                            _ => throw new NotSupportedException($"opcode {op} (or undefined operands)"),
                        } & mask;
                    }
                    values[id] = result;
                    if (a.Skip(2).All(uniform.Contains)) uniform.Add(id); else uniform.Remove(id);
                    continue;
            }
            break;
        }
        var outputs = new SortedDictionary<string, string> { ["killed"] = killed.ToString() };
        if (!killed)
            foreach (var (root, offset) in writtenOutputs)
                outputs[$"{Key(root)}[{offset}]"] = memory[(root, offset)].ToString("X16");
        return outputs;
    }

    private ulong[] Ext(uint type, uint[] a, Func<uint, ulong[]> value)
    {
        var op = a[3];
        var x = value(a[4]);
        if (op is 38 or 39 or 41 or 42 or 44 or 45)
        {
            var yInt = value(a[5]);
            var zInt = a.Length > 6 ? value(a[6]) : x;
            return x.Select((v, i) => op switch
            {
                38 => Math.Min(v, yInt[i]), 41 => Math.Max(v, yInt[i]), 44 => Math.Clamp(v, yInt[i], zInt[i]),
                39 => unchecked((uint)Math.Min((int)(uint)v, (int)(uint)yInt[i])),
                42 => unchecked((uint)Math.Max((int)(uint)v, (int)(uint)yInt[i])),
                _ => unchecked((uint)Math.Clamp((int)(uint)v, (int)(uint)yInt[i], (int)(uint)zInt[i])),
            }).ToArray();
        }
        if (op == 62) // UnpackHalf2x16
            return [BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf((ushort)x[0])),
                BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf((ushort)(x[0] >> 16)))];
        if (op == 58) // PackHalf2x16
            return [(uint)BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle((uint)x[0])) |
                (uint)BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle((uint)x[1])) << 16];
        if (Width(type) != 32) throw new NotSupportedException($"ext width {Width(type)}");
        var y = a.Length > 5 ? value(a[5]) : x;
        var z = a.Length > 6 ? value(a[6]) : x;
        return x.Select((bits, i) =>
        {
            var f = BitConverter.UInt32BitsToSingle((uint)bits);
            var g = BitConverter.UInt32BitsToSingle((uint)y[i]);
            var h = BitConverter.UInt32BitsToSingle((uint)z[i]);
            var result = op switch
            {
                1 => MathF.Round(f, MidpointRounding.AwayFromZero), 2 => MathF.Round(f, MidpointRounding.ToEven),
                3 => MathF.Truncate(f), 4 => MathF.Abs(f), 8 => MathF.Floor(f), 9 => MathF.Ceiling(f), 10 => f - MathF.Floor(f),
                13 => MathF.Sin(f), 14 => MathF.Cos(f), 26 => MathF.Pow(f, g), 27 => MathF.Exp(f), 28 => MathF.Log(f),
                29 => MathF.Pow(2, f), 30 => MathF.Log2(f), 31 => MathF.Sqrt(f), 32 => 1f / MathF.Sqrt(f),
                37 or 79 => MathF.Min(f, g), 40 or 80 => MathF.Max(f, g), 43 or 81 => Math.Clamp(f, g, h),
                50 => MathF.FusedMultiplyAdd(f, g, h),
                _ => throw new NotSupportedException($"GLSL ext {op}"),
            };
            return (ulong)BitConverter.SingleToUInt32Bits(result);
        }).ToArray();
    }
}
