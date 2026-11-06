// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.VideoOut;

// Diagnostic only: never authorizes reuse. All callers guard the static switch so the
// disabled path neither allocates TLS/collections nor reads image state or takes locks.
// It costs fps on the acquisition path it measures, so it needs its own switch on top of
// SHARPEMU_PROFILE_RENDER=1 (which reports it): SHARPEMU_PROFILE_SAMPLED_REUSE=1.
internal static class ImageAcquisitionProfile
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1" &&
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SAMPLED_REUSE") == "1";
    internal enum Caller { Stage, Worker, Prologue, Memo, Other }
    private static class Storage { internal static readonly Tracker Instance = new(); }
    [ThreadStatic] private static Dictionary<Pair, Observation>? _preparation;
    [ThreadStatic] private static Dictionary<Pair, Observation>? _segment;
    [ThreadStatic] private static SubmissionScheduler? _segmentScheduler;
    internal static bool InPreparation => _preparation is not null;

    internal static Preparation BeginPreparation()
    {
        var previous = _preparation;
        _preparation = new();
        return new Preparation(previous);
    }

    internal readonly struct Preparation(Dictionary<Pair, Observation>? previous) : IDisposable
    {
        public void Dispose()
        {
            if (Enabled) _preparation = previous;
        }
    }

    // Called on the recording thread at each actual command-buffer begin (including
    // recycled worker buffers). Tick numbers alone are not recording identities.
    internal static void BeginSegment(SubmissionScheduler scheduler)
    {
        _segmentScheduler = scheduler;
        _segment = new();
    }

    internal static void Invalidate(ulong begin, ulong size)
    {
        if (size == 0) return;
        Storage.Instance.Invalidate(begin, size);
    }

    internal static void Invalidate(CachedImage image)
    {
        Invalidate(image.Description.Data.Address, image.Description.Data.Size);
        Invalidate(image.Description.Stencil.Address, image.Description.Stencil.Size);
        Invalidate(image.Description.Metadata.Range.Address, image.Description.Metadata.Range.Size);
    }

    internal static Action Publication(Action action)
    {
        Storage.Instance.BeginPublication();
        return () =>
        {
            try { action(); }
            finally { Storage.Instance.EndPublication(); }
        };
    }

    internal static void BeginPublication() => Storage.Instance.BeginPublication();
    internal static void EndPublication() => Storage.Instance.EndPublication();

    internal readonly record struct Pair(object Cache, ResourceSlotIdentifier Image, ImageViewDescription View);
    internal readonly record struct Observation(Snapshot State, long Revision, bool Ordered);
    internal readonly record struct Snapshot(CachedImage Image, ulong Handle, long GpuWrite, long Contents,
        ResourceSlotIdentifier Owner, bool Registered, bool Parked, bool Rebind, bool Clean, bool BufferOwner,
        bool GpuOwner, bool RawWrite, GuestSpan Data, GuestSpan Stencil, GuestSpan Metadata, bool HasMetadata,
        bool PublicationPending = false)
    {
        internal static Snapshot Of(CachedImage image) => new(image, image.Backing.Handle.Handle,
            image.GpuWriteSequence, image.ContentSequence, image.DepthOwner, image.Registered, image.Parked,
            image.Binding.NeedsRebind, !image.IsCpuDirty && !image.IsBufferModified, image.BufferHoldsGpuContents,
            image.IsGpuModified, image.RawWriteAfterGpuWrite, image.Description.Data, image.Description.Stencil,
            image.Description.Metadata.Range, image.Description.HasMetadata);
        // Metadata-backed images need additional buffer/clear-state validation beyond
        // the image dirty flags; exclude them from this conservative opportunity count.
        internal bool Eligible => Clean && Registered && !Parked && !Rebind && !Owner.IsValid && Handle != 0 &&
            !HasMetadata && !PublicationPending;
    }

    internal static Acquisition Begin(object cache, SubmissionScheduler scheduler, ResourceSlotIdentifier id,
        CachedImage image, in ImageRequest request, Caller caller)
    {
        if (request.Role != ImageRole.Texture) return default;
        // Calls outside a known recording segment still contribute frame statistics.
        var segment = ReferenceEquals(scheduler, _segmentScheduler) ? _segment : null;
        return Storage.Instance.Begin(new Pair(cache, id, request.View), Snapshot.Of(image), caller,
            !SubmissionScheduler.ReadOnlyThread && caller != Caller.Worker, _preparation, segment);
    }

    internal readonly struct Acquisition(Tracker? tracker, Pair key, CachedImage? image,
        Caller caller, bool ordered, Dictionary<Pair, Observation>? preparation,
        Dictionary<Pair, Observation>? segment, Snapshot before, long revision)
    {
        // Only successful acquisitions enter the denominator; capture post-acquisition
        // state, so an upload performed by this acquisition is not its own invalidation.
        internal void Complete()
        {
            if (Enabled && tracker is not null && image is not null)
                tracker.Complete(key, before, revision, Snapshot.Of(image), caller, ordered, preparation, segment);
        }
    }

    internal static void RecordFrame() => Storage.Instance.RecordFrame();
    internal static void WriteReport(double seconds) => Console.Error.WriteLine(Storage.Instance.TakeReport(seconds));

    // Separately constructible for deterministic tests without Vulkan or global state.
    internal sealed class Tracker
    {
        private const int EventCapacity = 8192;
        private const int PairCapacity = 262144;
        private readonly object _gate = new();
        private readonly (ulong Begin, ulong End)[] _events = new (ulong, ulong)[EventCapacity];
        private long _revision;
        private int _pendingPublications;
        private Dictionary<Pair, Observation> _frame = new(), _previous = new();
        private readonly long[] _callers = new long[5], _repeats = new long[4], _unchanged = new long[4];
        private readonly long[] _fullUnchanged = new long[4];
        private long _frames, _uniqueSum, _uniqueMax, _historyLost, _capacityResets, _workerObservations;
        private long _metadataObservations, _pendingObservations, _dirtyObservations;

        internal long Revision { get { lock (_gate) return _revision; } }

        internal void Invalidate(ulong begin, ulong size)
        {
            lock (_gate)
            {
                _events[_revision++ % EventCapacity] = (begin, size > ulong.MaxValue - begin ? ulong.MaxValue : begin + size);
            }
        }

        internal void BeginPublication()
        {
            lock (_gate)
            {
                _pendingPublications++;
                Invalidate(0, ulong.MaxValue);
            }
        }

        internal void EndPublication()
        {
            lock (_gate)
            {
                Invalidate(0, ulong.MaxValue);
                _pendingPublications--;
            }
        }

        internal Acquisition Begin(Pair key, Snapshot before, Caller caller, bool ordered,
            Dictionary<Pair, Observation>? preparation = null, Dictionary<Pair, Observation>? segment = null)
        {
            lock (_gate)
                return new Acquisition(this, key, before.Image, caller, ordered, preparation, segment,
                    before with { PublicationPending = _pendingPublications != 0 }, _revision);
        }

        private bool Unchanged(Observation previous, Snapshot current, long revision, bool ordered)
        {
            if (!ordered || !previous.Ordered || !previous.State.Eligible || !current.Eligible || previous.State != current)
                return false;
            if (revision - previous.Revision > EventCapacity)
            {
                _historyLost++;
                return false;
            }
            for (var index = previous.Revision; index < revision; index++)
            {
                var change = _events[index % EventCapacity];
                if (Overlaps(current.Data, change) || Overlaps(current.Stencil, change) ||
                    Overlaps(current.Metadata, change)) return false;
            }
            return true;
        }

        private static bool Overlaps(GuestSpan range, (ulong Begin, ulong End) change) =>
            range.Size != 0 && range.Address < change.End && change.Begin < range.End;

        internal void Complete(Pair key, Snapshot before, long revision, Snapshot after, Caller caller, bool ordered,
            Dictionary<Pair, Observation>? preparation, Dictionary<Pair, Observation>? segment)
        {
            lock (_gate)
            {
                _callers[(int)caller]++;
                if (!ordered) _workerObservations++;
                if (before.HasMetadata) _metadataObservations++;
                if (before.PublicationPending || _pendingPublications != 0) _pendingObservations++;
                if (!before.Clean) _dirtyObservations++;
                var observation = new Observation(after with { PublicationPending = _pendingPublications != 0 },
                    _revision, ordered && revision == _revision);
                Count(preparation, 0, update: true);
                Count(segment, 1, update: true);
                Count(_frame, 2, update: true);
                Count(_previous, 3, update: false);

                void Count(Dictionary<Pair, Observation>? uses, int scope, bool update)
                {
                    if (uses is null) return;
                    if (uses.TryGetValue(key, out var previous))
                    {
                        _repeats[scope]++;
                        // A concurrent invalidation during acquisition makes its result uncertain.
                        var unchanged = revision == _revision && Unchanged(previous, before, revision, ordered);
                        if (unchanged)
                        {
                            _unchanged[scope]++;
                            if (caller is Caller.Stage or Caller.Prologue or Caller.Other) _fullUnchanged[scope]++;
                        }
                        // The previous-frame baseline is immutable, but its event-history
                        // validation can advance. A rejected baseline stays rejected until flip.
                        if (!update) uses[key] = previous with { Revision = revision, Ordered = previous.Ordered && unchanged };
                    }
                    if (!update) return;
                    if (uses.Count >= PairCapacity && !uses.ContainsKey(key))
                    {
                        uses.Clear();
                        _capacityResets++;
                    }
                    uses[key] = observation;
                }
            }
        }

        internal void RecordFrame()
        {
            lock (_gate)
            {
                _frames++;
                _uniqueSum += _frame.Count;
                _uniqueMax = Math.Max(_uniqueMax, _frame.Count);
                (_previous, _frame) = (_frame, _previous);
                _frame.Clear();
            }
        }

        internal string TakeReport(double seconds)
        {
            lock (_gate)
            {
                var report = FormattableString.Invariant($"[PERF][SAMPLED_REUSE] window_s={seconds:F3} sampled={_callers.Sum()} stage={_callers[0]} worker_view={_callers[1]} slice_prologue={_callers[2]} memo_hit={_callers[3]} other={_callers[4]} worker_observations={_workerObservations} metadata_observations={_metadataObservations} pending_observations={_pendingObservations} dirty_observations={_dirtyObservations} repeat_preparation={_repeats[0]} repeat_segment={_repeats[1]} repeat_frame={_repeats[2]} repeat_previous_frame={_repeats[3]} unchanged_preparation={_unchanged[0]} unchanged_segment={_unchanged[1]} unchanged_frame={_unchanged[2]} unchanged_previous_frame={_unchanged[3]} full_unchanged_preparation={_fullUnchanged[0]} full_unchanged_segment={_fullUnchanged[1]} full_unchanged_frame={_fullUnchanged[2]} full_unchanged_previous_frame={_fullUnchanged[3]} frames={_frames} frame_unique_sum={_uniqueSum} frame_unique_max={_uniqueMax} history_lost={_historyLost} capacity_resets={_capacityResets}");
                Array.Clear(_callers);
                Array.Clear(_repeats);
                Array.Clear(_unchanged);
                Array.Clear(_fullUnchanged);
                _frames = _uniqueSum = _uniqueMax = _historyLost = _capacityResets = _workerObservations = 0;
                _metadataObservations = _pendingObservations = _dirtyObservations = 0;
                return report;
            }
        }
    }
}
