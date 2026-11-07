// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Lock order: _gate is a leaf. Never call a backend, acquire a compile permit, wait,
// or join while holding it. Backends must not wait for jobs on this scheduler.
// Results belong to the host's pipeline registry, including unused predictions;
// Stop joins all owners before the host destroys that registry. No eviction.
internal sealed class PipelineCompileScheduler : IDisposable
{
    internal enum State { Queued, Running, Ready, Failed }

    internal sealed class Request
    {
        internal required object Key;
        internal readonly TaskCompletionSource<PipelineHandle> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<PipelineHandle>? Compile;
        internal LinkedListNode<Request>? Node;
        internal long Bytes;
        internal bool Speculative;
        internal bool WasSpeculative;
        internal volatile bool Consumed;
        internal long CompletedAt;
        private int _leadRecorded;
        internal State Status;

        internal PipelineHandle Use()
        {
            Consumed = true;
            var profile = RenderPhaseProfile.Enabled;
            var pending = !Completion.Task.IsCompleted;
            var started = profile && pending ? Stopwatch.GetTimestamp() : 0;
            var result = Completion.Task.GetAwaiter().GetResult();
            if (profile)
            {
                AsyncPipelineProfile.Use(pending, started);
                NoteLead();
            }
            return result;
        }

        internal void NoteLead()
        {
            if (RenderPhaseProfile.Enabled && CompletedAt != 0 && Interlocked.Exchange(ref _leadRecorded, 1) == 0)
                AsyncPipelineProfile.Lead(CompletedAt);
        }
    }

    internal static bool Enabled => Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_PIPELINES") != "0";
    internal static int WorkerCount => int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PIPELINE_COMPILE_WORKERS"), out var n)
        ? Math.Clamp(n, 1, 8) : 4;
    [ThreadStatic] internal static bool IsWorker;
    private readonly object _gate = new();
    private readonly Dictionary<object, Request> _requests = new();
    private readonly LinkedList<Request> _demand = new();
    private readonly LinkedList<Request> _speculative = new();
    private readonly Thread[] _workers;
    private readonly int _capacity;
    private readonly long _byteLimit;
    private long _bytes;
    private int _inline;
    private bool _stopping;

    internal PipelineCompileScheduler(int workers = 4, int capacity = 64, long byteLimit = 256L * 1024 * 1024)
    {
        _capacity = capacity;
        _byteLimit = byteLimit;
        AsyncPipelineProfile.Register(this);
        _workers = Enumerable.Range(0, workers).Select(i => new Thread(Work)
        {
            IsBackground = true, Name = $"Pipeline compile {i}",
        }).ToArray();
        foreach (var worker in _workers) worker.Start();
    }

    // Demand overflow runs inline, but has the same single-flight claim and native
    // permit as worker jobs. Speculative rejection retains no delegate or resources.
    internal Request? Enqueue(object key, Func<PipelineHandle> compile, bool speculative, long bytes, bool compute = false)
    {
        Request request;
        var inline = false;
        lock (_gate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(PipelineCompileScheduler));
            AsyncPipelineProfile.Request(compute, speculative);
            if (_requests.TryGetValue(key, out request!))
            {
                AsyncPipelineProfile.Dedup();
                if (!speculative && request.Node is { } node && request.Speculative)
                {
                    _speculative.Remove(node);
                    request.Node = _demand.AddLast(request);
                    request.Speculative = false;
                }
                return request;
            }
            var full = _demand.Count + _speculative.Count >= _capacity || bytes > _byteLimit - _bytes;
            if (full && speculative)
            {
                AsyncPipelineProfile.Reject();
                return null;
            }
            request = new Request { Key = key, Compile = compile, Bytes = bytes, Speculative = speculative, WasSpeculative = speculative };
            _requests.Add(key, request);
            if (full)
            {
                inline = true;
                _inline++;
                request.Status = State.Running;
            }
            else
            {
                request.Node = (speculative ? _speculative : _demand).AddLast(request);
                _bytes += bytes;
                AsyncPipelineProfile.Queue(_demand.Count + _speculative.Count, _bytes);
                Monitor.PulseAll(_gate);
            }
        }
        if (inline)
        {
            try { Run(request); }
            finally { lock (_gate) { _inline--; Monitor.PulseAll(_gate); } }
        }
        return request;
    }

    internal void Consume(Request request)
    {
        request.Consumed = true;
        if (request.Completion.Task.IsCompletedSuccessfully) request.NoteLead();
    }

    internal void ConsumeKey(object key)
    {
        Request? request;
        lock (_gate) _requests.TryGetValue(key, out request);
        if (request is not null) Consume(request);
    }

    private void Work()
    {
        IsWorker = true;
        Scheduling.SubmissionScheduler.ThrowOnFatal = true;
        while (true)
        {
            Request request;
            lock (_gate)
            {
                while (_demand.Count + _speculative.Count == 0 && !_stopping) Monitor.Wait(_gate);
                var queue = _demand.Count != 0 ? _demand : _speculative;
                if (queue.First is not { } node) return;
                request = node.Value;
                queue.RemoveFirst();
                request.Node = null;
                _bytes -= request.Bytes;
                request.Status = State.Running;
            }
            Run(request);
        }
    }

    private void Run(Request request)
    {
        try
        {
            var result = request.Compile!();
            request.CompletedAt = RenderPhaseProfile.Enabled ? Stopwatch.GetTimestamp() : 0;
            lock (_gate) request.Status = State.Ready;
            request.Completion.SetResult(result);
        }
        catch (Exception exception)
        {
            lock (_gate) request.Status = State.Failed;
            AsyncPipelineProfile.Failure();
            request.Completion.SetException(exception);
            Console.Error.WriteLine($"[GPU][WARN][ASYNC_PIPELINES] Compile failed: key_type={request.Key.GetType().Name} key_hash={request.Key.GetHashCode():X8} error={exception.Message}");
        }
        finally
        {
            request.Compile = null;
        }
    }

    internal long NeverConsumed
    {
        get { lock (_gate) return _requests.Values.LongCount(r => r.WasSpeculative && !r.Consumed && r.Status == State.Ready); }
    }

    public void Dispose()
    {
        StopAdmission();
        foreach (var worker in _workers)
        {
            JoinCompiler(worker, "Pipeline");
        }
        lock (_gate) while (_inline != 0) Monitor.Wait(_gate);
    }

    internal void StopAdmission()
    {
        lock (_gate) { _stopping = true; Monitor.PulseAll(_gate); }
    }

    internal static void JoinCompiler(Thread thread, string role, TimeSpan? warningAfter = null, Action? delayed = null)
    {
        if (thread.Join(warningAfter ?? TimeSpan.FromSeconds(10))) return;
        Console.Error.WriteLine($"[GPU][WARN] {role} compiler still running; delaying destruction until it finishes.");
        delayed?.Invoke();
        thread.Join();
    }
}

// A save closes admission first, then drains every native user, so a busy queue
// cannot indefinitely postpone its snapshot. Leases may be released on any thread.
internal sealed class PipelineCompileBudget(int permits)
{
    private readonly object _gate = new();
    private int _active;
    private bool _saving;

    internal IDisposable Enter()
    {
        lock (_gate)
        {
            while (_saving || _active == permits) Monitor.Wait(_gate);
            _active++;
        }
        return new Lease(this, false);
    }

    internal IDisposable Pause()
    {
        var started = RenderPhaseProfile.Enabled ? Stopwatch.GetTimestamp() : 0;
        lock (_gate)
        {
            while (_saving) Monitor.Wait(_gate);
            _saving = true;
            while (_active != 0) Monitor.Wait(_gate);
        }
        AsyncPipelineProfile.SaveDelay(started);
        return new Lease(this, true);
    }

    private sealed class Lease(PipelineCompileBudget owner, bool save) : IDisposable
    {
        private PipelineCompileBudget? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } budget) return;
            lock (budget._gate)
            {
                if (save) budget._saving = false;
                else budget._active--;
                Monitor.PulseAll(budget._gate);
            }
        }
    }
}
