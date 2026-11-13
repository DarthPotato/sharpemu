// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Pipelines;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private readonly PipelineCacheSaveSchedule _expensiveCompileSave = new(
            PipelineCacheSaveSchedule.ParseThreshold(
                Environment.GetEnvironmentVariable("SHARPEMU_PIPELINE_CACHE_SAVE_AFTER_MS")));
        private Task<PipelineCacheSaveResult[]>? _expensiveCompileSaveTask;
        private long _expensiveCompileSaveTrigger;
        private const long PrewarmCheckpointIntervalMs = 120_000;

        private sealed record PipelineCacheSaveRequest(PipelineCache Cache, string Path,
            nuint SavedBytes, DriverCacheShard? Shard, long Generation, long CompileGeneration, bool CanSkipUnchanged);
        private sealed record PipelineCacheSaveResult(PipelineCacheSaveRequest Request, bool Success, nuint Bytes);

        private void NotePipelineCompile(long milliseconds)
        {
            if (_pipelineCachePath is not null)
                _expensiveCompileSave?.RecordCompile(milliseconds, Environment.TickCount64);
        }

        // Called on the window loop. Close native admission and drain outside all
        // metadata locks before capturing handles; the save worker owns the pause.
        private void PollExpensiveCompileSave()
        {
            FinishExpensiveCompileSave(wait: false);
            if (_expensiveCompileSaveTask is not null || _pipelineCachePath is null || _deviceLost ||
                _expensiveCompileSave is null) return;
            if (_shaderPrewarmRunning)
            {
                CheckpointShaderPrewarm();
                return;
            }
            if (!_expensiveCompileSave.IsDue(Environment.TickCount64, _lastPipelineCacheSaveTick))
            {
                CheckpointPipelineCache();
                return;
            }
            if (!PipelineCompileScheduler.Enabled)
                lock (_pendingComputePipelines)
                    if (_pendingComputePipelines.Values.Any(pending => !pending.Compile.IsCompleted)) return;
            if (!_expensiveCompileSave.TryTakeSave(Environment.TickCount64, _lastPipelineCacheSaveTick, out var trigger)) return;
            var pause = PipelineCompileScheduler.Enabled ? _pipelineCompileBudget.Pause() : null;
            try
            {
                var requests = new List<PipelineCacheSaveRequest>();
                foreach (var shard in GuestCacheShards())
                {
                    lock (shard)
                    {
                        if (!shard.Dirty || !shard.Cache.IsValueCreated || shard.Cache.Value.Handle == 0) continue;
                        requests.Add(new(shard.Cache.Value, shard.Path, shard.SavedBytes, shard, shard.Generation, shard.CompileGeneration,
                            !PipelineCompileScheduler.Enabled || shard.CompileGeneration == shard.SavedCompileGeneration));
                        shard.Dirty = false;
                    }
                }
                lock (_pipelineCacheShards)
                {
                    if (_pipelineCache.Handle != 0)
                        requests.Add(new(_pipelineCache, _pipelineCachePath, _pipelineCacheSavedBytes, null, _pipelineCacheGeneration, 0,
                            !PipelineCompileScheduler.Enabled && Volatile.Read(ref _shaderPrewarmCompletion) is null));
                    _pipelineCacheDirty = false;
                }
                _expensiveCompileSaveTrigger = trigger;
                _expensiveCompileSaveTask = Task.Run(() =>
                {
                    try { return SaveAfterExpensiveCompile(requests, trigger); }
                    finally { pause?.Dispose(); }
                });
            }
            catch { pause?.Dispose(); throw; }
        }

        // A shader build can run for many minutes and nothing else saves the driver cache before it ends, so a build
        // that is cut short (crash, kill, power loss) would start over. The prewarm stamp is still written only when
        // the build is done: the next launch runs the list again and finds these pipelines in the driver cache.
        private void CheckpointShaderPrewarm()
        {
            var now = Environment.TickCount64;
            if (now - _lastPipelineCacheSaveTick < PrewarmCheckpointIntervalMs) return;
            PipelineCacheSaveRequest request;
            lock (_pipelineCacheShards)
            {
                if (_pipelineCache.Handle == 0) return;
                request = new(_pipelineCache, _pipelineCachePath!, _pipelineCacheSavedBytes, null,
                    _pipelineCacheGeneration, 0, CanSkipUnchanged: false);
            }
            _lastPipelineCacheSaveTick = now;
            _expensiveCompileSaveTrigger = 0;
            _expensiveCompileSaveTask = Task.Run(() =>
            {
                // One prewarm pipeline can compile for seconds: wait for the compiles in flight off the window loop.
                using var pause = PipelineCompileScheduler.Enabled ? _pipelineCompileBudget.Pause() : null;
                return SaveAfterExpensiveCompile([request], 0, "during shader prewarm");
            });
        }

        private PipelineCacheSaveResult[] SaveAfterExpensiveCompile(List<PipelineCacheSaveRequest> requests, long trigger,
            string when = "after expensive compile")
        {
            var started = Stopwatch.GetTimestamp();
            var results = new List<PipelineCacheSaveResult>();
            nuint writtenBytes = 0;
            foreach (var request in requests)
            {
                try
                {
                    // Vulkan caches are internally synchronized (no externally-synchronized flag is used).
                    // A new compile may grow the cache between size and data queries: the existing writer
                    // treats VK_INCOMPLETE as a failed snapshot, leaving the previous atomic file intact.
                    if (request.CanSkipUnchanged && QueryPipelineCacheSize(request.Cache) is { } size && size == request.SavedBytes)
                    {
                        results.Add(new(request, true, size));
                        continue;
                    }
                    var success = SaveDriverPipelineCache(request.Cache, request.Path, MaxPipelineCacheBytes, out var bytes);
                    results.Add(new(request, success, bytes));
                    if (success) writtenBytes += bytes;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Vulkan pipeline cache save failed: {exception.Message}");
                    results.Add(new(request, false, 0));
                }
            }
            if (writtenBytes != 0)
                Console.Error.WriteLine(
                    $"[GPU][PIPELINE_CACHE] saved {when}: bytes={writtenBytes} " +
                    $"ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} trigger_ms={trigger} " +
                    $"compute_created={Volatile.Read(ref _computeCreations)} compute_cache_hits={Volatile.Read(ref _computeCacheHits)}");
            return results.ToArray();
        }

        private void FinishExpensiveCompileSave(bool wait)
        {
            var task = _expensiveCompileSaveTask;
            if (task is null || (!wait && !task.IsCompleted)) return;
            // Teardown/checkpoints join before reusing filenames or destroying caches/device.
            var results = task.GetAwaiter().GetResult();
            _expensiveCompileSaveTask = null;
            _lastPipelineCacheSaveTick = Environment.TickCount64;
            foreach (var result in results)
            {
                if (result.Request.Shard is { } shard)
                {
                    lock (shard)
                    {
                        if (result.Success)
                        {
                            shard.SavedBytes = result.Bytes;
                            shard.SavedCompileGeneration = result.Request.CompileGeneration;
                        }
                        else shard.Dirty = true;
                        if (shard.Generation != result.Request.Generation) shard.Dirty = true;
                    }
                }
                else
                {
                    lock (_pipelineCacheShards)
                    {
                        if (result.Success) _pipelineCacheSavedBytes = result.Bytes;
                        else _pipelineCacheDirty = true;
                        if (_pipelineCacheGeneration != result.Request.Generation) _pipelineCacheDirty = true;
                    }
                }
            }
            if (results.Any(result => !result.Success))
                _expensiveCompileSave.Retry(_expensiveCompileSaveTrigger, _lastPipelineCacheSaveTick);
        }
    }
}
