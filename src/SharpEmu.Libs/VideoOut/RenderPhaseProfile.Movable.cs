// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;

namespace SharpEmu.Libs.VideoOut;

internal static partial class RenderPhaseProfile
{
    // Deliberately narrower than Enabled: this experiment requires explicit render profiling.
    internal static readonly bool MovableEnabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1";
    internal enum WorkKind { Outside, Draw, Dispatch, Count }
    internal enum WorkCategory { Ordered, Movable, Other, Count }
    internal enum PreparationBlock
    {
        ImageLookup, BufferOwnership, DeviceAddressResidency, TessellationDeviceAddresses,
        StencilOwnership, MarkGpuWritten, DccDepthPreparation, DescriptorHeap,
        GuestRead, RuntimeDescriptors, BindlessHeap, CallRecords, HostMovie, SpecialDraw, Count,
    }

    // This table is intentionally conservative. A broad descriptor/recording phase is not
    // evidence of immutable inputs. Only audited packing and native emission leaves move.
    internal static WorkCategory CategoryOf(Phase phase) => phase switch
    {
        Phase.ImmutablePacking or Phase.DescriptorEmission or Phase.ExistingPipeline or
        Phase.VulkanCommands => WorkCategory.Movable,
        Phase.DescriptorPreparation or
        Phase.DrawTargetResolution or Phase.DrawAttachmentPreparation or
        Phase.DrawVertexBufferAcquisition or Phase.DrawIndexBufferAcquisition or
        Phase.BufferMappedRangeValidation or Phase.BufferAcquisitionChecks or
        Phase.BufferCacheLookup or Phase.BufferDirtySynchronization or Phase.BufferDirtySyncWritten or
        Phase.BufferDirtySyncTexel or Phase.BufferDirtySyncUpload or Phase.BufferStreamUpload or
        Phase.BufferStagingUpload or Phase.BufferUploadTracking or Phase.BufferSharedPageFilter or
        Phase.ImageLookup or Phase.ImageAcquire or Phase.ImageCreate or Phase.ImageRefresh or
        Phase.ImageUpload or Phase.ImageDownload or Phase.ImageOverlap or Phase.ImageTracking or
        Phase.ImageTiling or Phase.ImageTransitions or Phase.ImageVersions or
        Phase.DrawResources or Phase.BufferResources or Phase.ResourceMaterialization or
        Phase.ProgramSourceRead or Phase.CommandMemoryRead or Phase.DispatchConsume or
        Phase.OrderedOwnership or Phase.OrderedGuestRead or Phase.OrderedBarrierDecision or
        Phase.OrderedMetadata => WorkCategory.Ordered,
        _ => WorkCategory.Other,
    };

    internal struct OperationWork
    {
        internal WorkKind Kind;
        internal ulong Blocks;
        internal ulong Dependencies;
        internal int Categories;
        internal long Ticks, MovableTicks;
    }

    // Pure tick accounting, also used by deterministic tests. No worker touches this object.
    internal sealed class MovableAccounting
    {
        internal readonly long[] Ticks = new long[(int)WorkKind.Count * (int)WorkCategory.Count];
        internal readonly long[] Operations = new long[(int)WorkKind.Count];
        internal readonly long[] CategoryOperations = new long[(int)WorkKind.Count * (int)WorkCategory.Count];
        internal readonly long[] Blocked = new long[(int)WorkKind.Count];
        internal readonly long[] BlockedTicks = new long[(int)WorkKind.Count];
        internal readonly long[] ReasonOperations = new long[(int)WorkKind.Count * (int)PreparationBlock.Count];
        internal readonly long[] ReasonTicks = new long[(int)WorkKind.Count * (int)PreparationBlock.Count];
        internal readonly long[] ReasonMovableTicks = new long[(int)WorkKind.Count * (int)PreparationBlock.Count];
        internal readonly long[] DependencyOperations = new long[(int)WorkKind.Count * (int)PreparationBlock.Count];

        internal void Charge(ref OperationWork operation, Phase phase, long ticks)
        {
            var category = operation.Kind == WorkKind.Outside ? WorkCategory.Other : CategoryOf(phase);
            Ticks[(int)operation.Kind * (int)WorkCategory.Count + (int)category] += ticks;
            if (operation.Kind == WorkKind.Outside) return;
            operation.Ticks += ticks;
            if (category == WorkCategory.Movable) operation.MovableTicks += ticks;
            operation.Categories |= 1 << (int)category;
        }

        internal void Finish(in OperationWork operation)
        {
            var kind = (int)operation.Kind;
            Operations[kind]++;
            for (var category = 0; category < (int)WorkCategory.Count; category++)
                if ((operation.Categories & (1 << category)) != 0)
                    CategoryOperations[kind * (int)WorkCategory.Count + category]++;
            for (var reason = 0; reason < (int)PreparationBlock.Count; reason++)
                if ((operation.Dependencies & (1UL << reason)) != 0)
                    DependencyOperations[kind * (int)PreparationBlock.Count + reason]++;
            if (operation.Blocks == 0) return;
            Blocked[kind]++;
            BlockedTicks[kind] += operation.Ticks;
            for (var reason = 0; reason < (int)PreparationBlock.Count; reason++)
            {
                if ((operation.Blocks & (1UL << reason)) == 0) continue;
                var index = kind * (int)PreparationBlock.Count + reason;
                ReasonOperations[index]++;
                ReasonTicks[index] += operation.Ticks;
                ReasonMovableTicks[index] += operation.MovableTicks;
            }
        }
    }

    private static class MovableStorage { internal static readonly MovableAccounting Counters = new(); }
    [ThreadStatic] private static OperationWork _operationWork;
    private static void ChargeMovable(Phase phase, long ticks) => MovableStorage.Counters.Charge(ref _operationWork, phase, ticks);

    internal readonly ref struct OperationScope
    {
        private readonly bool _active;
        private readonly OperationWork _previous;
        internal OperationScope(WorkKind kind)
        {
            _active = true;
            Charge(_current);
            _previous = _operationWork;
            _operationWork = new OperationWork { Kind = kind };
        }
        public void Dispose()
        {
            if (!_active) return;
            Charge(_current);
            MovableStorage.Counters.Finish(in _operationWork);
            _operationWork = _previous;
        }
    }

    internal static OperationScope MeasureOperation(WorkKind kind) =>
        MovableEnabled && _threadTicks is null && _scopeDepth > 0 && _operationWork.Kind == WorkKind.Outside ? new(kind) : default;

    internal static Scope MeasureMovableDetail(Phase phase) => MovableEnabled ? MeasureDetail(phase) : default;

    internal static void BlockPreparation(PreparationBlock reason, bool blocksRecording = true)
    {
        if (!MovableEnabled) return;
        if (_threadTicks is null && _operationWork.Kind != WorkKind.Outside)
        {
            _operationWork.Dependencies |= 1UL << (int)reason;
            if (blocksRecording) _operationWork.Blocks |= 1UL << (int)reason;
        }
    }

    private static void ReportMovable(double seconds, long frames, long renderTicks)
    {
        var counters = MovableStorage.Counters;
        static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var text = new StringBuilder(FormattableString.Invariant($"[PERF][MOVABLE] window_s={seconds:F1} frames={frames} render_ms={Ms(renderTicks)}"));
        var total = counters.Ticks.Sum();
        for (var category = 0; category < (int)WorkCategory.Count; category++)
        {
            var ticks = 0L;
            var count = 0L;
            for (var kind = 0; kind < (int)WorkKind.Count; kind++)
            {
                ticks += counters.Ticks[kind * (int)WorkCategory.Count + category];
                count += counters.CategoryOperations[kind * (int)WorkCategory.Count + category];
            }
            text.Append($" {((WorkCategory)category).ToString().ToLowerInvariant()}_ms={Ms(ticks)} {((WorkCategory)category).ToString().ToLowerInvariant()}_n={count}");
        }
        text.Append($" reconcile_ms={Ms(renderTicks - total)} outside_ms={Ms(counters.Ticks[(int)WorkCategory.Other])}");
        for (var kind = 1; kind < (int)WorkKind.Count; kind++)
        {
            var name = ((WorkKind)kind).ToString().ToLowerInvariant();
            text.Append($" {name}_n={counters.Operations[kind]} {name}_blocked_n={counters.Blocked[kind]} {name}_blocked_ms={Ms(counters.BlockedTicks[kind])}");
            for (var category = 0; category < (int)WorkCategory.Count; category++)
            {
                var index = kind * (int)WorkCategory.Count + category;
                var label = ((WorkCategory)category).ToString().ToLowerInvariant();
                text.Append($" {name}_{label}_ms={Ms(counters.Ticks[index])} {name}_{label}_n={counters.CategoryOperations[index]}");
            }
            for (var reason = 0; reason < (int)PreparationBlock.Count; reason++)
            {
                var index = kind * (int)PreparationBlock.Count + reason;
                text.Append($" {name}_{(PreparationBlock)reason}_requires_n={counters.DependencyOperations[index]} {name}_{(PreparationBlock)reason}_n={counters.ReasonOperations[index]} {name}_{(PreparationBlock)reason}_ms={Ms(counters.ReasonTicks[index])} {name}_{(PreparationBlock)reason}_movable_ms={Ms(counters.ReasonMovableTicks[index])}");
            }
        }
        Console.Error.WriteLine(text);
        Array.Clear(counters.Ticks); Array.Clear(counters.Operations); Array.Clear(counters.CategoryOperations);
        Array.Clear(counters.Blocked); Array.Clear(counters.BlockedTicks);
        Array.Clear(counters.ReasonOperations); Array.Clear(counters.ReasonTicks); Array.Clear(counters.ReasonMovableTicks);
        Array.Clear(counters.DependencyOperations);
    }
}
