// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed class CheckpointRing(int capacity = 1 << 20)
{
    internal sealed record Entry(ulong Sequence, uint Op, ulong SubmitId, uint Arg0, uint Arg1, uint Arg2, uint Arg3, ulong Arg4,
        ulong Shader0 = 0, ulong Shader1 = 0);

    private readonly Entry?[] _entries = new Entry?[ValidateCapacity(capacity)];
    private long _sequence;

    internal ulong NewestSequence => unchecked((ulong)Volatile.Read(ref _sequence));

    private static int ValidateCapacity(int capacity) => capacity > 0 && (capacity & (capacity - 1)) == 0
        ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    internal ulong Add(uint op, ulong submitId, uint arg0, uint arg1, uint arg2, uint arg3, ulong arg4, ulong shader0 = 0, ulong shader1 = 0)
    {
        var sequence = unchecked((ulong)Interlocked.Increment(ref _sequence));
        var entry = new Entry(sequence, op, submitId, arg0, arg1, arg2, arg3, arg4, shader0, shader1);
        ref var slot = ref _entries[(int)(sequence & (ulong)(_entries.Length - 1))];
        var previous = Volatile.Read(ref slot);
        // A delayed writer must not replace a newer generation of this slot.
        while (previous is null || previous.Sequence < sequence)
        {
            var observed = Interlocked.CompareExchange(ref slot, entry, previous);
            if (ReferenceEquals(observed, previous))
                break;
            previous = observed;
        }
        return sequence;
    }

    internal Entry? Decode(ulong sequence)
    {
        var entry = Volatile.Read(ref _entries[(int)(sequence & (ulong)(_entries.Length - 1))]);
        return entry?.Sequence == sequence ? entry : null;
    }

    internal string Describe(ulong sequence)
    {
        var entry = Decode(sequence);
        if (entry is null)
            return $"seq={sequence} evicted";
        var text = $"seq={sequence} op={(RecordedOperation)entry.Op} submit={entry.SubmitId} args={entry.Arg0},{entry.Arg1},{entry.Arg2},{entry.Arg3},0x{entry.Arg4:X16}";
        return entry.Shader0 == 0 && entry.Shader1 == 0 ? text : $"{text} draw_shaders=0x{entry.Shader0:X16},0x{entry.Shader1:X16}";
    }
}

internal static unsafe class GpuCheckpoints
{
    internal const string ExtensionName = "VK_NV_device_diagnostic_checkpoints";
    internal static readonly bool Requested = Environment.GetEnvironmentVariable("SHARPEMU_VK_CHECKPOINTS") == "1";
    internal static bool Enabled { get; private set; }
    internal static ulong NewestSequence => _ring?.NewestSequence ?? 0;
    private static delegate* unmanaged<CommandBuffer, void*, void> _setCheckpoint;
    private static delegate* unmanaged<Queue, uint*, CheckpointDataNV*, void> _getCheckpointData;
    private static CheckpointRing? _ring;
    private static (string Name, Queue Queue)[] _queues = [];
    private static int _dumped;
    // The vertex and pixel program hashes of the draw being recorded; zero outside draws.
    [ThreadStatic] private static ulong t_shader0;
    [ThreadStatic] private static ulong t_shader1;

    internal static void SetDrawShaders(ulong vertexHash, ulong pixelHash)
    {
        t_shader0 = vertexHash;
        t_shader1 = pixelHash;
    }

    internal static void Initialize(Vk vk, Device device, bool available, Queue main, Queue readback)
    {
        if (!Requested)
            return;
        try
        {
            if (available)
            {
                _setCheckpoint = (delegate* unmanaged<CommandBuffer, void*, void>)(nint)vk.GetDeviceProcAddr(device, "vkCmdSetCheckpointNV");
                _getCheckpointData = (delegate* unmanaged<Queue, uint*, CheckpointDataNV*, void>)(nint)vk.GetDeviceProcAddr(device, "vkGetQueueCheckpointDataNV");
                if (_setCheckpoint != null && _getCheckpointData != null)
                {
                    _ring = new CheckpointRing();
                    _queues = readback.Handle != 0 && readback.Handle != main.Handle
                        ? [("graphics/compute/present", main), ("readback/transfer", readback)]
                        : [("graphics/compute/present/readback", main)];
                    Enabled = true;
                }
            }
            Console.Error.WriteLine($"[GPU][INFO] checkpoints {(Enabled ? "active" : available ? "inactive (entry points unavailable)" : "inactive (extension unavailable)")}");
        }
        catch (Exception exception)
        {
            Enabled = false;
            try { Console.Error.WriteLine($"[GPU][INFO] checkpoints inactive: {exception.Message}"); } catch { }
        }
    }

    internal static void Record(nint buffer, uint op, ulong submitId, uint arg0, uint arg1, uint arg2, uint arg3, ulong arg4)
    {
        try
        {
            var sequence = _ring!.Add(op, submitId, arg0, arg1, arg2, arg3, arg4, t_shader0, t_shader1);
            _setCheckpoint(new CommandBuffer(buffer), (void*)(nuint)sequence);
        }
        catch { }
    }

    internal static void DumpOnce()
    {
        if (!Enabled || Interlocked.Exchange(ref _dumped, 1) != 0)
            return;
        try
        {
            Console.Error.WriteLine($"[GPU][ERROR] checkpoints newest_seq={_ring!.NewestSequence}");
            foreach (var (name, queue) in _queues)
            {
                try
                {
                    uint count = 0;
                    _getCheckpointData(queue, &count, null);
                    // Bound allocation even if a broken driver returns an invalid count.
                    if (count > 4096)
                        throw new InvalidOperationException($"Invalid checkpoint count {count}");
                    var entries = new CheckpointDataNV[count];
                    for (var index = 0; index < entries.Length; index++)
                        entries[index].SType = StructureType.CheckpointDataNV;
                    fixed (CheckpointDataNV* pointer = entries)
                    {
                        var capacity = count;
                        if (capacity != 0)
                            _getCheckpointData(queue, &count, pointer);
                        Console.Error.WriteLine($"[GPU][ERROR] checkpoints queue={name} count={count}");
                        ulong lowest = ulong.MaxValue, highest = 0;
                        for (var index = 0; index < Math.Min(count, capacity); index++)
                        {
                            var sequence = (ulong)(nuint)entries[index].PCheckpointMarker;
                            lowest = Math.Min(lowest, sequence);
                            highest = Math.Max(highest, sequence);
                            Console.Error.WriteLine($"[GPU][ERROR] checkpoint queue={name} stage={entries[index].Stage} {_ring.Describe(sequence)}");
                        }

                        // The hung command started after the last finished marker; list the
                        // recorded commands around the in-flight window.
                        if (highest != 0)
                        {
                            var first = lowest > 2 ? lowest - 2 : 1;
                            var last = Math.Min(highest + 8, first + 63);
                            for (var sequence = first; sequence <= last; sequence++)
                                Console.Error.WriteLine($"[GPU][ERROR] checkpoint_window queue={name} {_ring.Describe(sequence)}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[GPU][ERROR] checkpoints queue={name} unavailable: {exception.Message}");
                }
            }

            // SHARPEMU_DIAG_INDIRECT_ARGS records carry the checkpoint sequence they follow.
            IndirectArgsProbe.DumpOnce();
        }
        catch { }
    }
}
