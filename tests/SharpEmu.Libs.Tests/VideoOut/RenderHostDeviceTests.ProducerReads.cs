// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.VideoOut;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    // Exercise the real producer queue and GPU readback, including an overlapping command
    // store after the GPU writer. A synchronization-only policy test would miss ordering bugs.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProducerReadQueuedBehindGpuWriterSeesLatestOrderedBytes(bool laterStore)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var previous = GuestGpuMemoryHook.Current;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var host = (ICommandStreamHost)presenter.Instance;
            var translation = AgcExports.CreateCommandStreamTranslation(host.Memory, host);
            presenter.SetField("_translation", translation);
            var commandType = typeof(VulkanVideoPresenter).GetNestedType("CommandThread", BindingFlags.NonPublic)!;
            const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var command = Activator.CreateInstance(commandType, members, null,
                [presenter.Instance, presenter.GetField<CommandStreamQueue>("_commandStream"), harness.Memory, null, null], null)!;
            presenter.SetField("_commandThread", command);
            var pending = translation.PendingWrites!;
            uint actual = 0;
            Exception? failure = null;
            var producer = new Thread(() =>
            {
                try
                {
                    pending.Mark(address, 4, 1);
                    commandType.GetMethod("Post")!.Invoke(command, [(Action)(() =>
                    {
                        var (buffer, offset) = harness.Cache.ObtainBuffer(address, 4, true);
                        buffer.Fill(offset, 4, 0x12345678);
                        pending.Release(1);
                    }), "test"]);
                    if (laterStore)
                        Assert.True(host.Memory.TryWrite(address, BitConverter.GetBytes(0xAABBCCDDu)));
                    Assert.True(((IShaderPipelineHost)presenter.Instance).TryReadGuestWord(address, out actual));
                }
                catch (Exception exception) { failure = exception; }
            }) { IsBackground = true };
            commandType.GetField("_thread", members)!.SetValue(command, producer);
            producer.Start();
            var executed = commandType.GetProperty("Executed")!;
            var enqueued = commandType.GetField("_enqueued", members)!;
            var waiting = commandType.GetField("_waitingForSequence", members)!;
            var waitingSync = commandType.GetField("_waitingForSync", members)!;
            Assert.True(SpinWait.SpinUntil(() =>
                (bool)waitingSync.GetValue(command)! || (long)waiting.GetValue(command)! != long.MaxValue || !producer.IsAlive,
                TimeSpan.FromSeconds(10)), "Producer did not reach its read wait.");
            var coalesced = (bool)PresenterUnderTest.PresenterType.GetField("CoalesceProducerReads", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.Equal((laterStore ? 2L : 1L) + (coalesced ? 1 : 0), (long)enqueued.GetValue(command)!);
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
            while ((producer.IsAlive || (long)executed.GetValue(command)! < (long)enqueued.GetValue(command)!) && Stopwatch.GetTimestamp() < deadline)
            {
                presenter.Run(() => commandType.GetMethod("RunBatch")!.Invoke(command, [long.MaxValue, 256, (Action)(() => { })]));
                Thread.Yield();
            }
            Assert.True(producer.Join(TimeSpan.FromSeconds(1)), "Producer read did not complete.");
            Assert.Null(failure);
            Assert.Equal(laterStore ? 0xAABBCCDDu : 0x12345678u, actual);
            Assert.False(pending.TryGetPending(address, 4, out _));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(previous);
        }
        harness.Shutdown();
    }
}
