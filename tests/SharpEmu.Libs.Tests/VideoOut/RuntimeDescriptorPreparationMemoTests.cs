// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class RuntimeDescriptorPreparationMemoTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Property(object target, string name) => target.GetType().GetProperty(name, Members)!.GetValue(target)!;
    private static object Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
    private static bool Memoized(object prepared) => (bool)Property(prepared, "Memoized");
    private static IList RuntimeImages(object prepared) => (IList)Property(prepared, "RuntimeImages");
    private static ShaderProgramInfo Program(ShaderResourceInfo? info = null) => new()
    {
        Stage = ShaderStageKind.Pixel, Hash = 0xD35C1234, Bindings = new BindingLayout(),
        Resources = new SpecializedResourceInfo { Info = info ?? new ShaderResourceInfo { UsesRuntimeDescriptors = true } },
    };

    private static object Registry(PresenterUnderTest presenter, ShaderProgramInfo program) =>
        Field(presenter.InvokeMethod("RuntimeDescriptorsFor", program)!, "Registry");

    // Feed exactly the words the completed GPU feedback buffer would contain.
    private static void Feedback(object registry, uint[]? image = null)
    {
        var words = new uint[RuntimeDescriptorTable.MissBufferDwords];
        if (image is null) words[RuntimeDescriptorTable.SamplerMissCountDword] = 1;
        else
        {
            words[RuntimeDescriptorTable.ImageMissCountDword] = 1;
            words[RuntimeDescriptorTable.ImageMissOffset] = RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2D);
            image.CopyTo(words, (int)RuntimeDescriptorTable.ImageMissOffset + 1);
        }
        typeof(RuntimeDescriptorPreparationMemoTests).GetMethod(nameof(ReadMisses), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(registry.GetType().GenericTypeArguments[0]).Invoke(null, [registry, words]);
    }

    private static void ReadMisses<T>(object registry, uint[] words) where T : class =>
        ((RuntimeDescriptorRegistry<T>)registry).ReadMisses(words);

    [Fact]
    public void MemoHitConsumesChangedRuntimeWordsAndRebindsChangedRuntimeImages()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan, runtimeDescriptors: true)) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan);
        using var heap = presenter.Run(() => new RuntimeDescriptorHeap(fixture.Vulkan.DeviceInfo,
            presenter.Harness.Scheduler, fixture.Vulkan.RuntimeDescriptorLimits));
        presenter.SetField("_runtimeDescriptorHeap", heap);
        var address = presenter.Harness.MapBacked(0x200000, ReadWrite);
        var program = Program();
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            var stage = new ShaderStageResources(program, new ResourceSnapshot());
            var registry = Registry(presenter, program);
            Feedback(registry, RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 8, 8));
            var first = presenter.RenderHost.PrepareBindings(stage);
            Assert.False(Memoized(first));
            presenter.RenderHost.BindResources(first);
            var firstBinding = Assert.Single(RuntimeImages(first).Cast<object>());
            var id = (ResourceSlotIdentifier)Field(firstBinding, "ImageIdentifier");
            var image = presenter.Harness.Image(id);
            Assert.NotEqual(0UL, ((Silk.NET.Vulkan.ImageView)Field(firstBinding, "View")).Handle);

            // Registry changes are independent of the ordinary snapshot's identity.
            Feedback(registry, RegisterWords.Texture(address + 0x10000, GuestPixelFormat.Bits8_8_8_8UNorm, 8, 8));
            Feedback(registry); // Sampler feedback must also be drained on a memo hit.
            image.Binding.NeedsRebind = true;
            var second = presenter.RenderHost.PrepareBindings(stage);
            Assert.Equal(DescriptorPreparationOptions.Fast, Memoized(second));
            Assert.Equal(0, Property(registry, "PendingMisses"));
            Assert.Equal(1, Property(registry, "SamplerCount"));
            Assert.Equal(2, RuntimeImages(second).Count);
            Assert.Contains(RuntimeImages(second).Cast<object>(), binding => (ulong)Field(binding, "Address") == address + 0x10000);
            presenter.RenderHost.BindResources(second);
            Assert.All(RuntimeImages(second).Cast<object>(), binding =>
                Assert.NotEqual(0UL, ((Silk.NET.Vulkan.ImageView)Field(binding, "View")).Handle));
            Assert.False(image.Binding.NeedsRebind);
            if (DescriptorPreparationOptions.Fast)
            {
                var ordinary = Program(new ShaderResourceInfo());
                presenter.RenderHost.PrepareBindings(new(ordinary, new ResourceSnapshot()));
                var ordinaryHit = presenter.RenderHost.PrepareBindings(new(ordinary, new ResourceSnapshot()));
                var kept = (Array)presenter.GetField<object>("_keptTextureSets");
                // Stand in for a live draw scope without recording a render pass. Even an
                // identical ordinary memo cannot prove anything about the runtime set.
                presenter.SetField("_renderingActive", true);
                presenter.SetField("_renderingScopesBegun", 123L);
                presenter.SetField("_keptTextureScope", presenter.RenderHost.DrawScope);
                try
                {
                    kept.SetValue(Property(ordinaryHit, "Memo"), 1);
                    Assert.True((bool)presenter.InvokeMethod("KeepsTextureSet", ordinaryHit)!);
                    kept.SetValue(Property(second, "Memo"), 1);
                    Assert.False((bool)presenter.InvokeMethod("KeepsTextureSet", second)!);
                }
                finally { presenter.SetField("_renderingActive", false); }
            }
            presenter.InvokeMethod("DestroyRuntimeDescriptors");
        });
    }

    [Fact]
    public void RuntimeStageStillRejectsChangedOrdinaryImagesAndRefreshesBufferSources()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan, runtimeDescriptors: true)) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan);
        using var heap = presenter.Run(() => new RuntimeDescriptorHeap(fixture.Vulkan.DeviceInfo,
            presenter.Harness.Scheduler, fixture.Vulkan.RuntimeDescriptorLimits));
        presenter.SetField("_runtimeDescriptorHeap", heap);
        var address = presenter.Harness.MapBacked(0x200000, ReadWrite);
        var program = Program(new ShaderResourceInfo
        {
            UsesRuntimeDescriptors = true, Buffers = [new BufferResource()],
            Images = [new ImageResource
            {
                Dimension = ImageDimension.Dim2D, NumericClass = ImageNumericClass.Float,
                ResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass.Sampled, Read = true,
            }],
        });
        var images = new[] { RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 8, 8) };
        uint[][] Buffers(ulong at) => [[(uint)at, (uint)(at >> 32), 32, 0]];
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            var snapshot = new ResourceSnapshot { Images = images, Buffers = Buffers(address + 0x10000) };
            Assert.False(Memoized(presenter.RenderHost.PrepareBindings(new(program, snapshot))));
            var hit = presenter.RenderHost.PrepareBindings(new(program, snapshot));
            Assert.Equal(DescriptorPreparationOptions.Fast, Memoized(hit));
            var changedBuffer = presenter.RenderHost.PrepareBindings(new(program,
                new ResourceSnapshot { Images = images, Buffers = Buffers(address + 0x20000) }));
            Assert.Equal(DescriptorPreparationOptions.Fast, Memoized(changedBuffer));
            var sources = ((BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)[])Property(changedBuffer, "BufferSources");
            Assert.Equal(address + 0x20000, Assert.Single(sources).Descriptor.Address);

            // Existing cache registration serial checks also apply with identical descriptor words.
            presenter.Harness.Cache.FindBuffer(address + 0x1F000, 0x3000);
            var refreshed = presenter.RenderHost.PrepareBindings(new(program,
                new ResourceSnapshot { Images = images, Buffers = Buffers(address + 0x20000) }));
            sources = ((BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)[])Property(refreshed, "BufferSources");
            Assert.Equal(presenter.Harness.Cache.FindBuffer(address + 0x20000, 32), Assert.Single(sources).Buffer);

            var changedImage = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot
            {
                Images = [RegisterWords.Texture(address + 0x30000, GuestPixelFormat.Bits8_8_8_8UNorm, 8, 8)],
                Buffers = snapshot.Buffers,
            }));
            Assert.False(Memoized(changedImage));
            var imageBindings = (Array)Field(Property(changedImage, "Descriptors"), "Images");
            Assert.Equal(address + 0x30000, (ulong)Field(imageBindings.GetValue(0)!, "Address"));
            var fake = new ResourceSlotIdentifier(1000000, 1);
            presenter.Harness.Images.AddPageOwner(address, fake);
            Assert.True(presenter.Harness.Images.RemovePageOwner(address, fake));
            Assert.False(Memoized(presenter.RenderHost.PrepareBindings(new(program, snapshot))));
            presenter.InvokeMethod("DestroyRuntimeDescriptors");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerMemoHitStillValidatesEpochAndRejectsPendingFeedbackOrChangedTable(bool pending)
    {
        if (!RuntimeDescriptorWorkerOptions.Enabled || !GatePrerequisites.Ready(fixture.Vulkan, runtimeDescriptors: true)) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan);
        using var heap = presenter.Run(() => new RuntimeDescriptorHeap(fixture.Vulkan.DeviceInfo,
            presenter.Harness.Scheduler, fixture.Vulkan.RuntimeDescriptorLimits));
        presenter.SetField("_runtimeDescriptorHeap", heap);
        var program = Program();
        presenter.Run(() =>
        {
            // Publish a real empty epoch and use a separate host with its own memo/scratch.
            using (presenter.RenderHost.BeginPreparation())
            {
                var prepared = presenter.RenderHost.PrepareBindings(new(program, new ResourceSnapshot()));
                presenter.InvokeMethod("CommitRuntimeDescriptors", prepared);
            }
            var worker = Activator.CreateInstance(PresenterUnderTest.RecordingHostType, [presenter.Instance, false])!;
            Set(worker, "_worker", true);
            var segmentType = typeof(VulkanVideoPresenter).GetNestedType("SliceSegment", BindingFlags.NonPublic)!;
            var segment = Activator.CreateInstance(segmentType, nonPublic: true)!;
            Set(worker, "_segment", segment);
            var scheduler = presenter.Harness.Scheduler;
            var recording = scheduler.CreateRecordingBuffer(new RecordingRenderingState());
            scheduler.BeginRecording(recording);
            Set(worker, "_workerBuffer", recording);
            var host = (IRenderHost)worker;
            using (host.BeginPreparation())
            {
                var stage = new ShaderStageResources(program, new ResourceSnapshot());
                Assert.False(Memoized(host.PrepareBindings(stage)));
                Assert.Equal(DescriptorPreparationOptions.Fast, Memoized(host.PrepareBindings(stage)));
                var registry = Registry(presenter, program);
                if (pending) Feedback(registry);
                else registry.GetType().GetProperty("TableDirty")!.SetValue(registry, true);
                Assert.Throws<SerialRequiredException>(() => host.PrepareBindings(stage));
                Assert.False((bool)presenter.InvokeMethod("ValidateRuntimeUses", Field(segment, "Requests"))!);
            }
            scheduler.EndRecording(recording);
            scheduler.DiscardRecording(recording);
            foreach (var use in (IList)Field(Field(segment, "Requests"), "RuntimeUses")) Call(use!, "Discard");
            presenter.InvokeMethod("RecordRuntimeMissCopies");
            scheduler.Finish();
            presenter.InvokeMethod("DestroyRuntimeDescriptors");
        });
    }
}
