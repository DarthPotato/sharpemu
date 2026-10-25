// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

internal static unsafe partial class VulkanVideoPresenter
{
    internal enum ConcurrentQuery : byte
    {
        DirtyBytes,
        CleanWord,
        Resident,
        ResidentClean,
    }

    internal sealed class ConcurrentAssumptions(long bufferVersion, long writebackEpoch)
    {
        public readonly long BufferVersion = bufferVersion;
        public readonly long WritebackEpoch = writebackEpoch;
        public readonly List<(ConcurrentQuery Query, ulong Address, ulong Size, bool Answer)> Answers = new();
        public bool ReadsImageOwnership;
        // GpuWritebackEpoch.HistoryFloor at the start (render profile diagnostics only).
        public long HistoryFloor;

        public void Record(ConcurrentQuery query, ulong address, ulong size, bool answer)
        {
            ReadsImageOwnership |= query is ConcurrentQuery.CleanWord or ConcurrentQuery.ResidentClean;
            for (var index = Answers.Count - 1; index >= Math.Max(0, Answers.Count - 8); index--)
            {
                var previous = Answers[index];
                if (previous.Query != query || previous.Answer != answer)
                    continue;
                if (previous.Address == address && previous.Size == size)
                    return;
                // No owned bytes in either range proves the union, but an owned byte does not.
                if (!answer && size <= ulong.MaxValue - address && previous.Size <= ulong.MaxValue - previous.Address &&
                    address <= previous.Address + previous.Size && previous.Address <= address + size)
                {
                    var start = Math.Min(address, previous.Address);
                    var end = Math.Max(address + size, previous.Address + previous.Size);
                    Answers[index] = (query, start, end - start, false);
                    return;
                }
            }

            Answers.Add((query, address, size, answer));
        }
    }

    // Why taken resolutions were stale, for the render profile report (render thread only).
    private static long _staleByWriteback;
    private static long _staleByAnswer;

    // Writeback-stale resolutions by whether a store overlapped a range they read through the recorded
    // readers (other reads, such as shader code, are not recorded): overlapping, disjoint, unknown.
    private static readonly long[] _staleWritebackCauses = new long[3];

    internal static string TakeStaleReport()
    {
        var line = $"[PERF][PROGRAM_PREFETCH_STALE] writeback_epoch={_staleByWriteback} ownership_answer={_staleByAnswer} " +
            $"writeback_overlapping={_staleWritebackCauses[0]} writeback_disjoint={_staleWritebackCauses[1]} writeback_unknown={_staleWritebackCauses[2]}";
        _staleByWriteback = 0;
        _staleByAnswer = 0;
        Array.Clear(_staleWritebackCauses);
        return line;
    }

    private sealed partial class Presenter : IConcurrentResolveHost
    {
        // This partial serves the guest readers of program resolution on a thread resolving draws
        // ahead of the render thread. That thread never synchronizes GPU-owned memory: a word the
        // GPU owns abandons the resolution, which the render thread then does itself. Every
        // ownership answer it relied on is recorded and checked again when the result is taken.

        // Set only on the resolving thread, around one resolution.
        [ThreadStatic]
        private static ConcurrentAssumptions? _concurrentAssumptions;

        void IConcurrentResolveHost.BeginConcurrentResolve()
        {
            // The floor first: a store still writing when the epoch is read started at or after it.
            var historyFloor = GpuWritebackEpoch.HistoryFloor();
            _concurrentAssumptions = new ConcurrentAssumptions(_bufferCache.GpuModifiedVersion, GpuWritebackEpoch.Value)
            {
                HistoryFloor = historyFloor,
            };
        }

        object IConcurrentResolveHost.EndConcurrentResolve()
        {
            var assumptions = _concurrentAssumptions ?? throw new InvalidOperationException("No concurrent resolution is open.");
            _concurrentAssumptions = null;
            return assumptions;
        }

        // GPU data stored into guest memory since the resolution may have changed what it read;
        // otherwise the answers stand while the ownership they came from has not changed.
        bool IConcurrentResolveHost.IsStillCurrent(object assumptionsObject)
        {
            var assumptions = (ConcurrentAssumptions)assumptionsObject;
            if (!GpuWritebackEpoch.Unchanged(assumptions.WritebackEpoch))
            {
                _staleByWriteback++;
                if (GpuWritebackEpoch.RecordsHistory)
                {
                    var cause = GpuWritebackEpoch.ClassifyStores(assumptions.HistoryFloor, (address, size) =>
                    {
                        foreach (var answer in assumptions.Answers)
                        {
                            if (address < answer.Address + answer.Size && answer.Address < address + size)
                                return true;
                        }

                        return false;
                    });
                    _staleWritebackCauses[(int)cause]++;
                }

                return false;
            }

            if (assumptions.BufferVersion == _bufferCache.GpuModifiedVersion && !assumptions.ReadsImageOwnership)
            {
                return true;
            }

            foreach (var (query, address, size, answer) in assumptions.Answers)
            {
                var now = query switch
                {
                    ConcurrentQuery.DirtyBytes => _bufferCache.HasGpuDirtyBytes(address, size),
                    ConcurrentQuery.CleanWord => IsGpuOwnedForCleanRead(address, size),
                    ConcurrentQuery.Resident => IsGpuOwnedForResidentRead(address, size, clean: false),
                    _ => IsGpuOwnedForResidentRead(address, size, clean: true),
                };
                if (now != answer)
                {
                    _staleByAnswer++;
                    return false;
                }
            }

            return true;
        }

        // What the render thread's clean word read would first bring back to guest memory.
        private bool IsGpuOwnedForCleanRead(ulong address, ulong size) =>
            (_bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyPages(address, size)) ||
            _bufferCache.HasGpuDirtyBytes(address, size) ||
            _imageCache.HasUnsynchronizedGpuImage(address, size);

        private bool IsGpuOwnedForResidentRead(ulong address, ulong size, bool clean) =>
            _bufferCache.HasGpuDirtyBytes(address, size) ||
            (clean && (_bufferCache.HasGpuDirtyPages(address, size) || _imageCache.HasGpuModifiedImageBytes(address, size)));

        private bool TryReadGuestWordConcurrent(ulong address, out uint word, ConcurrentAssumptions assumptions)
        {
            word = 0;
            if (!_guestMemory.CanRead(address, sizeof(uint)))
            {
                return false;
            }

            var dirty = _bufferCache.HasGpuDirtyBytes(address, sizeof(uint));
            assumptions.Record(ConcurrentQuery.DirtyBytes, address, sizeof(uint), dirty);
            if (dirty)
            {
                throw new SubmissionScheduler.AbandonedWorkException($"The resolution reads GPU-owned memory: address=0x{address:X16}.");
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        private bool TryReadResidentGuestBytesConcurrent(ulong address, Span<byte> destination, bool clean, ConcurrentAssumptions assumptions)
        {
            var size = (ulong)destination.Length;
            if (!_guestMemory.CanRead(address, size))
            {
                return false;
            }

            var owned = IsGpuOwnedForResidentRead(address, size, clean);
            assumptions.Record(clean ? ConcurrentQuery.ResidentClean : ConcurrentQuery.Resident, address, size, owned);
            return !owned && _guestMemory.TryRead(address, destination);
        }
    }
}
