// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Vulkan;

// Diagnostic: SHARPEMU_DIAG_INDIRECT_ARGS=1 (every indexed indirect draw) or =<vertex shader hash>.
// Before each matching draw the GPU copies the five arguments it is about to read into a
// host-visible ring. Completed copies are checked for implausible counts; on device loss the
// newest records are printed with the checkpoint sequence recorded just before each draw.
internal static unsafe class IndirectArgsProbe
{
    private const int Capacity = 4096;
    private const int RecordBytes = 32;
    private const uint ArgumentBytes = 20;
    private const int MaxAnomalies = 200;

    internal static readonly bool Enabled;
    private static readonly ulong _vertexHashFilter;

    private readonly record struct Meta(long Draw, ulong Checkpoint, ulong GuestAddress, uint IndexRange, ulong VertexHash, ulong SubmitTick);

    private static GpuBuffer? _ring;
    private static readonly Meta[] _meta = new Meta[Capacity];
    // A slot stays busy from its copy until the completion check of its tick has read it.
    private static readonly bool[] _busy = new bool[Capacity];
    private static long _next;
    private static long _skipped;
    private static int _anomalies;
    private static long _checked;
    private static uint _maxInstances;
    private static ulong _maxIndexEnd;
    private static int _dumped;

    static IndirectArgsProbe()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_DIAG_INDIRECT_ARGS");
        if (string.IsNullOrEmpty(value) || value == "0")
        {
            return;
        }

        Enabled = true;
        if (value != "1" && ulong.TryParse(value.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                System.Globalization.NumberStyles.HexNumber, null, out var hash))
        {
            _vertexHashFilter = hash;
        }

        Console.Error.WriteLine($"[GPU][INFO] indirect args probe active filter=0x{_vertexHashFilter:X16}");
    }

    internal static bool Wants(ulong vertexHash) => Enabled && (_vertexHashFilter == 0 || vertexHash == _vertexHashFilter);

    // The caller has ended rendering: the copy is a transfer command.
    internal static void Record(Vk vk, GpuDeviceInfo device, SubmissionScheduler scheduler, CommandBuffer command,
        VkBuffer source, ulong offset, ulong guestAddress, uint indexRange, ulong vertexHash)
    {
        _ring ??= new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, Capacity * RecordBytes);
        var slot = (int)(_next % Capacity);
        if (_busy[slot])
        {
            // The ring is full of copies the GPU may still be writing; skip rather than overwrite.
            _skipped++;
            return;
        }

        var draw = _next++;
        _busy[slot] = true;
        var bytes = (ulong)slot * RecordBytes;
        // A copy that never ran reads back as all ones.
        _ring.Mapped.Slice((int)bytes, RecordBytes).Fill(0xFF);
        if (!_ring.IsCoherent)
        {
            _ring.Flush(bytes, RecordBytes);
        }

        _meta[slot] = new Meta(draw, GpuCheckpoints.NewestSequence, guestAddress, indexRange, vertexHash, scheduler.CurrentTick);
        var region = new BufferCopy(offset, bytes, ArgumentBytes);
        vk.CmdCopyBuffer(command, source, _ring.Handle, 1, &region);
        scheduler.QueueCompletionAction(() => Check(slot));
    }

    private static uint[] Read(int slot)
    {
        var bytes = (ulong)slot * RecordBytes;
        if (!_ring!.IsCoherent)
        {
            _ring.Invalidate(bytes, RecordBytes);
        }

        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_ring.Mapped.Slice((int)bytes, (int)ArgumentBytes));
        return words.ToArray();
    }

    private static void Check(int slot)
    {
        var meta = _meta[slot];
        var words = Read(slot);
        _busy[slot] = false;
        var indexCount = words[0];
        var instances = words[1];
        var firstIndex = words[2];
        var indexEnd = (ulong)firstIndex + indexCount;
        _checked++;
        _maxInstances = Math.Max(_maxInstances, instances);
        _maxIndexEnd = Math.Max(_maxIndexEnd, indexEnd);
        if ((instances > 4096 || indexEnd > meta.IndexRange) && _anomalies < MaxAnomalies)
        {
            _anomalies++;
            Console.Error.WriteLine($"[GPU][ERROR] INDIRECT_ARGS_ODD {Describe(meta, words)}");
        }

        if (_checked % 100_000 == 0)
        {
            Console.Error.WriteLine($"[GPU][INFO] INDIRECT_ARGS_STATS checked={_checked} max_instances={_maxInstances} max_index_end={_maxIndexEnd} anomalies={_anomalies} skipped={_skipped}");
        }
    }

    private static string Describe(in Meta meta, uint[] words) =>
        $"draw={meta.Draw} checkpoint={meta.Checkpoint} tick={meta.SubmitTick} vs=0x{meta.VertexHash:X16} args_addr=0x{meta.GuestAddress:X16} " +
        $"index_range={meta.IndexRange} index_count={words[0]} instances={words[1]} first_index={words[2]} vertex_offset={(int)words[3]} first_instance={words[4]}";

    internal static void DumpOnce()
    {
        if (!Enabled || _ring is null || Interlocked.Exchange(ref _dumped, 1) != 0)
        {
            return;
        }

        try
        {
            var newest = Volatile.Read(ref _next) - 1;
            Console.Error.WriteLine($"[GPU][ERROR] indirect_args newest_draw={newest} checked={_checked} max_instances={_maxInstances} max_index_end={_maxIndexEnd} anomalies={_anomalies} skipped={_skipped}");
            for (var draw = Math.Max(0, newest - 47); draw <= newest; draw++)
            {
                var slot = (int)(draw % Capacity);
                var meta = _meta[slot];
                if (meta.Draw != draw)
                {
                    continue;
                }

                var words = Read(slot);
                var copied = !(words[0] == uint.MaxValue && words[1] == uint.MaxValue && words[2] == uint.MaxValue);
                Console.Error.WriteLine($"[GPU][ERROR] indirect_args copied={copied} {Describe(meta, words)}");
            }
        }
        catch (Exception exception)
        {
            try { Console.Error.WriteLine($"[GPU][ERROR] indirect_args dump failed: {exception.Message}"); } catch { }
        }
    }
}
