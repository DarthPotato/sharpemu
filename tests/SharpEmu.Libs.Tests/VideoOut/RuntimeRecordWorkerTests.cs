// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Native-free component tests of the actual snapshot, worker binding and join predicate.
// Empty registries are useful stable epochs too (the shader discovers its first images by feedback).
[Collection(SchedulingStateCollection.Name)]
public sealed class RuntimeRecordWorkerTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Nested(string name) => typeof(VulkanVideoPresenter).GetNestedType(name, BindingFlags.NonPublic)!;
    private static object New(Type type) => Activator.CreateInstance(type, nonPublic: true)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static object Get(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);

    private sealed class Snapshot
    {
        public readonly object Presenter = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.PresenterType);
        public readonly object Host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
        public readonly object Prepared = New(PresenterUnderTest.RecordingHostType.GetNestedType("PreparedStageBindings", BindingFlags.NonPublic)!);
        public readonly object Registry = New(typeof(RuntimeDescriptorRegistry<>).MakeGenericType(Nested("TextureResource")));
        public readonly object Requests = New(Nested("SliceRequests"));
        public readonly RuntimeDescriptorHeap Heap = (RuntimeDescriptorHeap)RuntimeHelpers.GetUninitializedObject(typeof(RuntimeDescriptorHeap));
        public readonly GpuBuffer Table = Buffer(101, 256);
        public readonly GpuBuffer Misses = Buffer(202, 4096);
        public readonly object Epoch;

        public Snapshot()
        {
            Registry.GetType().GetProperty("TableDirty")!.SetValue(Registry, false);
            Set(Host, "_presenter", Presenter);
            var segment = New(Nested("SliceSegment"));
            Set(segment, "Requests", Requests);
            Set(Host, "_segment", segment);
            Set(Prepared, "_program", new ShaderProgramInfo { Stage = ShaderStageKind.Pixel, Hash = 123 });
            Epoch = Activator.CreateInstance(Nested("RuntimeEpoch"), Registry, 0L, Table,
                Array.CreateInstance(Nested("RuntimeEpochImage"), 0), Heap, (ShaderStageKind.Pixel, 123UL))!;
            var epochs = (IDictionary)PresenterUnderTest.PresenterType.GetProperty("RuntimeEpochs", Members)!.GetValue(Presenter)!;
            epochs.Add((ShaderStageKind.Pixel, 123UL), Epoch);
            var use = Activator.CreateInstance(Nested("RuntimeWorkerUse"), Epoch, Misses, Buffer(303, 4096))!;
            var uses = (IList)New(typeof(List<>).MakeGenericType(Nested("RuntimeWorkerUse")));
            Set(Requests, "RuntimeUses", uses);
            uses.Add(use);
        }

        public bool Valid() => (bool)Call(Presenter, "ValidateRuntimeUses", Requests)!;
        public void PendingMiss()
        {
            typeof(RuntimeRecordWorkerTests).GetMethod(nameof(AddMiss), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(Nested("TextureResource")).Invoke(null, [Registry]);
        }
    }

    private static GpuBuffer Buffer(ulong handle, ulong size)
    {
        var buffer = (GpuBuffer)RuntimeHelpers.GetUninitializedObject(typeof(GpuBuffer));
        Set(buffer, "_handle", new Silk.NET.Vulkan.Buffer(handle));
        Set(buffer, "<Size>k__BackingField", size);
        return buffer;
    }

    private static void AddMiss<T>(object registry) where T : class
    {
        var words = new uint[RuntimeDescriptorTable.MissBufferDwords];
        words[RuntimeDescriptorTable.SamplerMissCountDword] = 1;
        ((RuntimeDescriptorRegistry<T>)registry).ReadMisses(words);
    }

    [Fact]
    public void StableWorkerBindsPublishedTableAndIsolatedFeedback()
    {
        var state = new Snapshot();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Call(state.Host, "PrepareRuntimeWorker", state.Prepared);
                Call(state.Host, "BindRuntimeWorker", state.Prepared);
                Call(state.Host, "CommitRuntimeWorker", state.Prepared);
            }
            catch (Exception e) { failure = e; }
        });
        worker.Start();
        worker.Join();
        Assert.Null(failure);
        var descriptors = state.Prepared.GetType().GetProperty("Descriptors", Members)!.GetValue(state.Prepared)!;
        var viewType = PresenterUnderTest.RecordingHostType.GetNestedType("BufferView", BindingFlags.NonPublic)!;
        Assert.Equal(Activator.CreateInstance(viewType, state.Table.Handle, 0UL, state.Table.Size), Get(descriptors, "RuntimeTable"));
        Assert.Equal(Activator.CreateInstance(viewType, state.Misses.Handle, 0UL, state.Misses.Size), Get(descriptors, "RuntimeMisses"));
        Assert.True(state.Valid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingMissOrRegistryMutationRefusesWorkerAndRejectsJoin(bool pending)
    {
        var state = new Snapshot();
        Assert.True(state.Valid());
        if (pending) state.PendingMiss();
        else state.Registry.GetType().GetProperty("TableDirty")!.SetValue(state.Registry, true);
        var error = Assert.Throws<TargetInvocationException>(() => Call(state.Host, "PrepareRuntimeWorker", state.Prepared));
        Assert.IsType<SerialRequiredException>(error.InnerException);
        Assert.False(state.Valid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeapRetirementOrTableReplacementRejectsJoin(bool heap)
    {
        var state = new Snapshot();
        Assert.True(state.Valid());
        if (heap)
        {
            var token = new RuntimeDescriptorLease();
            var image = Activator.CreateInstance(Nested("RuntimeEpochImage"), New(Nested("TextureResource")),
                new Silk.NET.Vulkan.ImageView(77), 4u, Array.Empty<uint>(), token, token.Generation)!;
            var images = Array.CreateInstance(Nested("RuntimeEpochImage"), 1);
            images.SetValue(image, 0);
            Set(state.Epoch, "Images", images);
            Assert.True(state.Valid());
            new RuntimeDescriptorLease().Invalidate(); // Another slot is unrelated.
            Assert.True(state.Valid());
            token.Invalidate();
        }
        else ((IDictionary)PresenterUnderTest.PresenterType.GetProperty("RuntimeEpochs", Members)!.GetValue(state.Presenter)!).Clear();
        Assert.False(state.Valid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeWorkerKeepsInlineTableAndAdoptsOnlyItsOwnFeedbackAfterJoin(bool rebuildBeforeJoin)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_RUNTIME_RECORD_WORKERS") == "0" ||
            !GatePrerequisites.Ready(fixture.Vulkan, runtimeDescriptors: true)) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan);
        using var heap = presenter.Run(() => new RuntimeDescriptorHeap(fixture.Vulkan.DeviceInfo, presenter.Harness.Scheduler,
            fixture.Vulkan.RuntimeDescriptorLimits));
        presenter.SetField("_runtimeDescriptorHeap", heap);
        presenter.Run(() =>
        {
            var prepared = New(PresenterUnderTest.RecordingHostType.GetNestedType("PreparedStageBindings", BindingFlags.NonPublic)!);
            var program = new ShaderProgramInfo { Stage = ShaderStageKind.Pixel, Hash = 456 };
            Set(prepared, "_program", program);
            Call(presenter.Recording, "CommitRuntimeDescriptors", prepared);
            var descriptors = prepared.GetType().GetProperty("Descriptors", Members)!.GetValue(prepared)!;
            var inlineTable = Get(descriptors, "RuntimeTable");
            var inlineMisses = Get(descriptors, "RuntimeMisses");
            var registrySet = Call(presenter.Recording, "RuntimeDescriptorsFor", program)!;
            var registry = Get(registrySet, "Registry");

            var scheduler = presenter.Harness.Scheduler;
            var recording = scheduler.CreateRecordingBuffer(new RecordingRenderingState());
            scheduler.BeginRecording(recording);
            var worker = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
            Set(worker, "_presenter", presenter.Instance);
            Set(worker, "_worker", true);
            Set(worker, "_scheduler", scheduler);
            Set(worker, "_deviceInfo", fixture.Vulkan.DeviceInfo);
            Set(worker, "_workerBuffer", recording);
            var segment = New(Nested("SliceSegment"));
            Set(worker, "_segment", segment);
            Call(worker, "PrepareRuntimeWorker", prepared);
            Call(worker, "BindRuntimeWorker", prepared);
            Call(worker, "CommitRuntimeWorker", prepared);
            Assert.Equal(inlineTable, Get(descriptors, "RuntimeTable"));
            Assert.NotEqual(inlineMisses, Get(descriptors, "RuntimeMisses"));
            var requests = Get(segment, "Requests");
            var use = Assert.Single(((IList)Get(requests, "RuntimeUses")).Cast<object>());
            var misses = (GpuBuffer)Get(use, "Misses");
            // Stand in for one shader miss after the worker's zero-fill. The record's words are zero.
            misses.Fill(recording, RuntimeDescriptorTable.SamplerMissCountDword * sizeof(uint), sizeof(uint), 1);
            scheduler.EndRecording(recording);
            Assert.Equal(0, registry.GetType().GetProperty("PendingMisses")!.GetValue(registry));
            Assert.True((bool)Call(presenter.Instance, "ValidateRuntimeUses", requests)!);
            if (rebuildBeforeJoin)
            {
                var oldTable = (GpuBuffer)Get(Get(use, "Epoch"), "Table");
                registry.GetType().GetProperty("TableDirty")!.SetValue(registry, true);
                Call(presenter.Recording, "CommitRuntimeDescriptors", prepared);
                Assert.False((bool)Call(presenter.Instance, "ValidateRuntimeUses", requests)!);
                // The old table's ordinary retirement tick can complete while this segment
                // is still recorded but unsubmitted. Its epoch pin must keep the buffer alive.
                scheduler.Finish();
                Assert.NotEqual(0UL, oldTable.Handle.Handle);
                scheduler.DiscardRecording(recording);
                Call(use, "Discard");
                Assert.Equal(0UL, oldTable.Handle.Handle);
                Call(presenter.Recording, "DestroyRuntimeDescriptors");
                return;
            }
            scheduler.FlushDeferred([recording]);
            Call(use, "Adopt", scheduler);
            var pool = PresenterUnderTest.PresenterType.GetProperty("FeedbackPool", Members)!.GetValue(presenter.Instance)!;
            var concurrent = Call(pool, "Rent")!;
            Assert.NotSame(misses, Get(concurrent, "Item1")); // The adopted pair is still in flight.
            Assert.Equal(0, registry.GetType().GetProperty("PendingMisses")!.GetValue(registry));
            Call(presenter.Recording, "RecordRuntimeMissCopies");
            scheduler.Finish();
            Assert.Equal(1, registry.GetType().GetProperty("PendingMisses")!.GetValue(registry));
            var recycled = Call(pool, "Rent")!;
            Assert.Same(misses, Get(recycled, "Item1"));
            Call(pool, "Return", Get(recycled, "Item1"), Get(recycled, "Item2"));
            Call(pool, "Return", Get(concurrent, "Item1"), Get(concurrent, "Item2"));
            Call(presenter.Recording, "DestroyRuntimeDescriptors");
        });
        presenter.Harness.Vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void DisabledRecordingLeavesEpochsFeedbackPoolAndSegmentUsesUnallocated()
    {
        if (RuntimeDescriptorWorkerOptions.Enabled || !GatePrerequisites.Ready(fixture.Vulkan, runtimeDescriptors: true)) return;
        var requests = New(Nested("SliceRequests"));
        Assert.Null(requests.GetType().GetField("RuntimeUses", Members)!.GetValue(requests));
        using var presenter = new PresenterUnderTest(fixture.Vulkan);
        using var heap = presenter.Run(() => new RuntimeDescriptorHeap(fixture.Vulkan.DeviceInfo, presenter.Harness.Scheduler,
            fixture.Vulkan.RuntimeDescriptorLimits));
        presenter.SetField("_runtimeDescriptorHeap", heap);
        presenter.Run(() =>
        {
            var prepared = New(PresenterUnderTest.RecordingHostType.GetNestedType("PreparedStageBindings", BindingFlags.NonPublic)!);
            var program = new ShaderProgramInfo { Stage = ShaderStageKind.Pixel, Hash = 987 };
            Set(prepared, "_program", program);
            Call(presenter.Recording, "CommitRuntimeDescriptors", prepared);
            Assert.Null(PresenterUnderTest.PresenterType.GetField("_runtimeEpochs", Members)!.GetValue(presenter.Instance));
            Assert.Null(PresenterUnderTest.PresenterType.GetField("_runtimeFeedbackPool", Members)!.GetValue(presenter.Instance));
            var registry = Get(Call(presenter.Recording, "RuntimeDescriptorsFor", program)!, "Registry");
            registry.GetType().GetProperty("TableDirty")!.SetValue(registry, true);
            Assert.Equal(0L, registry.GetType().GetProperty("Version")!.GetValue(registry));
            presenter.Harness.Scheduler.Finish();
            Call(presenter.Recording, "DestroyRuntimeDescriptors");
        });
    }

    [Fact]
    public void RetirementCannotReclaimOutstandingSegmentPin()
    {
        var lease = new RuntimeDescriptorLease();
        Assert.True(lease.TryPin());
        var reused = false;
        lease.Retire(() => reused = true);
        Assert.False(reused);
        Assert.False(lease.TryPin());
        lease.Unpin();
        Assert.True(reused);
    }
}
