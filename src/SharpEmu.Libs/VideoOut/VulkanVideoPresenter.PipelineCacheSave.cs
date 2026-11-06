// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
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

        private sealed record PipelineCacheSaveRequest(PipelineCache Cache, string Path,
            nuint SavedBytes, DriverCacheShard? Shard);
        private sealed record PipelineCacheSaveResult(PipelineCacheSaveRequest Request, bool Success, nuint Bytes);

        private void NotePipelineCompile(long milliseconds)
        {
            if (_pipelineCachePath is not null)
                _expensiveCompileSave?.RecordCompile(milliseconds, Environment.TickCount64);
        }

        // Called by the window loop even when no more pipelines are requested. Mutable dictionaries and
        // dirty flags stay on their owning thread; the worker receives only stable native handles/paths.
        private void PollExpensiveCompileSave()
        {
            FinishExpensiveCompileSave(wait: false);
            if (_expensiveCompileSaveTask is not null || _pipelineCachePath is null || _deviceLost ||
                _shaderPrewarmRunning || _expensiveCompileSave is null) return;
            if (!_expensiveCompileSave.IsDue(Environment.TickCount64, _lastPipelineCacheSaveTick)) return;

            lock (_pendingComputePipelines)
            {
                if (_pendingComputePipelines.Values.Any(pending => !pending.Compile.IsCompleted)) return;
                if (!_expensiveCompileSave.TryTakeSave(Environment.TickCount64, _lastPipelineCacheSaveTick,
                        out var trigger)) return;

                var requests = new List<PipelineCacheSaveRequest>();
                foreach (var shard in _pipelineCacheShards.Values)
                {
                    if (!shard.Dirty || !shard.Cache.IsValueCreated || shard.Cache.Value.Handle == 0) continue;
                    requests.Add(new(shard.Cache.Value, shard.Path, shard.SavedBytes, shard));
                    shard.Dirty = false;
                }
                if (_pipelineCache.Handle != 0)
                    requests.Add(new(_pipelineCache, _pipelineCachePath, _pipelineCacheSavedBytes, null));
                _pipelineCacheDirty = false;
                _expensiveCompileSaveTrigger = trigger;
                _expensiveCompileSaveTask = Task.Run(() => SaveAfterExpensiveCompile(requests, trigger));
            }
        }

        private PipelineCacheSaveResult[] SaveAfterExpensiveCompile(List<PipelineCacheSaveRequest> requests, long trigger)
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
                    if ((request.Shard is not null || Volatile.Read(ref _shaderPrewarmCompletion) is null) &&
                        QueryPipelineCacheSize(request.Cache) is { } size && size == request.SavedBytes)
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
                    $"[GPU][PIPELINE_CACHE] saved after expensive compile: bytes={writtenBytes} " +
                    $"ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} trigger_ms={trigger}");
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
                    if (result.Success) shard.SavedBytes = result.Bytes;
                    else shard.Dirty = true;
                }
                else
                {
                    if (result.Success) _pipelineCacheSavedBytes = result.Bytes;
                    else _pipelineCacheDirty = true;
                }
            }
            if (results.Any(result => !result.Success))
                _expensiveCompileSave.Retry(_expensiveCompileSaveTrigger, _lastPipelineCacheSaveTick);
        }
    }
}
