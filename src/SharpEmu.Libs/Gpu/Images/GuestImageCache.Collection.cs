// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

// Garbage collection by recency and memory pressure, and the scheduled readback flush.
public sealed partial class GuestImageCache
{
    public void RunGarbageCollector() => RunGarbageCollector(endsFrame: true);

    // Image ages are counted in frames. Collection also runs after guest submissions, so memory
    // pressure is relieved during long stretches without a flip, but only a frame advances the
    // age: a title submits dozens of command buffers per frame, and counting those made every
    // texture it samples once per frame look stale, so it was deleted and uploaded again.
    public void RunGarbageCollector(bool endsFrame)
    {
        using var held = _lock.Hold();
        var tick = endsFrame ? _collectionTick++ : _collectionTick;
        _allocationCollectionBlocked = false;
        if (MemoryUnderPressure)
        {
            // Idle pool memory is kept for reuse until the cache is short of memory.
            ReleaseUnusedMemoryCore();
        }

        if (CollectionMemoryBytes < _collectionStartBytes)
        {
            return;
        }

        Collect(tick, allowAggressive: false);
        if (CollectionMemoryBytes >= _criticalMemoryBytes)
        {
            Collect(tick, allowAggressive: true);
        }
    }

    public void ReleaseUnusedMemory()
    {
        using var held = _lock.Hold();
        ReleaseUnusedMemoryCore();
    }

    // Allocation-time pressure is separate from the periodic sweep: a burst of draws can create
    // thousands of images before the next frame boundary. Destruction still goes through
    // DeleteImage's completion action, so no image is freed while the current tick can use it.
    private void CollectForAllocation(ulong requiredBytes)
    {
        FinishRetiredImagesForAllocation(requiredBytes);
        // A burst can contain only newly touched images. Once the recency walk finds nothing
        // evictable, repeating it for every allocation in the same tick is wasted work; the next
        // collection makes older images eligible.
        if (_allocationCollectionBlocked)
        {
            return;
        }

        var current = CollectionMemoryBytes;
        if (requiredBytes > _criticalMemoryBytes || current <= _criticalMemoryBytes - requiredBytes)
        {
            return;
        }

        var target = _criticalMemoryBytes - requiredBytes;
        var blocked = false;
        // Retired images still occupy memory until completion; do not retire the whole old
        // working set merely because that completion is pending.
        while (CollectionMemoryBytes - (ulong)Interlocked.Read(ref _retiredImageMemoryBytes) > target)
        {
            var before = _totalUsedMemory;
            Collect(_collectionTick, allowAggressive: true);
            if (_totalUsedMemory == before)
            {
                blocked = true;
                break;
            }
        }

        _allocationCollectionBlocked = blocked;
        FinishRetiredImagesForAllocation(requiredBytes);
    }

    private void FinishRetiredImagesForAllocation(ulong requiredBytes)
    {
        if (Interlocked.Read(ref _retiredImageMemoryBytes) == 0 || !_scheduler.Active || _scheduler.InsideTickCallback)
        {
            return;
        }

        if (!RetirementOverBudget && requiredBytes <= _criticalMemoryBytes &&
            CollectionMemoryBytes <= _criticalMemoryBytes - requiredBytes)
        {
            return;
        }

        // Completion destroys the retired images and can call back into the cache: never wait
        // while holding its lock. Images this preparation touched stay registered; only the
        // previously retired ones are destroyed.
        _lock.Exit();
        try
        {
            _scheduler.Finish();
        }
        finally
        {
            _lock.Enter();
        }

        ReleaseUnusedMemoryCore();
        _allocationCollectionBlocked = false;
    }

    // An allocation failed despite the cache's own accounting: the device is full. Retire every
    // image collection allows, wait for the retired ones to be destroyed and hand the pools'
    // idle memory back to the device.
    public void ReclaimForAllocation()
    {
        using var held = _lock.Hold();
        ReclaimAfterFailedAllocation();
    }

    private void ReclaimAfterFailedAllocation()
    {
        for (var rounds = 0; rounds < 64; rounds++)
        {
            var before = _totalUsedMemory;
            Collect(_collectionTick, allowAggressive: true, reclaim: true);
            if (_totalUsedMemory == before)
            {
                break;
            }
        }

        // Completion destroys the retired images and can call back into the cache, so the wait
        // happens without its lock.
        if (_scheduler.Active && !_scheduler.InsideTickCallback)
        {
            _lock.Exit();
            try
            {
                _scheduler.Finish();
            }
            finally
            {
                _lock.Enter();
            }
        }

        ReleaseUnusedMemoryCore();
    }

    // Idle memory the pools keep for reuse: retired backings, empty image blocks and buffer slab
    // chunks without a live buffer. Registered images and live buffers are untouched.
    private void ReleaseUnusedMemoryCore()
    {
        _backingPool?.ReleaseRetained();
        _imageMemoryPool.ReleaseRetained();
        _device.Slabs.ReleaseUnused();
    }

    private void Collect(ulong tick, bool allowAggressive, bool reclaim = false)
    {
        var pressured = reclaim || CollectionMemoryBytes >= _memoryPressureBytes;
        var aggressive = reclaim || (allowAggressive && CollectionMemoryBytes >= _criticalMemoryBytes);
        // Under pressure only the images the current frame touched are kept: the work being
        // recorded needs those, and whatever it already references is destroyed after its
        // tick completes. Waiting more frames as memory fills would stop reclamation in a
        // slow title.
        var age = pressured ? 1UL : 16UL;
        if (tick < age)
        {
            return;
        }

        var deletions = aggressive ? 40 : pressured ? 20 : 10;
        var candidates = new List<ResourceSlotIdentifier>(deletions);
        // Deleting a depth image also deletes its stencil association, so the recency walk ends first.
        _recencyQueue.ForEachItemAtOrBeforeTick(tick - age, imageIdentifier =>
        {
            candidates.Add(imageIdentifier);
            // A protected prefix must not hide older reclaimable images behind it: the walk takes
            // every candidate and the budget below counts retirements, not candidates visited.
            return false;
        });
        foreach (var imageIdentifier in candidates)
        {
            if (deletions <= 0)
            {
                break;
            }

            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null || !owner.Registered || owner.DepthOwner.IsValid || _lookupPins.ContainsKey(imageIdentifier))
            {
                continue;
            }

            if (reclaim && (owner.Binding.IsBound || owner.Binding.IsTarget))
            {
                // The draw state being built still holds this image's views.
                continue;
            }

            if (owner.IsGpuModified)
            {
                var safe = CanReadBack(owner);
                if (safe && owner.Description.IsTiled)
                {
                    continue;
                }

                if (safe && !pressured)
                {
                    continue;
                }

                if (safe && !TryDownloadToGuest(imageIdentifier))
                {
                    continue;
                }

                owner.ClearGpuModified();
            }

            deletions -= DeleteImage(imageIdentifier);
            if (reclaim)
            {
                continue;
            }

            if (CollectionMemoryBytes < _criticalMemoryBytes && aggressive)
            {
                deletions >>= 2;
                aggressive = false;
            }

            if (CollectionMemoryBytes < _memoryPressureBytes && pressured)
            {
                deletions >>= 1;
                pressured = false;
            }
        }
    }

    // Publishes every scheduled linear GPU-written image to guest memory.
    public void FlushScheduledReadbacks()
    {
        using var held = _lock.Hold();
        foreach (var imageIdentifier in _scheduledReadbacks)
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner != null && owner.Registered && owner.IsGpuModified)
            {
                _ = TryDownloadToGuest(imageIdentifier);
            }
        }

        _scheduledReadbacks.Clear();
    }

    // Test seams: thresholds, the recency order and the private state the tests inspect.
    internal void SetCollectionThresholds(ulong trigger, ulong pressure, ulong critical, ulong tick)
    {
        _collectionStartBytes = trigger;
        _memoryPressureBytes = pressure;
        _criticalMemoryBytes = critical;
        _collectionTick = tick;
    }

    internal void ResetRecency(ReadOnlySpan<ResourceSlotIdentifier> oldest, ulong tick)
    {
        var live = new List<ResourceSlotIdentifier>();
        _recencyQueue = new RecencyQueue<ResourceSlotIdentifier>();
        _slots.ForEach((imageIdentifier, image) =>
        {
            if (image.Registered)
            {
                image.LastAccessTick = _scheduler.CurrentTick;
                live.Add(imageIdentifier);
            }
        });
        foreach (var imageIdentifier in oldest)
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner != null && owner.Registered)
            {
                owner.LastAccessTick = 0;
                owner.RecencyEntryIndex = _recencyQueue.Insert(imageIdentifier, 0);
            }
        }

        foreach (var imageIdentifier in live)
        {
            if (oldest.IndexOf(imageIdentifier) < 0)
            {
                _slots[imageIdentifier].RecencyEntryIndex = _recencyQueue.Insert(imageIdentifier, tick);
            }
        }
    }

    internal bool Contains(ResourceSlotIdentifier imageIdentifier) => _slots.TryGet(imageIdentifier) is { Registered: true };

    internal CachedImage? Owner(ResourceSlotIdentifier imageIdentifier) => _slots.TryGet(imageIdentifier);

    internal bool IsReadbackScheduled(ResourceSlotIdentifier imageIdentifier) => _scheduledReadbacks.Contains(imageIdentifier);

    internal int NullImageCount => _nullImages.Count;

    internal void SetLinearReadback(bool enabled) => _readbackLinearImages = enabled;

    internal List<ResourceSlotIdentifier> FindImagesInRangeForTest(ulong address, ulong size, bool pageOverlap)
    {
        using var held = _lock.Hold();
        var result = new List<ResourceSlotIdentifier>();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap)) result.Add(imageIdentifier);
        return result;
    }

    internal int PageOwnerCount(ulong address)
    {
        using var held = _lock.Hold();
        return _pageOwners.Find(address >> ImagePageOwnerTable.PageBits)?.Count ?? 0;
    }

    internal int OwnedPageCount(ulong address, ulong size, ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (!ImagePageOwnerTable.TryGetPageRange(address, size, out var first, out var lastExclusive))
        {
            return 0;
        }

        var count = 0;
        for (var page = first; page < lastExclusive; page++)
        {
            count += _pageOwners.Find(page)?.Contains(imageIdentifier) == true ? 1 : 0;
        }

        return count;
    }

    internal void AddPageOwner(ulong address, ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        InvalidateLookups();
        _pageOwners.GetOrCreate(address >> ImagePageOwnerTable.PageBits).Add(imageIdentifier);
    }

    internal bool RemovePageOwner(ulong address, ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        InvalidateLookups();
        return _pageOwners.Find(address >> ImagePageOwnerTable.PageBits)?.Remove(imageIdentifier) == true;
    }

    internal uint QueryEpoch
    {
        get
        {
            using var held = _lock.Hold();
            return _queryEpoch;
        }
        set
        {
            using var held = _lock.Hold();
            _queryEpoch = value;
        }
    }

    internal ResourceSlotIdentifier InsertImageForTest(in ImageDescription description)
    {
        using var held = _lock.Hold();
        return InsertImage(description);
    }

    internal void DeleteImageForTest(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        DeleteImage(imageIdentifier);
    }

    internal void ScheduleReadbackForTest(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        ScheduleReadback(imageIdentifier, _slots[imageIdentifier]);
    }

    internal void AssociateStencilForTest(ResourceSlotIdentifier depth, HLE.GpuMemory.GuestSpan stencil)
    {
        using var held = _lock.Hold();
        AssociateStencilRange(depth, stencil);
    }

    internal bool TryDownloadForTest(ResourceSlotIdentifier imageIdentifier) => TryDownloadToGuest(imageIdentifier);

    internal GpuTiler TilerForTest => _tiler;

    internal void RegisterHtileMetadataForTest(ulong address, ulong size, uint layers)
    {
        RegisterHtileMetadataForTest(address);
        using var held = _lock.Hold();
        _surfaceMetadata[address].Size = size;
        _surfaceMetadata[address].Layers = layers;
    }

    internal void RegisterHtileMetadataForTest(ulong address)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var metadata))
        {
            metadata = new SurfaceMetadata();
            _surfaceMetadata.Add(address, metadata);
        }

        metadata.Kind = SurfaceMetadataKind.HTile;
        metadata.SetFromMask(0);
    }

    internal void RegisterDccMetadataForTest(ulong address, ulong size)
    {
        using var held = _lock.Hold();
        _surfaceMetadata[address] = new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc, Size = size };
    }

    internal RegionLockScope HoldLockForTest() => new(_lock);

    internal readonly struct RegionLockScope : IDisposable
    {
        private readonly HLE.GpuMemory.RegionLock _lock;

        public RegionLockScope(HLE.GpuMemory.RegionLock regionLock)
        {
            _lock = regionLock;
            regionLock.Enter();
        }

        public void Dispose() => _lock.Exit();
    }
}
