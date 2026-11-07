// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class PipelineCompileSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static PipelineHandle Handle(ulong id) => new(id, id, true);

    [Fact]
    public async Task ConcurrentRequestsClaimOnceAndIndependentJobsOverlap()
    {
        using var scheduler = new PipelineCompileScheduler(2);
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        var calls = 0;
        PipelineHandle Compile()
        {
            Interlocked.Increment(ref calls);
            entered.Signal();
            Assert.True(release.Wait(Timeout));
            return Handle(1);
        }
        try
        {
            var requests = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => scheduler.Enqueue("same", Compile, true, 1)!)));
            var other = scheduler.Enqueue("other", Compile, true, 1)!;
            Assert.True(entered.Wait(Timeout));
            Assert.All(requests, r => Assert.Same(requests[0], r));
            Assert.Equal(2, calls);
            release.Set();
            await Task.WhenAll(requests[0].Completion.Task, other.Completion.Task).WaitAsync(Timeout);
            Assert.Equal(PipelineCompileScheduler.State.Ready, requests[0].Status);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(2, 100)]
    [InlineData(64, 2)]
    public async Task QueueAndBytesRejectOnlySpeculationAndDemandPromotes(int capacity, long bytes)
    {
        using var scheduler = new PipelineCompileScheduler(1, capacity, bytes);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new ConcurrentQueue<int>();
        var first = scheduler.Enqueue(0, () =>
        {
            entered.Set(); Assert.True(release.Wait(Timeout)); return Handle(0);
        }, true, 1)!;
        try
        {
            Assert.True(entered.Wait(Timeout));
            var a = scheduler.Enqueue(1, () => { order.Enqueue(1); return Handle(1); }, true, 1)!;
            var b = scheduler.Enqueue(2, () => { order.Enqueue(2); return Handle(2); }, true, 1)!;
            Assert.Null(scheduler.Enqueue(3, () => throw new Exception("Rejected job ran"), true, 1));
            Assert.Same(b, scheduler.Enqueue(2, () => throw new Exception("Duplicate job ran"), false, 1));
            var overflow = scheduler.Enqueue(4, () => Handle(4), false, 1)!;
            Assert.Equal(Handle(4), overflow.Use());
            release.Set();
            await Task.WhenAll(first.Completion.Task, a.Completion.Task, b.Completion.Task).WaitAsync(Timeout);
            Assert.Equal(new[] { 2, 1 }, order.ToArray());
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task FailureIsTerminalAndReachesEveryWaiter()
    {
        using var scheduler = new PipelineCompileScheduler(2);
        var failure = new InvalidOperationException("driver failure");
        var calls = 0;
        var request = scheduler.Enqueue(1, () => { Interlocked.Increment(ref calls); throw failure; }, true, 1)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.Completion.Task.WaitAsync(Timeout));
        var again = scheduler.Enqueue(1, () => Handle(2), false, 1)!;
        Assert.Same(request, again);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => again.Use()));
        Assert.Equal(PipelineCompileScheduler.State.Failed, request.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ShutdownJoinsBeforeRegistryDestructionAndStopsAdmission()
    {
        var scheduler = new PipelineCompileScheduler(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owned = new ConcurrentBag<ulong>();
        var destroyed = new ConcurrentDictionary<ulong, int>();
        PipelineHandle Compile(ulong id)
        {
            if (id == 1) { entered.Set(); Assert.True(release.Wait(Timeout)); }
            owned.Add(id); return Handle(id);
        }
        var first = scheduler.Enqueue(1, () => Compile(1), true, 1)!;
        Assert.True(entered.Wait(Timeout));
        var unused = scheduler.Enqueue(2, () => Compile(2), true, 1)!;
        var stop = Task.Run(() =>
        {
            scheduler.Dispose();
            foreach (var id in owned) destroyed.AddOrUpdate(id, 1, (_, n) => n + 1);
        });
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { scheduler.Enqueue(1, () => Handle(1), true, 1); return false; }
                catch (ObjectDisposedException) { return true; }
            }, Timeout));
            Assert.False(stop.IsCompleted);
            Assert.Empty(destroyed);
        }
        finally { release.Set(); }
        await stop.WaitAsync(Timeout);
        Assert.Equal(Handle(1), first.Use());
        Assert.True(unused.Completion.Task.IsCompletedSuccessfully);
        Assert.Equal(2, destroyed.Count);
        Assert.All(destroyed.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task BackendCanClaimAnotherKeyWhileConsumerWaits()
    {
        using var scheduler = new PipelineCompileScheduler(1);
        var request = scheduler.Enqueue(1, () =>
        {
            // This only claims work; it never waits on another compiler job.
            var claim = Task.Run(() => scheduler.Enqueue(2, () => Handle(2), true, 1));
            Assert.True(claim.Wait(Timeout));
            return Handle(1);
        }, true, 1)!;
        Assert.Equal(Handle(1), await Task.Run(request.Use).WaitAsync(Timeout));
    }

    [Fact]
    public async Task SharedPermitBudgetIncludesInlineJobsAndSaveDrainsBeforeSnapshot()
    {
        var budget = new PipelineCompileBudget(2);
        using var first = budget.Enter();
        var second = budget.Enter();
        var pause = Task.Run(budget.Pause);
        Assert.False(pause.IsCompleted);
        second.Dispose();
        Assert.False(pause.IsCompleted);
        first.Dispose();
        using var snapshot = await pause.WaitAsync(Timeout);
        var next = Task.Run(budget.Enter);
        Assert.False(next.IsCompleted);
        snapshot.Dispose();
        using var admitted = await next.WaitAsync(Timeout);
    }

    [Fact]
    public async Task JoinTimeoutWarnsButDoesNotAuthorizeDestruction()
    {
        using var release = new ManualResetEventSlim();
        using var delayed = new ManualResetEventSlim();
        var thread = new Thread(() => release.Wait()) { IsBackground = true };
        thread.Start();
        var destroyed = false;
        var teardown = Task.Run(() =>
        {
            PipelineCompileScheduler.JoinCompiler(thread, "Test", TimeSpan.FromMilliseconds(1), delayed.Set);
            destroyed = true;
        });
        try
        {
            Assert.True(delayed.Wait(Timeout));
            Assert.False(destroyed);
            Assert.False(teardown.IsCompleted);
        }
        finally { release.Set(); }
        await teardown.WaitAsync(Timeout);
        Assert.True(destroyed);
    }
}
