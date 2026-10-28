// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// One BDA consumer, independent of the current dirty-page summary. The bounded
// sparse word index avoids walking a second address-space-sized bitmap.
public sealed class PendingCpuDirtyBlocks
{
    private const int Capacity = 256;
    private readonly object _gate = new();
    private readonly long[] _words = new long[(TrackerLayout.BlockCount + 63) / 64];
    private readonly HashSet<int> _pendingWords = new();
    private bool _overflow;

    public void Publish(ulong block)
    {
        lock (_gate)
        {
            if (_overflow) return;
            var index = (int)(block / 64);
            if (!_pendingWords.Contains(index) && _pendingWords.Count == Capacity)
            {
                _overflow = true;
                return;
            }
            Interlocked.Or(ref _words[index], 1L << (int)(block % 64));
            _pendingWords.Add(index);
        }
    }

    public (bool Overflow, List<(int Index, ulong Bits)> Words) Drain()
    {
        lock (_gate)
        {
            var words = new List<(int, ulong)>(_pendingWords.Count);
            foreach (var index in _pendingWords)
                words.Add((index, (ulong)Interlocked.Exchange(ref _words[index], 0)));
            _pendingWords.Clear();
            var overflow = _overflow;
            _overflow = false;
            return (overflow, words);
        }
    }

    public void Restore((bool Overflow, List<(int Index, ulong Bits)> Words) batch)
    {
        lock (_gate)
        {
            if (batch.Overflow) _overflow = true;
            foreach (var (index, bits) in batch.Words)
            {
                if (_overflow) break;
                if (!_pendingWords.Contains(index) && _pendingWords.Count == Capacity)
                {
                    _overflow = true;
                    break;
                }
                Interlocked.Or(ref _words[index], (long)bits);
                _pendingWords.Add(index);
            }
        }
    }
}
