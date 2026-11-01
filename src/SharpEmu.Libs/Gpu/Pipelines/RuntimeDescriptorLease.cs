// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class RuntimeDescriptorWorkerOptions
{
    // On by default; SHARPEMU_RUNTIME_RECORD_WORKERS=0 keeps runtime-descriptor recording on the render thread.
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_RUNTIME_RECORD_WORKERS") != "0";
}

// Retirement closes acquisition immediately, but reclamation waits for speculative users as well
// as submitted users. The caller transfers a successful pin to the joined tick's completion action.
internal sealed class RuntimeDescriptorLease(long generation = 1)
{
    private long _generation = generation;
    public long Generation => Volatile.Read(ref _generation);
    public void Invalidate() => Interlocked.Increment(ref _generation);
    private readonly object _gate = new();
    private int _pins;
    private bool _retired;
    private Action? _release;

    public bool TryPin()
    {
        lock (_gate)
        {
            if (_retired) return false;
            _pins++;
            return true;
        }
    }

    public void Unpin()
    {
        Action? release = null;
        lock (_gate)
        {
            if (_pins <= 0) throw new InvalidOperationException("Unbalanced runtime descriptor pin.");
            if (--_pins == 0 && _retired) { release = _release; _release = null; }
        }
        release?.Invoke();
    }

    public void Retire(Action release)
    {
        lock (_gate)
        {
            if (_retired) throw new InvalidOperationException("Runtime descriptor retired twice.");
            _retired = true;
            if (_pins != 0) { _release = release; return; }
        }
        release();
    }
}
