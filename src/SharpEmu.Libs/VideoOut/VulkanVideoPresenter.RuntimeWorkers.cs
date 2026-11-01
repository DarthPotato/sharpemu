// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly bool RuntimeRecordWorkers = RuntimeDescriptorWorkerOptions.Enabled;
    private static readonly ThreadCounter _runtimeWorkerRecords = new();
    private static readonly ThreadCounter _runtimeJoinRejects = new();
    private static readonly ThreadCounter _runtimeEpochPublished = new();
    private static readonly ThreadCounter _runtimeEpochKept = new();
    private static readonly ThreadCounter _runtimeFeedbackNew = new();
    private static readonly ThreadCounter _runtimeFeedbackReuse = new();
    private static readonly ConcurrentDictionary<string, ThreadCounter> _runtimeUnstable = new();

    private static string TakeRuntimeWorkerReport() =>
        $"runtime_worker_records={_runtimeWorkerRecords.Take()} runtime_join_rejects={_runtimeJoinRejects.Take()} runtime_epoch_published={_runtimeEpochPublished.Take()} runtime_epoch_kept={_runtimeEpochKept.Take()} runtime_feedback=new:{_runtimeFeedbackNew.Take()},reuse:{_runtimeFeedbackReuse.Take()} runtime_epoch_unstable=" +
        string.Join(",", _runtimeUnstable.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.Take()}")) + " ";

    private sealed partial class Presenter
    {
        private ConcurrentDictionary<(ShaderStageKind, ulong), RuntimeEpoch>? _runtimeEpochs;
        private RuntimeFeedbackPool? _runtimeFeedbackPool;
        internal RuntimeFeedbackPool FeedbackPool => _runtimeFeedbackPool ??
            LazyInitializer.EnsureInitialized(ref _runtimeFeedbackPool, () => new RuntimeFeedbackPool(_deviceInfo, _scheduler));
        internal ConcurrentDictionary<(ShaderStageKind, ulong), RuntimeEpoch> RuntimeEpochs => LazyInitializer.EnsureInitialized(ref _runtimeEpochs);

        internal void DestroyRuntimeWorkerResources()
        {
            if (_runtimeEpochs is { } epochs)
            {
                foreach (var epoch in epochs.Values) epoch.Lease.Retire(epoch.Table.ReleaseForeignRead);
                epochs.Clear();
            }
            _runtimeFeedbackPool?.Dispose();
        }

        private bool ValidateRuntimeUses(SliceRequests requests)
        {
            if (requests.RuntimeUses is not { } uses) return true;
            foreach (var use in uses)
            {
                var epoch = use.Epoch;
                if (!epoch.Stable || !RuntimeEpochs.TryGetValue(epoch.Key, out var current) || !ReferenceEquals(current, epoch))
                {
                    _runtimeJoinRejects.Increment();
                    return false;
                }
            }
            return true;
        }
    }

    private sealed record RuntimeEpochImage(TextureResource Binding, ImageView View, uint Slot, uint[] Key, RuntimeDescriptorLease SlotLease, long SlotGeneration);

    // Only the ordered host constructs snapshots. No worker reads mutable registry entries.
    private sealed class RuntimeEpoch(RuntimeDescriptorRegistry<TextureResource> registry, long version,
        GpuBuffer table, RuntimeEpochImage[] images, RuntimeDescriptorHeap heap, (ShaderStageKind, ulong) key)
    {
        public readonly RuntimeDescriptorRegistry<TextureResource> Registry = registry;
        // Heap identity is retained for diagnostics; unrelated slots do not invalidate this epoch.
        public readonly RuntimeDescriptorHeap Heap = heap;
        public readonly long Version = version;
        public readonly GpuBuffer Table = table;
        public readonly RuntimeEpochImage[] Images = images;
        public readonly RuntimeDescriptorLease Lease = new();
        public readonly (ShaderStageKind, ulong) Key = key;
        public string? UnstableReason => Registry.PendingMisses != 0 ? "pending_misses" :
            Registry.TableDirty || Registry.Version != Version ? "registry_changed" : Images.Any(image => image.SlotLease.Generation != image.SlotGeneration) ? "slot_changed" : null;
        public bool Stable => UnstableReason is null;
    }

    // Shared per presenter, allocated lazily only on the enabled path. A pair returns here only
    // after discard of unsubmitted commands or completion of the ordered feedback copy.
    private sealed class RuntimeFeedbackPool(GpuDeviceInfo device, SubmissionScheduler scheduler) : IDisposable
    {
        private readonly ConcurrentBag<(GpuBuffer Misses, GpuBuffer Readback)> _free = [];
        public (GpuBuffer Misses, GpuBuffer Readback) Rent()
        {
            if (_free.TryTake(out var pair)) { _runtimeFeedbackReuse.Increment(); return pair; }
            const ulong bytes = RuntimeDescriptorTable.MissBufferDwords * sizeof(uint);
            var misses = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, bytes);
            try
            {
                var readback = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, bytes);
                _runtimeFeedbackNew.Increment();
                return (misses, readback);
            }
            catch { misses.Dispose(); throw; }
        }
        public void Return(GpuBuffer misses, GpuBuffer readback) => _free.Add((misses, readback));
        // Called after workers and GPU work have finished, alongside runtime descriptor teardown.
        public void Dispose()
        {
            while (_free.TryTake(out var pair)) { pair.Misses.Dispose(); pair.Readback.Dispose(); }
        }
    }

    // One shader's feedback in one segment. It cannot race another segment or an inline draw.
    private sealed class RuntimeWorkerUse(RuntimeEpoch epoch, GpuBuffer misses, GpuBuffer readback)
    {
        public readonly RuntimeEpoch Epoch = epoch;
        public readonly GpuBuffer Misses = misses;
        public readonly GpuBuffer Readback = readback;
        public readonly List<RuntimeDescriptorLease> Slots = [];
        public RuntimeFeedbackPool? Pool;
        private const ulong Bytes = RuntimeDescriptorTable.MissBufferDwords * sizeof(uint);

        public void ReleasePins()
        {
            foreach (var slot in Slots) slot.Unpin();
            Epoch.Lease.Unpin();
        }

        public void Discard()
        {
            if (Pool is { } pool) pool.Return(Misses, Readback);
            else { Misses.Dispose(); Readback.Dispose(); }
            ReleasePins();
        }

        // Called only after this segment has been appended to the ordered queue. Copy in the
        // following command buffer: its completion necessarily follows the segment's writes.
        public void Adopt(SubmissionScheduler scheduler)
        {
            Readback.CopyFrom(scheduler.Current, Misses, 0, 0, Bytes,
                AccessFlags.ShaderWriteBit, AccessFlags.None, AccessFlags.ShaderReadBit, AccessFlags.HostReadBit);
            scheduler.QueueCompletionAction(() =>
            {
                Readback.Invalidate(0, Bytes);
                Epoch.Registry.ReadMisses(MemoryMarshal.Cast<byte, uint>(Readback.Mapped));
                Discard();
            });
        }
    }

    private sealed partial class RecordingHost
    {
        private bool _runtimeItemRecorded;
        private static SerialRequiredException UnstableRuntime(string reason)
        {
            _runtimeUnstable.GetOrAdd(reason, static _ => new ThreadCounter()).Increment();
            return Serial("runtime image descriptors", resumable: true);
        }

        private void PrepareRuntimeWorker(PreparedStageBindings prepared)
        {
            var key = (prepared.Program.Stage, prepared.Program.Hash);
            if (!_presenter.RuntimeEpochs.TryGetValue(key, out var epoch)) throw UnstableRuntime("unpublished");
            if (epoch.UnstableReason is { } reason) throw UnstableRuntime(reason);
            var uses = Requests.RuntimeUses ??= [];
            var use = uses.FirstOrDefault(x => ReferenceEquals(x.Epoch, epoch));
            if (use is null)
            {
                if (!epoch.Lease.TryPin()) throw UnstableRuntime("retired_table");
                var slots = new List<RuntimeDescriptorLease>();
                try
                {
                    foreach (var image in epoch.Images)
                    {
                        var pin = _runtimeDescriptorHeap!.TryPinImageSlot(image.View, image.Slot, image.SlotLease, image.SlotGeneration);
                        if (pin is null) throw UnstableRuntime("slot_changed");
                        slots.Add(pin);
                    }
                    var pool = _presenter.FeedbackPool;
                    var pair = pool.Rent();
                    use = new RuntimeWorkerUse(epoch, pair.Misses, pair.Readback) { Pool = pool };
                    use.Slots.AddRange(slots);
                    uses.Add(use);
                }
                catch
                {
                    foreach (var pin in slots) pin.Unpin();
                    epoch.Lease.Unpin();
                    throw;
                }
                EndRendering();
                use.Misses.Fill(_workerBuffer!, 0, use.Misses.Size, 0);
            }
            prepared.RuntimeEpoch = epoch;
            foreach (var image in epoch.Images)
            {
                BindImage(image.Binding.ImageIdentifier, storage: false);
                prepared.RuntimeImages.Add(CopyRuntimeBinding(image.Binding));
            }
        }

        private void BindRuntimeWorker(PreparedStageBindings prepared)
        {
            var epoch = prepared.RuntimeEpoch!;
            for (var index = 0; index < epoch.Images.Length; index++)
            {
                var binding = prepared.RuntimeImages[index];
                binding.View = AcquireTextureViewForSlice(binding.ImageIdentifier, in binding.Request);
                if (binding.View.Handle != epoch.Images[index].View.Handle) throw UnstableRuntime("view_changed");
                binding.MipViews = [];
                binding.CachedImage = Image(binding.ImageIdentifier);
                binding.Image = binding.CachedImage.Backing.Handle;
            }
        }

        private void CommitRuntimeWorker(PreparedStageBindings prepared)
        {
            var epoch = prepared.RuntimeEpoch!;
            for (var index = 0; index < epoch.Images.Length; index++)
            {
                // Rendering preparation can replace a sampled view with a feedback/stencil copy.
                // Such a draw needs ordered slot allocation, even if acquisition was stable earlier.
                if (prepared.RuntimeImages[index].View.Handle != epoch.Images[index].View.Handle)
                    throw UnstableRuntime("view_changed");
            }
            var use = Requests.RuntimeUses!.First(x => ReferenceEquals(x.Epoch, epoch));
            prepared.Descriptors.RuntimeTable = new BufferView(epoch.Table.Handle, 0, epoch.Table.Size);
            prepared.Descriptors.RuntimeMisses = new BufferView(use.Misses.Handle, 0, use.Misses.Size);
            _runtimeItemRecorded = true;
        }

        private void PublishRuntimeEpoch(PreparedStageBindings prepared, RuntimeDescriptorSet descriptors, GpuBuffer table)
        {
            if (!RuntimeRecordWorkers) return;
            var key = (prepared.Program.Stage, prepared.Program.Hash);
            if (!descriptors.Registry.CanPublishWorkerEpoch)
            {
                var reason = descriptors.Registry.PendingMisses != 0 ? "pending_misses" : descriptors.Registry.TableDirty ? "dirty_table" : "new_slot";
                _runtimeUnstable.GetOrAdd(reason, static _ => new ThreadCounter()).Increment();
                return;
            }
            if (_presenter.RuntimeEpochs.TryGetValue(key, out var old) &&
                old.Stable && old.Version == descriptors.Registry.Version && ReferenceEquals(old.Table, table))
            {
                _runtimeEpochKept.Increment();
                return;
            }
            var images = new RuntimeEpochImage[prepared.RuntimeImages.Count];
            for (var index = 0; index < images.Length; index++)
            {
                var binding = prepared.RuntimeImages[index];
                var entry = prepared.RuntimeEntries[index];
                var slotLease = _runtimeDescriptorHeap!.ImageSlotLease(binding.View, entry.Slot);
                if (slotLease is null) { _runtimeUnstable.GetOrAdd("slot_changed", static _ => new ThreadCounter()).Increment(); return; }
                images[index] = new RuntimeEpochImage(new TextureResource
                {
                    Address = binding.Address, ImageIdentifier = binding.ImageIdentifier, Request = binding.Request,
                    DestinationSelect = binding.DestinationSelect, Width = binding.Width, Height = binding.Height,
                }, binding.View, entry.Slot, entry.Key.ToArray(), slotLease, slotLease.Generation);
            }
            table.RetainForeignRead();
            var epoch = new RuntimeEpoch(descriptors.Registry, descriptors.Registry.Version, table, images, _runtimeDescriptorHeap!, key);
            _presenter.RuntimeEpochs[key] = epoch;
            _runtimeEpochPublished.Increment();
            old?.Lease.Retire(old.Table.ReleaseForeignRead);
        }
    }
}
