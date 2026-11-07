// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;

namespace SharpEmu.ShaderCompiler.Vulkan;

public sealed partial class SpirvModuleBuilder
{
    // Immutable roots make a branch snapshot independent of the number of expressions
    // accumulated so far. Keep insertion order separately: hash-tree enumeration would
    // change join order, IDs and the serialized module.
    private readonly record struct ExpressionValue(uint Value, int Position);

    private sealed record ExpressionSnapshot(
        ImmutableDictionary<InstructionKey, ExpressionValue> Values, ImmutableList<InstructionKey> Order,
        ExpressionSnapshot? Parent, int Depth, InstructionKey[] Changes)
    {
        public static readonly ExpressionSnapshot Empty = new(
            ImmutableDictionary<InstructionKey, ExpressionValue>.Empty, ImmutableList<InstructionKey>.Empty, null, 0, []);

        public bool TryGetValue(InstructionKey key, out uint value)
        {
            var found = Values.TryGetValue(key, out var entry);
            value = entry.Value;
            return found;
        }
    }

    private sealed class SharedExpressions
    {
        private ImmutableDictionary<InstructionKey, ExpressionValue>.Builder _values = ImmutableDictionary.CreateBuilder<InstructionKey, ExpressionValue>();
        private ImmutableList<InstructionKey>.Builder _order = ImmutableList.CreateBuilder<InstructionKey>();
        private ExpressionSnapshot _parent = ExpressionSnapshot.Empty;
        private readonly HashSet<InstructionKey> _changes = [];
        private readonly Dictionary<InstructionKey, uint> _laneDependent = [];
        private readonly Dictionary<InstructionKey, uint> _local = [];
        public bool SharingEnabled { get; set; } = true;

        public uint this[InstructionKey key]
        {
            set
            {
                if (!SharingEnabled)
                {
                    _local[key] = value;
                    return;
                }
                if (IsSubgroupOperation(key.Opcode))
                {
                    _laneDependent[key] = value;
                    return;
                }
                var found = _values.TryGetValue(key, out var previous);
                if (found && previous.Value == value) return;
                var position = found ? previous.Position : _order.Count;
                if (!found) _order.Add(key);
                _values[key] = new(value, position);
                _changes.Add(key);
            }
        }

        public bool TryGetValue(InstructionKey key, out uint value)
        {
            if (!SharingEnabled) return _local.TryGetValue(key, out value);
            if (IsSubgroupOperation(key.Opcode)) return _laneDependent.TryGetValue(key, out value);
            var found = _values.TryGetValue(key, out var entry);
            value = entry.Value;
            return found;
        }

        public ExpressionSnapshot Snapshot()
        {
            if (_changes.Count != 0)
            {
                _parent = new(_values.ToImmutable(), _order.ToImmutable(), _parent, _parent.Depth + 1, _changes.ToArray());
                _changes.Clear();
            }
            return _parent;
        }
        public void Restore(ExpressionSnapshot snapshot)
        {
            _laneDependent.Clear();
            _values = snapshot.Values.ToBuilder();
            _order = snapshot.Order.ToBuilder();
            _parent = snapshot;
            _changes.Clear();
        }

        // A key unchanged on both paths from the common ancestor is identical at
        // the join. Inspect only the union of path changes; unrelated inherited
        // expressions retain the first predecessor's logical enumeration order.
        public void Intersect(List<(uint Block, BlockState State)> incoming)
        {
            var first = incoming[0].State.Expressions;
            Restore(first);
            var candidates = new HashSet<InstructionKey>();
            for (var index = 1; index < incoming.Count; index++)
            {
                var left = first;
                var right = incoming[index].State.Expressions;
                while (!ReferenceEquals(left, right))
                {
                    if (left.Depth >= right.Depth)
                    {
                        candidates.UnionWith(left.Changes);
                        left = left.Parent!;
                    }
                    else
                    {
                        candidates.UnionWith(right.Changes);
                        right = right.Parent!;
                    }
                }
            }
            foreach (var key in candidates)
            {
                if (!first.TryGetValue(key, out var value)) continue;
                for (var index = 1; index < incoming.Count; index++)
                {
                    if (!incoming[index].State.Expressions.TryGetValue(key, out var other) || other != value)
                    {
                        _values.Remove(key);
                        _changes.Add(key);
                        break;
                    }
                }
            }
        }
        public void Clear()
        {
            if (!SharingEnabled)
            {
                _local.Clear();
                return;
            }
            _laneDependent.Clear();
            _values = ImmutableDictionary.CreateBuilder<InstructionKey, ExpressionValue>();
            _order = ImmutableList.CreateBuilder<InstructionKey>();
            _parent = ExpressionSnapshot.Empty;
            _changes.Clear();
        }
    }

    // The order of removals matters: Dictionary reuses freed slots in LIFO order,
    // and its later enumeration determines phi IDs. Track slots along with active
    // ownership so indexed invalidation visits exactly the old enumeration order.
    private readonly Dictionary<uint, SortedDictionary<int, uint>> _elementValueOwners = [];
    private readonly Dictionary<uint, HashSet<uint>> _elementStoreOwners = [];
    private readonly Dictionary<uint, int> _valueSlots = [];
    private readonly Stack<int> _freeValueSlots = [];
    private int _nextValueSlot;

    private void SetVariableValue(uint pointer, uint value)
    {
        if (!_variableValues.ContainsKey(pointer))
        {
            var slot = _freeValueSlots.TryPop(out var free) ? free : _nextValueSlot++;
            _valueSlots.Add(pointer, slot);
            if (_elementBases.TryGetValue(pointer, out var owner))
            {
                if (!_elementValueOwners.TryGetValue(owner, out var elements))
                    _elementValueOwners.Add(owner, elements = []);
                elements.Add(slot, pointer);
            }
        }
        _variableValues[pointer] = value;
    }

    private void RemoveVariableValue(uint pointer)
    {
        if (!_variableValues.Remove(pointer)) return;
        var slot = _valueSlots[pointer];
        _valueSlots.Remove(pointer);
        _freeValueSlots.Push(slot);
        if (_elementBases.TryGetValue(pointer, out var owner) && _elementValueOwners.TryGetValue(owner, out var elements))
            elements.Remove(slot);
    }

    private void SetPendingStore(uint pointer, int offset)
    {
        _pendingStores[pointer] = offset;
        if (_elementBases.TryGetValue(pointer, out var owner))
        {
            if (!_elementStoreOwners.TryGetValue(owner, out var elements))
                _elementStoreOwners.Add(owner, elements = []);
            elements.Add(pointer);
        }
    }

    private void RemovePendingStore(uint pointer)
    {
        _pendingStores.Remove(pointer);
        if (_elementBases.TryGetValue(pointer, out var owner) && _elementStoreOwners.TryGetValue(owner, out var elements))
            elements.Remove(pointer);
    }

    private void ForgetOwnedElements(uint variable, bool keepValues)
    {
        if (_elementStoreOwners.Remove(variable, out var stores))
            foreach (var element in stores) _pendingStores.Remove(element);
        if (!keepValues && _elementValueOwners.Remove(variable, out var values))
            foreach (var element in values.Values) RemoveVariableValue(element);
    }

    private void ResetOwnership()
    {
        _elementValueOwners.Clear();
        _elementStoreOwners.Clear();
        _valueSlots.Clear();
        _freeValueSlots.Clear();
        _nextValueSlot = 0;
    }
}
