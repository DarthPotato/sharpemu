// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Rendering;

// Resolves the programs of deferred draws on a worker thread, in issue order, while the render
// thread interprets and records. The render thread takes a result for the draw it runs only when
// the resolution completed, used the inputs the draw derives and still matches the host's
// GPU-ownership state; otherwise it resolves the draw itself.
public sealed class ParallelProgramPrefetch : IProgramPrefetch, IDisposable
{
    public sealed class Job(RegisterBanks banks, long gate, bool indexed = false)
    {
        internal readonly RegisterBanks Banks = banks;
        internal readonly bool Indexed = indexed;
        // The count of stream items other than draws that must have run before the job resolves.
        internal readonly long Gate = gate;
        internal GraphicsProgramInputs Inputs;
        internal GraphicsPrograms? Programs;
        internal object? Assumptions;
        // 0 queued, 1 resolved, 2 abandoned.
        internal volatile int State;

        // A dispatch's job resolves its compute program instead.
        internal bool IsCompute;
        internal uint DispatchInitiator;
        internal uint DimensionX;
        internal uint DimensionY;
        internal uint DimensionZ;
        internal ComputeProgram? ComputeProgram;
        // Set under the lock before the worker took the job: the worker skips it.
        internal bool Cancelled;
        // Stopwatch timestamp of the submission, with the render profile on.
        internal long SubmittedAt;
    }

    // The job the worker took off the queue and is resolving, under _lock.
    private Job? _runningJob;

    // A job the worker has not started yet will not be: its dispatch resolves its own program
    // instead of waiting for the worker to reach it.
    private bool TryCancel(Job job)
    {
        lock (_lock)
        {
            if (job.State != 0 || ReferenceEquals(_runningJob, job))
            {
                return false;
            }

            job.Cancelled = true;
            return true;
        }
    }

    // On by default (SHARPEMU_PREFETCH_UNGATE=0 turns it off): command-processor stores and dispatches stop gating the jobs after
    // them, so the worker resolves ahead of them. A resolution is then checked, when taken, against
    // every store since it began by range (range-aware staleness), including the guest writes the
    // stream threads make for those items (GpuWritebackEpoch.AttachStreamStores); compute jobs are on.
    // (Declared before ComputeEnabled, which reads it: static fields initialize in text order.)
    public static bool Ungated { get; internal set; } =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PREFETCH_UNGATE"), "0", StringComparison.Ordinal);

    // SHARPEMU_PREFETCH_COMPUTE=1 resolves dispatches' compute programs on the worker too. On whenever
    // the prefetch is ungated (the default); off when gated: while every dispatch gates the jobs after it, the worker reaches a dispatch's job only
    // once the item before it ran, so most jobs are cancelled and the rest are waited for (Ghost of
    // Yotei: 3.65 vs 3.82 fps). It pays off once dispatches stop gating the prefetch.
    public static readonly bool ComputeEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PREFETCH_COMPUTE"), "1", StringComparison.Ordinal) || Ungated;

    private readonly RenderExecutor _executor;
    private readonly IRenderHost _host;
    private readonly IConcurrentResolveHost _concurrent;
    private readonly object _lock = new();
    private readonly Queue<Job> _queue = new();
    private readonly Thread _thread;
    private int _outstanding;
    private long _finished;
    private long _executedGate;
    // The gate the worker sleeps on; long.MaxValue while it does not.
    private long _sleepingOnGate = long.MaxValue;
    private bool _paused;
    private bool _running;
    private bool _stopping;

    public ParallelProgramPrefetch(RenderExecutor executor, IRenderHost host, IConcurrentResolveHost concurrent)
    {
        _executor = executor;
        _host = host;
        _concurrent = concurrent;
        _thread = new Thread(Run) { IsBackground = true, Name = "SharpEmu program resolve" };
        _thread.Start();
    }

    // The job of the draw the render thread runs now; set around each deferred draw.
    public Job? Current { get; set; }

    public long Submitted { get; private set; }

    // A gate above zero holds the job until AdvanceGate reaches it: with the command stream on its own
    // thread, draws are submitted ahead of the work before them, which may change what they read.
    public Job Submit(RegisterBanks banks, long gate = 0, bool indexed = false) => Enqueue(new Job(banks, gate, indexed));

    // A dispatch's compute program, resolved like a draw's programs: after the stream items before
    // the dispatch ran (the gate), and taken only when nothing it assumed changed.
    public Job SubmitCompute(RegisterBanks banks, long gate, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) =>
        Enqueue(new Job(banks, gate)
        {
            IsCompute = true,
            DispatchInitiator = dispatchInitiator,
            DimensionX = dimensionX,
            DimensionY = dimensionY,
            DimensionZ = dimensionZ,
        });

    private Job Enqueue(Job job)
    {
        if (RenderPhaseProfile.Enabled) job.SubmittedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _queue.Enqueue(job);
            Submitted++;
            _outstanding++;
            Monitor.PulseAll(_lock);
        }

        return job;
    }

    // Returns once the worker has finished every submitted job: it reads guest memory and cache
    // state, which the render thread changes only with the worker idle.
    public void WaitIdle()
    {
        if (Volatile.Read(ref _outstanding) == 0)
        {
            return;
        }

        using var profile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrefetchDrainWait);
        lock (_lock)
        {
            while (_outstanding != 0)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    // The render thread ran this many non-draw items: jobs gated on them may resolve.
    public void AdvanceGate(long executed)
    {
        Interlocked.Exchange(ref _executedGate, executed);
        // Only a worker asleep on a gate this reaches needs a wake: one per item costs the render
        // thread a kernel call each.
        if (Volatile.Read(ref _sleepingOnGate) <= executed)
        {
            lock (_lock)
            {
                Monitor.PulseAll(_lock);
            }
        }
    }

    // Returns once the worker finished the first count jobs it was given, so a non-draw item runs
    // with no resolution of the draws before it still reading.
    public void WaitFinished(long count)
    {
        if (Volatile.Read(ref _finished) >= count)
        {
            return;
        }

        using var profile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrefetchOrderWait);
        lock (_lock)
        {
            while (_finished < count)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    // Holds the worker between jobs, for host work that changes what a resolution reads.
    public void Pause()
    {
        using var profile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrefetchPauseWait);
        lock (_lock)
        {
            _paused = true;
            while (_running)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            Monitor.PulseAll(_lock);
        }
    }

    public bool TryTakeGraphics(RegisterBanks banks, in GraphicsProgramInputs inputs, out GraphicsPrograms programs)
    {
        programs = null!;
        var job = Current;
        if (job is null)
        {
            _takeOutcomes[(int)TakeOutcome.NoJob]++;
            return false;
        }

        if (!ReferenceEquals(job.Banks, banks))
        {
            _takeOutcomes[(int)TakeOutcome.OtherBanks]++;
            return false;
        }

        Current = null;
        // Ungated, the worker resolves dispatches too and can fall behind: a draw whose job it has not
        // started resolves its own programs rather than waiting for the worker to reach it.
        if (Ungated && job.State == 0 && TryCancel(job))
        {
            _takeOutcomes[(int)TakeOutcome.NotStarted]++;
            return false;
        }

        WaitFor(job);
        using var profile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrefetchValidation);
        var outcome = job.State != 1 ? TakeOutcome.NotResolved
            : !job.Inputs.Equals(inputs) ? TakeOutcome.OtherInputs
            : !_concurrent.IsStillCurrent(job.Assumptions!) ? TakeOutcome.Stale
            : TakeOutcome.Taken;
        _takeOutcomes[(int)outcome]++;
        if (outcome == TakeOutcome.OtherInputs)
        {
            var predicted = job.Inputs;
            if (predicted.PixelActive != inputs.PixelActive) _inputMismatches[0]++;
            if (predicted.DepthBound != inputs.DepthBound) _inputMismatches[1]++;
            if (predicted.Indexed != inputs.Indexed) _inputMismatches[2]++;
            if (!((ReadOnlySpan<SharpEmu.Libs.Gpu.Images.ColorComponentMap>)predicted.Mapping).SequenceEqual(inputs.Mapping)) _inputMismatches[3]++;
        }

        if (outcome != TakeOutcome.Taken)
        {
            return false;
        }

        // A clean word read the worker refused, where the render thread would have synchronized
        // the image first and read it: the programs it resolved can differ from the render thread's.
        if (_concurrent.RefusedReads(job.Assumptions!))
        {
            _graphicsTakenWithRefusals++;
            return false;
        }

        programs = job.Programs!;
        return true;
    }

    // The compute program of the dispatch the render thread runs now. Only a resolved, available
    // program that read nothing the worker refused and whose assumptions still hold is taken: an
    // unavailable result or a refused read may differ from what the render thread would resolve.
    public bool TryTakeCompute(RegisterBanks banks, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ,
        out ComputeProgram program)
    {
        program = null!;
        var job = Current;
        if (job is null || !job.IsCompute)
        {
            _computeOutcomes[(int)ComputeOutcome.NoJob]++;
            return false;
        }

        Current = null;
        if (!ReferenceEquals(job.Banks, banks) || job.DispatchInitiator != dispatchInitiator || job.DimensionX != dimensionX ||
            job.DimensionY != dimensionY || job.DimensionZ != dimensionZ)
        {
            _computeOutcomes[(int)ComputeOutcome.OtherInputs]++;
            return false;
        }

        // The worker reaches a dispatch's job only after the item before it ran, usually just before
        // the dispatch itself: waiting for it then costs more than resolving here.
        if (job.State == 0 && TryCancel(job))
        {
            _computeOutcomes[(int)ComputeOutcome.NotStarted]++;
            return false;
        }

        WaitFor(job);
        using var profile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrefetchValidation);
        var outcome = job.State != 1 || job.ComputeProgram is null ? ComputeOutcome.NotResolved
            : !job.ComputeProgram.Available || job.ComputeProgram.Consumed ? ComputeOutcome.Unavailable
            : _concurrent.RefusedReads(job.Assumptions!) ? ComputeOutcome.Refused
            : !_concurrent.IsStillCurrent(job.Assumptions!) ? ComputeOutcome.Stale
            : ComputeOutcome.Taken;
        _computeOutcomes[(int)outcome]++;
        if (outcome != ComputeOutcome.Taken)
        {
            return false;
        }

        program = job.ComputeProgram!;
        return true;
    }

    private enum ComputeOutcome { Taken, NoJob, OtherInputs, NotResolved, Unavailable, Refused, Stale, NotStarted, Count }

    private static readonly long[] _computeOutcomes = new long[(int)ComputeOutcome.Count];
    private static long _graphicsTakenWithRefusals;

    // Why the render thread did or did not take a resolution, counted on the render thread for the
    // render profile report.
    private enum TakeOutcome { Taken, NoJob, OtherBanks, NotResolved, OtherInputs, Stale, NotStarted, Count }

    private static readonly long[] _takeOutcomes = new long[(int)TakeOutcome.Count];

    // Which predicted input differed: pixel activity, depth binding, indexed mode, color export mapping.
    private static readonly long[] _inputMismatches = new long[4];

    internal static string TakeReport()
    {
        var line = $"[PERF][PROGRAM_PREFETCH] taken={_takeOutcomes[0]} no_job={_takeOutcomes[1]} other_banks={_takeOutcomes[2]} " +
            $"not_resolved={_takeOutcomes[3]} other_inputs={_takeOutcomes[4]} stale={_takeOutcomes[5]} not_started={_takeOutcomes[6]} " +
            $"input_pixel={_inputMismatches[0]} input_depth={_inputMismatches[1]} input_indexed={_inputMismatches[2]} input_mapping={_inputMismatches[3]} " +
            $"graphics_refused={_graphicsTakenWithRefusals} " +
            $"compute_taken={_computeOutcomes[0]} compute_no_job={_computeOutcomes[1]} compute_other_inputs={_computeOutcomes[2]} " +
            $"compute_not_resolved={_computeOutcomes[3]} compute_unavailable={_computeOutcomes[4]} compute_refused={_computeOutcomes[5]} " +
            $"compute_stale={_computeOutcomes[6]} compute_not_started={_computeOutcomes[7]}";
        Array.Clear(_takeOutcomes);
        Array.Clear(_inputMismatches);
        Array.Clear(_computeOutcomes);
        _graphicsTakenWithRefusals = 0;
        static string Ms(long ticks) => $"{ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms";
        var waits = _waitCount.Sum();
        line += $"\n[PERF][PREFETCH_WAIT] running={_waitCount[0]}/{Ms(_waitTicks[0])} behind={_waitCount[1]}/{Ms(_waitTicks[1])} " +
            $"gated={_waitCount[2]}/{Ms(_waitTicks[2])} paused={_waitCount[3]}/{Ms(_waitTicks[3])} " +
            $"mean_jobs_ahead={(waits == 0 ? 0 : (double)_waitJobsAhead / waits):F1} " +
            $"mean_job_age={(waits == 0 ? "0" : Ms(_waitJobAgeTicks / waits))} submitted_under_100us={_waitSubmittedLate} " +
            $"worker_empty={Ms(Interlocked.Exchange(ref _workerEmptyTicks, 0))} worker_gate={Ms(Interlocked.Exchange(ref _workerGateTicks, 0))} " +
            $"worker_paused={Ms(Interlocked.Exchange(ref _workerPausedTicks, 0))} worker_resolve={Ms(Interlocked.Exchange(ref _workerResolveTicks, 0))} " +
            $"worker_jobs={Interlocked.Exchange(ref _workerJobs, 0)}";
        Array.Clear(_waitCount);
        Array.Clear(_waitTicks);
        _waitJobsAhead = 0;
        _waitJobAgeTicks = 0;
        _waitSubmittedLate = 0;
        return line;
    }

    private void WaitFor(Job job)
    {
        if (job.State != 0)
        {
            return;
        }

        if (RenderPhaseProfile.Enabled)
        {
            WaitForMeasured(job);
            return;
        }

        WaitForCore(job);
    }

    // Why the render thread waited for the worker: its job was being resolved, or still queued
    // behind other jobs (how many), behind a closed gate, or while the worker was paused.
    private enum WaitCause { Running, Behind, Gated, Paused, Count }

    private static readonly long[] _waitCount = new long[(int)WaitCause.Count];
    private static readonly long[] _waitTicks = new long[(int)WaitCause.Count];
    private static long _waitJobsAhead;
    private static long _waitSubmittedLate;
    // Worker time: waiting for a job, on a closed gate, paused, and resolving.
    private static long _workerEmptyTicks;
    private static long _workerGateTicks;
    private static long _workerPausedTicks;
    private static long _workerResolveTicks;
    private static long _workerJobs;
    // The job a waiting render thread found was submitted this recently (ticks), summed.
    private static long _waitJobAgeTicks;

    private void WaitForMeasured(Job job)
    {
        WaitCause cause;
        long ahead;
        lock (_lock)
        {
            ahead = 0;
            var gated = false;
            var queuedJob = false;
            foreach (var queued in _queue)
            {
                if (ReferenceEquals(queued, job))
                {
                    queuedJob = true;
                    break;
                }

                ahead++;
            }

            if (queuedJob)
            {
                gated = _queue.Peek().Gate > Volatile.Read(ref _executedGate);
            }

            cause = !queuedJob ? WaitCause.Running : _paused ? WaitCause.Paused : gated ? WaitCause.Gated : WaitCause.Behind;
        }

        var age = System.Diagnostics.Stopwatch.GetTimestamp() - job.SubmittedAt;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        WaitForCore(job);
        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        _waitCount[(int)cause]++;
        _waitTicks[(int)cause] += elapsed;
        _waitJobsAhead += ahead;
        _waitJobAgeTicks += age;
        // Submitted less than 100 us before the render thread needed it.
        if (age < System.Diagnostics.Stopwatch.Frequency / 10_000) _waitSubmittedLate++;
    }

    private void WaitForCore(Job job)
    {
        using var profile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrefetchWait);
        var spinner = new SpinWait();
        for (var spin = 0; spin < 64 && job.State == 0; spin++)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }

        if (job.State != 0)
        {
            return;
        }

        lock (_lock)
        {
            while (job.State == 0)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    private void Run()
    {
        SubmissionScheduler.ThrowOnFatal = true;
        while (true)
        {
            Job job;
            var measured = RenderPhaseProfile.Enabled;
            var waitStart = measured ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_stopping)
                {
                    Monitor.Wait(_lock);
                }

                if (_stopping)
                {
                    return;
                }

                long gateStart = 0;
                var pausedAtGate = false;
                if (measured)
                {
                    gateStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _workerEmptyTicks, gateStart - waitStart);
                    pausedAtGate = _paused;
                }

                job = _queue.Peek();
                if (job.Gate > Volatile.Read(ref _executedGate))
                {
                    // The gate usually opens within a few items: spin before sleeping.
                    Monitor.Exit(_lock);
                    try
                    {
                        var spinner = new SpinWait();
                        for (var spin = 0; spin < 50 && job.Gate > Volatile.Read(ref _executedGate); spin++)
                        {
                            spinner.SpinOnce(sleep1Threshold: -1);
                        }
                    }
                    finally
                    {
                        Monitor.Enter(_lock);
                    }
                }

                while ((_paused || job.Gate > Volatile.Read(ref _executedGate)) && !_stopping)
                {
                    Interlocked.Exchange(ref _sleepingOnGate, _paused ? long.MaxValue : job.Gate);
                    if (!_paused && job.Gate <= Volatile.Read(ref _executedGate))
                    {
                        break;
                    }

                    Monitor.Wait(_lock, 50);
                }

                Volatile.Write(ref _sleepingOnGate, long.MaxValue);
                if (_stopping)
                {
                    return;
                }

                _queue.Dequeue();
                _running = true;
                _runningJob = job;
                if (measured)
                {
                    var gated = System.Diagnostics.Stopwatch.GetTimestamp() - gateStart;
                    if (pausedAtGate) Interlocked.Add(ref _workerPausedTicks, gated);
                    else Interlocked.Add(ref _workerGateTicks, gated);
                }
            }

            var resolveStart = measured ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (job.Cancelled)
            {
                job.State = 2;
            }
            else
            {
                Resolve(job);
            }

            if (measured)
            {
                Interlocked.Add(ref _workerResolveTicks, System.Diagnostics.Stopwatch.GetTimestamp() - resolveStart);
                Interlocked.Increment(ref _workerJobs);
            }

            lock (_lock)
            {
                _outstanding--;
                _finished++;
                _running = false;
                _runningJob = null;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void Resolve(Job job)
    {
        if (job.IsCompute && !RenderExecutor.ResolvesComputeAhead(job.Banks, job.DispatchInitiator, job.DimensionX, job.DimensionY, job.DimensionZ))
        {
            // The dispatch runs no program (no shader, no work): nothing to resolve.
            job.State = 2;
            return;
        }

        _concurrent.BeginConcurrentResolve();
        try
        {
            if (job.IsCompute)
            {
                job.ComputeProgram = _executor.ResolveComputeProgramAhead(job.Banks, job.DispatchInitiator,
                    job.DimensionX, job.DimensionY, job.DimensionZ);
                job.Assumptions = _concurrent.EndConcurrentResolve();
                job.State = job.ComputeProgram is null ? 2 : 1;
                return;
            }

            job.Inputs = RenderExecutor.ProgramInputsOf(job.Banks, _host.FormatSupport, _host.Fatal,
                job.Indexed && _executor.IndexedModeAffectsPrograms(job.Banks));
            job.Programs = _executor.ResolveGraphicsPrograms(job.Banks, in job.Inputs);
            job.Assumptions = _concurrent.EndConcurrentResolve();
            job.State = 1;
        }
        catch (Exception)
        {
            // The render thread resolves the draw itself, and fails there if the error is real.
            job.Assumptions = _concurrent.EndConcurrentResolve();
            job.State = 2;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopping = true;
            Monitor.PulseAll(_lock);
        }
    }
}
