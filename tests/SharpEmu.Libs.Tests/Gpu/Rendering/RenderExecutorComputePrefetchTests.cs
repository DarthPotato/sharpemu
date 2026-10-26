// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// A dispatch takes the compute program the prefetch worker resolved from its banks only when the
// result is available, nothing it read was refused and its assumptions still hold; otherwise the
// render thread resolves it itself.
[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorComputePrefetchTests : IDisposable
{
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new() { ResolvesAhead = true };
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorComputePrefetchTests()
    {
        _executor = new RenderExecutor(_host, _pipelines);
        _pipelines.Ahead = _pipelines.Compute;
    }

    public void Dispose() => _fatal.Dispose();

    private sealed class ConcurrentHost(bool current, bool refused = false) : IConcurrentResolveHost
    {
        public void BeginConcurrentResolve() { }
        public object EndConcurrentResolve() => this;
        public bool IsStillCurrent(object assumptions) => ReferenceEquals(this, assumptions) && current;
        public bool RefusedReads(object assumptions) => refused;
    }

    private int SynchronousResolutions => _pipelines.Calls.Count(call => call.StartsWith("get_compute_program", StringComparison.Ordinal));

    // Resolves the dispatch's job on the worker and runs the dispatch with it as the current job.
    private void DispatchWithPrefetch(IConcurrentResolveHost concurrent, uint x = 4, uint y = 1, uint z = 1,
        uint takeX = 4, uint initiator = 0x1)
    {
        using var prefetch = new ParallelProgramPrefetch(_executor, _host, concurrent);
        _executor.Prefetch = prefetch;
        var banks = Banks();
        var job = prefetch.SubmitCompute(banks, 0, initiator, x, y, z);
        prefetch.WaitIdle();
        prefetch.Current = job;
        _executor.Dispatch(1, banks, takeX, y, z, initiator);
        Assert.Null(prefetch.Current);
    }

    [Fact]
    public void ADispatchTakesTheComputeProgramResolvedAhead()
    {
        DispatchWithPrefetch(new ConcurrentHost(current: true));

        Assert.Contains("resolve_compute_ahead 4 1 1", _pipelines.Calls);
        Assert.Equal(0, SynchronousResolutions);
        Assert.Contains("dispatch 4 1 1", _host.Calls);
    }

    [Fact]
    public void AStaleResolutionIsResolvedAgainByTheRenderThread()
    {
        DispatchWithPrefetch(new ConcurrentHost(current: false));

        Assert.Equal(1, SynchronousResolutions);
        Assert.Contains("dispatch 4 1 1", _host.Calls);
    }

    [Fact]
    public void AResolutionWithARefusedReadIsResolvedAgain()
    {
        DispatchWithPrefetch(new ConcurrentHost(current: true, refused: true));

        Assert.Equal(1, SynchronousResolutions);
        Assert.Contains("dispatch 4 1 1", _host.Calls);
    }

    // A refused read can make the worker's program unavailable where the render thread's is not:
    // an unavailable result never skips the dispatch.
    [Fact]
    public void AnUnavailableResolutionIsResolvedAgain()
    {
        _pipelines.Ahead = new ComputeProgram { Available = false };

        DispatchWithPrefetch(new ConcurrentHost(current: true));

        Assert.Equal(1, SynchronousResolutions);
        Assert.Contains("dispatch 4 1 1", _host.Calls);
    }

    // The provider leaves a dispatch it resolves with a side effect (the masked copy kernel) to
    // the render thread.
    [Fact]
    public void ADispatchTheProviderLeavesToTheRenderThreadIsResolvedThere()
    {
        _pipelines.Ahead = null;

        DispatchWithPrefetch(new ConcurrentHost(current: true));

        Assert.Equal(1, SynchronousResolutions);
        Assert.Contains("dispatch 4 1 1", _host.Calls);
    }

    [Fact]
    public void ADispatchWithOtherDimensionsResolvesItsOwnProgram()
    {
        DispatchWithPrefetch(new ConcurrentHost(current: true), takeX: 8);

        Assert.Equal(1, SynchronousResolutions);
        Assert.Contains("dispatch 8 1 1", _host.Calls);
    }

    // No shader: the worker resolves nothing and the dispatch returns before asking for a program.
    [Fact]
    public void ADispatchWithoutAShaderIsNotResolvedAhead()
    {
        using var prefetch = new ParallelProgramPrefetch(_executor, _host, new ConcurrentHost(current: true));
        _executor.Prefetch = prefetch;
        var banks = Banks();
        banks.Shader.Compute.Address = 0;
        prefetch.Current = prefetch.SubmitCompute(banks, 0, 0x1, 4, 1, 1);
        prefetch.WaitIdle();

        _executor.Dispatch(1, banks, 4, 1, 1, 0x1);

        Assert.DoesNotContain(_pipelines.Calls, call => call.StartsWith("resolve_compute_ahead", StringComparison.Ordinal));
        Assert.Equal(0, SynchronousResolutions);
    }

    // A job the worker has not started when its dispatch runs is cancelled: the dispatch resolves
    // its own program instead of waiting, and the worker skips the job.
    [Fact]
    public void ADispatchWhoseJobHasNotStartedCancelsItAndResolvesItself()
    {
        using var prefetch = new ParallelProgramPrefetch(_executor, _host, new ConcurrentHost(current: true));
        _executor.Prefetch = prefetch;
        var banks = Banks();
        // Gated on an item that has not run: the worker cannot start it.
        prefetch.Current = prefetch.SubmitCompute(banks, 1, 0x1, 4, 1, 1);

        _executor.Dispatch(1, banks, 4, 1, 1, 0x1);
        prefetch.AdvanceGate(1);
        prefetch.WaitIdle();

        Assert.Equal(1, SynchronousResolutions);
        Assert.DoesNotContain(_pipelines.Calls, call => call.StartsWith("resolve_compute_ahead", StringComparison.Ordinal));
        Assert.Contains("dispatch 4 1 1", _host.Calls);
    }

    // A graphics job is not taken as a dispatch's, and the reverse.
    [Fact]
    public void ADrawJobIsNotTakenByADispatch()
    {
        using var prefetch = new ParallelProgramPrefetch(_executor, _host, new ConcurrentHost(current: true));
        _executor.Prefetch = prefetch;
        var banks = Banks();
        prefetch.Current = prefetch.Submit(banks);
        prefetch.WaitIdle();

        _executor.Dispatch(1, banks, 4, 1, 1, 0x1);

        Assert.Equal(1, SynchronousResolutions);
    }
}
