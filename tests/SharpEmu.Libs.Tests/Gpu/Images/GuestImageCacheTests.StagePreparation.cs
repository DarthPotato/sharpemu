// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void StagePreparationChecksEveryRangeAgainstOriginalGenerationAndTouchesImages()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x200000, ReadWrite);
        var first = Sampled(address);
        var second = Sampled(address + 0x10000);
        var firstId = harness.Find(ref first);
        var secondId = harness.Find(ref second);
        GuestImageCache.RememberedStageImage[] bindings =
        [
            new(firstId, harness.Images.RememberedRange(firstId, first), true),
            new(secondId, harness.Images.RememberedRange(secondId, second), true),
        ];
        var generation = harness.Images.LookupGeneration;
        var staleId = new ResourceSlotIdentifier(firstId.Index, firstId.Generation + 1);
        harness.Image(firstId).LastAccessTick = 0;
        harness.Image(secondId).LastAccessTick = 0;
        Assert.True(harness.Worker.Run(() => harness.Images.TryTouchStagePreparation(bindings, ref generation)));
        Assert.True(harness.Image(firstId).LastAccessTick > 0);
        Assert.Equal(harness.Image(firstId).LastAccessTick, harness.Image(secondId).LastAccessTick);

        // Lookup changes elsewhere can be revalidated with the existing bounded event history.
        harness.Images.AddPageOwner(address + 0x100000, staleId);
        Assert.True(harness.Images.RemovePageOwner(address + 0x100000, staleId));
        Assert.True(harness.Worker.Run(() => harness.Images.TryTouchStagePreparation(bindings, ref generation)));
        Assert.Equal(harness.Images.LookupGeneration, generation);

        // First range revalidates, second overlaps. Advancing the common generation after
        // the first range would incorrectly hide the second range's invalidation.
        var before = generation;
        harness.Images.AddPageOwner(second.Description.Data.Address, staleId);
        Assert.True(harness.Images.RemovePageOwner(second.Description.Data.Address, staleId));
        Assert.Equal(GuestImageCache.StagePreparationFailure.RangeInvalidated,
            harness.Worker.Run(() => harness.Images.TouchStagePreparation(bindings, ref generation)));
        Assert.Equal(before, generation);

        // A reused slot index with another slot generation is never a valid binding.
        generation = harness.Images.LookupGeneration;
        bindings[0] = bindings[0] with { Image = staleId };
        Assert.Equal(GuestImageCache.StagePreparationFailure.SlotGeneration,
            harness.Worker.Run(() => harness.Images.TouchStagePreparation(bindings, ref generation)));
        harness.Shutdown();
    }

    [Fact]
    public void StagePreparationRejectsLookupHistoryOverflowEvenForUnrelatedChanges()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x200000, ReadWrite);
        var request = Sampled(address);
        var id = harness.Find(ref request);
        GuestImageCache.RememberedStageImage[] bindings = [new(id, harness.Images.RememberedRange(id, request), true)];
        var generation = harness.Images.LookupGeneration;
        var before = generation;
        var fake = new ResourceSlotIdentifier(id.Index, id.Generation + 1);
        // Each add/remove pair records two lookup events.
        for (var index = 0; index < GuestImageCache.LookupHistorySlots / 2 + 1; index++)
        {
            harness.Images.AddPageOwner(address + 0x100000, fake);
            Assert.True(harness.Images.RemovePageOwner(address + 0x100000, fake));
        }
        Assert.Equal(GuestImageCache.StagePreparationFailure.HistoryOverflow,
            harness.Worker.Run(() => harness.Images.TouchStagePreparation(bindings, ref generation)));
        Assert.Equal(before, generation);
        harness.Shutdown();
    }

    [Theory]
    [InlineData("unregistered")]
    [InlineData("parked")]
    [InlineData("depth-owner")]
    [InlineData("rebind")]
    public void StagePreparationRejectsChangedBindingStateWithoutLookupGenerationChange(string state)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var request = Sampled(address);
        var id = harness.Find(ref request);
        var image = harness.Image(id);
        GuestImageCache.RememberedStageImage[] bindings = [new(id, harness.Images.RememberedRange(id, request), true)];
        var generation = harness.Images.LookupGeneration;
        Assert.True(harness.Worker.Run(() => harness.Images.TryTouchStagePreparation(bindings, ref generation)));
        var registered = image.Registered;
        var parked = image.Parked;
        var depthOwner = image.DepthOwner;
        var needsRebind = image.Binding.NeedsRebind;
        try
        {
            switch (state)
            {
                case "unregistered": image.Registered = false; break;
                case "parked": image.Parked = true; break;
                case "depth-owner": image.DepthOwner = id; break;
                case "rebind": image.Binding.NeedsRebind = true; break;
            }
            var expected = state switch
            {
                "unregistered" => GuestImageCache.StagePreparationFailure.Unregistered,
                "parked" => GuestImageCache.StagePreparationFailure.Parked,
                "depth-owner" => GuestImageCache.StagePreparationFailure.DepthOwner,
                _ => GuestImageCache.StagePreparationFailure.Rebind,
            };
            Assert.Equal(expected, harness.Worker.Run(() => harness.Images.TouchStagePreparation(bindings, ref generation)));
            if (state == "parked")
                Assert.Equal(expected, harness.Worker.Run(() => harness.Images.TouchRememberedForProfile(id, ref generation, bindings[0].Range, false)));
            else
                Assert.Equal(expected, harness.Images.StageImageStateForProfile(id));
        }
        finally
        {
            image.Registered = registered;
            image.Parked = parked;
            image.DepthOwner = depthOwner;
            image.Binding.NeedsRebind = needsRebind;
        }
        Assert.True(harness.Worker.Run(() => harness.Images.TryTouchStagePreparation(bindings, ref generation)));
        harness.Shutdown();
    }

    [Fact]
    public void StagePreparationPreservesEmptyRequestAndEmptyStageSemantics()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var request = Sampled(0);
        request.Description.Data = GuestSpan.Empty;
        request.Description.Pitch = 0;
        var id = harness.Find(ref request);
        var image = harness.Image(id);
        GuestImageCache.RememberedStageImage[] bindings = [new(id, GuestSpan.Empty, false)];
        ulong generation = 0;
        Assert.False(image.Registered);
        Assert.True(harness.Images.TryTouchStagePreparation([], ref generation));
        Assert.True(harness.Images.TryTouchStagePreparation(bindings, ref generation));
        Assert.Equal(0UL, generation);
        image.Binding.NeedsRebind = true;
        Assert.False(harness.Images.TryTouchStagePreparation(bindings, ref generation));
        image.Binding.NeedsRebind = false;
        harness.Shutdown();
    }
}
