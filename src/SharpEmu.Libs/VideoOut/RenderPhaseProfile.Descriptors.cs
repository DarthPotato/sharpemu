// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.VideoOut;

internal static partial class RenderPhaseProfile
{
    internal enum DescriptorMemoKind { None, Reference, Content, Count }
    internal enum DescriptorStep { Rest, MemoValidation, ImageResolution, BufferDiscovery, PublicationMetadata, ShaderData, Samplers, RuntimeDescriptors, Count }
    internal enum DescriptorMissReason
    {
        Unclassified, FirstProgramUse, NoContentEntry, CapacityEviction,
        ImageWordsChanged, SamplerWordsChanged, ImageAndSamplerWordsChanged, ShaderBaseChanged, ContentCollision,
        MemoInvalid, SlotGenerationChanged, Unregistered, Parked, NeedsRebind, DepthOwner,
        LookupRangeInvalidated, LookupHistoryOverflow, BindingRefind, DepthFeedback, StencilStorage,
        RuntimeDescriptors, FrontendNotReady, HostMovie, ImageTrace, MeshTrace, BindlessHeap,
        CallArguments, CallTargets, CallRecords, WorkerOrderedPreparation, SnapshotMismatch, Count,
    }

    // Uses the render clock's exact deltas. Self time reconciles with DescriptorPreparation;
    // inclusive time also includes the existing nested cache/upload/wait phases. Steps never overlap.
    internal sealed class DescriptorAccounting
    {
        internal readonly long[] SelfTicks = new long[(int)DescriptorMemoKind.Count * (int)DescriptorStep.Count];
        internal readonly long[] InclusiveTicks = new long[(int)DescriptorMemoKind.Count * (int)DescriptorStep.Count];
        internal readonly long[] StepCalls = new long[(int)DescriptorStep.Count];
        internal readonly long[] PrepareCalls = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] BindCalls = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] PrepareTicks = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] BindTicks = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] MissPrepareCalls = new long[(int)DescriptorMissReason.Count];
        internal readonly long[] MissBindCalls = new long[(int)DescriptorMissReason.Count];
        internal readonly long[] MissPrepareTicks = new long[(int)DescriptorMissReason.Count];
        internal readonly long[] MissBindTicks = new long[(int)DescriptorMissReason.Count];
        internal readonly long[] MissInclusiveTicks = new long[(int)DescriptorMissReason.Count];
        // Workers use wall time, separately from render self time and its reconciliation.
        internal readonly long[] WorkerCalls = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] WorkerTicks = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] WorkerMissCalls = new long[(int)DescriptorMissReason.Count];
        internal readonly long[] WorkerMissTicks = new long[(int)DescriptorMissReason.Count];
        internal readonly long[] WorkerBindCalls = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] WorkerBindTicks = new long[(int)DescriptorMemoKind.Count];
        internal readonly long[] WorkerMissBindCalls = new long[(int)DescriptorMissReason.Count];
        internal readonly long[] WorkerMissBindTicks = new long[(int)DescriptorMissReason.Count];
        private readonly long[] _self = new long[(int)DescriptorStep.Count];
        private readonly long[] _inclusive = new long[(int)DescriptorStep.Count];

        internal void Charge(DescriptorStep step, Phase phase, long ticks)
        {
            _inclusive[(int)step] += ticks;
            if (phase == Phase.DescriptorPreparation) _self[(int)step] += ticks;
        }

        internal void Finish(DescriptorMemoKind kind, bool bind, DescriptorMissReason reason = DescriptorMissReason.Unclassified)
        {
            (bind ? BindCalls : PrepareCalls)[(int)kind]++;
            if (kind == DescriptorMemoKind.None)
                (bind ? MissBindCalls : MissPrepareCalls)[(int)reason]++;
            for (var step = 0; step < (int)DescriptorStep.Count; step++)
            {
                var index = (int)kind * (int)DescriptorStep.Count + step;
                SelfTicks[index] += _self[step];
                InclusiveTicks[index] += _inclusive[step];
                (bind ? BindTicks : PrepareTicks)[(int)kind] += _self[step];
                if (kind == DescriptorMemoKind.None)
                {
                    (bind ? MissBindTicks : MissPrepareTicks)[(int)reason] += _self[step];
                    MissInclusiveTicks[(int)reason] += _inclusive[step];
                }
            }
            Array.Clear(_self);
            Array.Clear(_inclusive);
        }

        internal string TakeReport(double seconds, long frames, long phaseTicks, long materializationTicks, long materializationCalls)
        {
            static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
            var text = new StringBuilder(FormattableString.Invariant($"[PERF][DESC_PREP] window_s={seconds:F1} frames={frames} fast={(DescriptorPreparationOptions.Fast ? 1 : 0)}"));
            long total = 0, inclusive = 0;
            for (var kind = 0; kind < (int)DescriptorMemoKind.Count; kind++)
            {
                long self = 0, all = 0;
                for (var step = 0; step < (int)DescriptorStep.Count; step++)
                {
                    self += SelfTicks[kind * (int)DescriptorStep.Count + step];
                    all += InclusiveTicks[kind * (int)DescriptorStep.Count + step];
                }
                var name = ((DescriptorMemoKind)kind).ToString().ToLowerInvariant();
                text.Append($" {name}_ms={Ms(self)} {name}_all_ms={Ms(all)} {name}_n={PrepareCalls[kind] + BindCalls[kind]} {name}_prepare_n={PrepareCalls[kind]} {name}_bind_n={BindCalls[kind]} {name}_prepare_ms={Ms(PrepareTicks[kind])} {name}_bind_ms={Ms(BindTicks[kind])}");
                total += self;
                inclusive += all;
            }
            text.Append($" total_ms={Ms(total)} all_ms={Ms(inclusive)} reconcile_ms={Ms(phaseTicks - total)} materialization_ms={Ms(materializationTicks)} materialization_n={materializationCalls}");
            for (var step = 0; step < (int)DescriptorStep.Count; step++)
            {
                long self = 0, all = 0;
                for (var kind = 0; kind < (int)DescriptorMemoKind.Count; kind++)
                {
                    var index = kind * (int)DescriptorStep.Count + step;
                    self += SelfTicks[index];
                    all += InclusiveTicks[index];
                    // The matrix exposes what still happens specifically on hits.
                    text.Append($" {((DescriptorMemoKind)kind).ToString().ToLowerInvariant()}_{(DescriptorStep)step}_ms={Ms(SelfTicks[index])}");
                }
                text.Append($" {(DescriptorStep)step}_ms={Ms(self)} {(DescriptorStep)step}_all_ms={Ms(all)} {(DescriptorStep)step}_n={StepCalls[step]}");
            }
            for (var reason = 0; reason < (int)DescriptorMissReason.Count; reason++)
            {
                var name = ((DescriptorMissReason)reason).ToString();
                if (MissPrepareCalls[reason] + MissBindCalls[reason] != 0)
                    text.Append($" miss_{name}_n={MissPrepareCalls[reason]} miss_{name}_ms={Ms(MissPrepareTicks[reason])} miss_{name}_bind_n={MissBindCalls[reason]} miss_{name}_bind_ms={Ms(MissBindTicks[reason])} miss_{name}_all_ms={Ms(MissInclusiveTicks[reason])}");
                var workerCalls = Interlocked.Exchange(ref WorkerMissCalls[reason], 0);
                var workerTicks = Interlocked.Exchange(ref WorkerMissTicks[reason], 0);
                var workerBindCalls = Interlocked.Exchange(ref WorkerMissBindCalls[reason], 0);
                var workerBindTicks = Interlocked.Exchange(ref WorkerMissBindTicks[reason], 0);
                if (workerCalls + workerBindCalls != 0)
                    text.Append($" worker_miss_{name}_n={workerCalls} worker_miss_{name}_wall_ms={Ms(workerTicks)} worker_miss_{name}_bind_n={workerBindCalls} worker_miss_{name}_bind_wall_ms={Ms(workerBindTicks)}");
            }
            for (var kind = 0; kind < (int)DescriptorMemoKind.Count; kind++)
                text.Append($" worker_{(DescriptorMemoKind)kind}_n={Interlocked.Exchange(ref WorkerCalls[kind], 0)} worker_{(DescriptorMemoKind)kind}_wall_ms={Ms(Interlocked.Exchange(ref WorkerTicks[kind], 0))} worker_{(DescriptorMemoKind)kind}_bind_n={Interlocked.Exchange(ref WorkerBindCalls[kind], 0)} worker_{(DescriptorMemoKind)kind}_bind_wall_ms={Ms(Interlocked.Exchange(ref WorkerBindTicks[kind], 0))}");
            Array.Clear(SelfTicks); Array.Clear(InclusiveTicks); Array.Clear(StepCalls);
            Array.Clear(PrepareCalls); Array.Clear(BindCalls);
            Array.Clear(PrepareTicks); Array.Clear(BindTicks);
            Array.Clear(MissPrepareCalls); Array.Clear(MissBindCalls); Array.Clear(MissPrepareTicks);
            Array.Clear(MissBindTicks); Array.Clear(MissInclusiveTicks);
            return text.ToString();
        }
    }

    private static class DescriptorStorage { internal static readonly DescriptorAccounting Counters = new(); }
    [ThreadStatic] private static bool _descriptorActive;
    [ThreadStatic] private static DescriptorMemoKind _descriptorMemo;
    [ThreadStatic] private static DescriptorStep _descriptorStep;
    [ThreadStatic] private static DescriptorMissReason _descriptorMiss;
    [ThreadStatic] private static bool _descriptorWorkerActive;

    private static void ChargeDescriptor(Phase phase, long ticks)
    {
        if (_descriptorActive) DescriptorStorage.Counters.Charge(_descriptorStep, phase, ticks);
    }

    internal readonly ref struct DescriptorScope
    {
        private readonly bool _active;
        private readonly bool _bind;
        private readonly bool _worker;
        private readonly long _started;
        internal DescriptorScope(DescriptorMemoKind kind, bool bind, DescriptorMissReason reason, bool worker)
        {
            _worker = worker;
            _started = worker ? Stopwatch.GetTimestamp() : 0;
            if (!worker) Charge(_current);
            _active = true;
            _bind = bind;
            _descriptorMemo = kind;
            _descriptorMiss = reason;
            _descriptorStep = DescriptorStep.Rest;
            if (worker) _descriptorWorkerActive = true;
            else
            {
                _descriptorActive = true;
                DescriptorStorage.Counters.StepCalls[(int)DescriptorStep.Rest]++;
            }
        }
        public void Dispose()
        {
            if (!_active) return;
            if (_worker)
            {
                var elapsed = Stopwatch.GetTimestamp() - _started;
                var counters = DescriptorStorage.Counters;
                Interlocked.Increment(ref (_bind ? counters.WorkerBindCalls : counters.WorkerCalls)[(int)_descriptorMemo]);
                Interlocked.Add(ref (_bind ? counters.WorkerBindTicks : counters.WorkerTicks)[(int)_descriptorMemo], elapsed);
                if (_descriptorMemo == DescriptorMemoKind.None)
                {
                    Interlocked.Increment(ref (_bind ? counters.WorkerMissBindCalls : counters.WorkerMissCalls)[(int)_descriptorMiss]);
                    Interlocked.Add(ref (_bind ? counters.WorkerMissBindTicks : counters.WorkerMissTicks)[(int)_descriptorMiss], elapsed);
                }
                _descriptorWorkerActive = false;
                return;
            }
            Charge(_current);
            DescriptorStorage.Counters.Finish(_descriptorMemo, _bind, _descriptorMiss);
            _descriptorActive = false;
        }
    }

    internal readonly ref struct DescriptorStepScope
    {
        private readonly bool _active;
        private readonly DescriptorStep _previous;
        internal DescriptorStepScope(DescriptorStep step)
        {
            Charge(_current);
            _active = true;
            _previous = _descriptorStep;
            _descriptorStep = step;
            DescriptorStorage.Counters.StepCalls[(int)step]++;
        }
        public void Dispose()
        {
            if (!_active) return;
            Charge(_current);
            _descriptorStep = _previous;
        }
    }

    internal static DescriptorScope MeasureDescriptor(DescriptorMemoKind kind = DescriptorMemoKind.None, bool bind = false,
        DescriptorMissReason reason = DescriptorMissReason.Unclassified, bool worker = false) =>
        MovableEnabled && !_descriptorActive && !_descriptorWorkerActive &&
        (worker || _threadTicks is null && _scopeDepth > 0) ? new(kind, bind, reason, worker) : default;

    internal static DescriptorStepScope MeasureDescriptorStep(DescriptorStep step) =>
        MovableEnabled && _descriptorActive ? new(step) : default;

    internal static void DescriptorMemo(DescriptorMemoKind kind)
    {
        if (MovableEnabled && (_descriptorActive || _descriptorWorkerActive)) _descriptorMemo = kind;
    }

    internal static DescriptorMissReason CurrentDescriptorMiss => _descriptorMiss;

    internal static void DescriptorMiss(DescriptorMissReason reason)
    {
        if (MovableEnabled && (_descriptorActive || _descriptorWorkerActive)) _descriptorMiss = reason;
    }
}
