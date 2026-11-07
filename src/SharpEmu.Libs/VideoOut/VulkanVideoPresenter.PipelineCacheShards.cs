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
        private sealed class DriverCacheShard
        {
            public Lazy<PipelineCache> Cache = null!;
            public required string Path;
            public bool Dirty;
            public nuint SavedBytes;
            public long Generation;
            public long CompileGeneration;
            public long SavedCompileGeneration;
        }

        private const double SlowShardLoadMilliseconds = 50;

        private string? _pipelineCacheShardDirectory;
        private readonly Dictionary<string, DriverCacheShard> _pipelineCacheShards = new();
        private Dictionary<Lazy<PipelineCache>, DriverCacheShard>? _pipelineCacheShardSources;

        // Linked programs' pipelines (Bindings.CallTargetCount != 0) keep a cache file of their own, one per caller, when the
        // other pipelines keep none: the driver can spend minutes on one, and its own shader cache does not keep pipelines
        // that large. The program cache keeps the callees of the caller's newest ready program (ShaderCallUnionStore), so a
        // later session links that program again and finds its pipeline here. SHARPEMU_VK_PIPELINE_CACHE=0 keeps them
        // in memory.
        private string? _linkedPipelineCacheDirectory;
        private static readonly bool LegacyShaderGroups =
            Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_LEGACY_SHARDS") == "1";

        private string ComputeCacheKey(ulong guestHash, ulong module) =>
            !LegacyShaderGroups && TryGetShaderModuleCacheIdentity(module, out var identity)
                ? $"c2-{identity}" : $"c-{guestHash:X16}";

        private string GraphicsCacheKey(ulong vertexHash, ulong pixelHash, ulong vertexModule, ulong pixelModule)
        {
            string? pixel = null;
            if (!LegacyShaderGroups && TryGetShaderModuleCacheIdentity(vertexModule, out var vertex) &&
                (pixelModule == 0 || TryGetShaderModuleCacheIdentity(pixelModule, out pixel)))
                return $"g2-{vertex}-{(pixelModule == 0 ? "none" : pixel)}";
            return $"g-{vertexHash:X16}-{pixelHash:X16}";
        }

        // MoltenVK recompiles cached MSL libraries during vkCreatePipelineCache.
        // Loading only requested translated modules prevents a mature cache from
        // compiling every previously visited scene before the first frame.
        // Vulkan still validates the complete shader/layout key inside each blob;
        // content hashes only select a storage bucket, never a pipeline to reuse.
        private PipelineCache GetGuestPipelineCache(string key)
            => ResolveGuestPipelineCache(GetGuestPipelineCacheSource(key));

        // Registry and shard metadata locks are short leaf locks. Lazy creation,
        // driver calls and disk writes happen after releasing them.
        private Lazy<PipelineCache>? GetGuestPipelineCacheSource(string key)
        {
            if (_pipelineCacheShardDirectory is null) return null;
            DriverCacheShard shard;
            lock (_pipelineCacheShards)
            {
                if (!_pipelineCacheShards.TryGetValue(key, out shard!))
                {
                    shard = new DriverCacheShard { Path = Path.Combine(_pipelineCacheShardDirectory, key + ".bin") };
                    shard.Cache = new Lazy<PipelineCache>(() => LoadGuestPipelineCache(key, shard));
                    _pipelineCacheShards.Add(key, shard);
                    (_pipelineCacheShardSources ??= new()).Add(shard.Cache, shard);
                }
            }
            MarkShardDirty(shard);
            return shard.Cache;
        }

        private static void MarkShardDirty(DriverCacheShard shard, bool compile = false)
        {
            lock (shard)
            {
                shard.Generation++;
                if (compile) shard.CompileGeneration++;
                shard.Dirty = true;
            }
        }

        private DriverCacheShard[] GuestCacheShards()
        {
            lock (_pipelineCacheShards) return _pipelineCacheShards.Values.ToArray();
        }

        // A linked compile's own pipeline cache, created from its caller's cache file; null when shards keep every pipeline or
        // the linked cache is off. No other compile uses it, so it is written once its pipeline is compiled.
        private DriverCacheShard? CreateLinkedPipelineCache(ulong callerHash)
        {
            if (_pipelineCacheShardDirectory is not null || _linkedPipelineCacheDirectory is not { } directory)
            {
                return null;
            }

            var key = $"l-{callerHash:X16}";
            var shard = new DriverCacheShard { Path = Path.Combine(directory, key + ".bin"), Dirty = true };
            shard.Cache = new Lazy<PipelineCache>(() => LoadGuestPipelineCache(key, shard));
            return shard;
        }

        // A linked compile's cache is written to its caller's file once its pipeline is registered, since a forced close
        // saves nothing, so the file holds the caller's newest pipeline; the cache is destroyed either way.
        private void ReleaseLinkedPipelineCache(DriverCacheShard? shard, ulong callerHash, bool save)
        {
            if (shard is null || !shard.Cache.IsValueCreated || shard.Cache.Value.Handle == 0)
            {
                return;
            }

            var cache = shard.Cache.Value;
            if (save)
            {
                // Variants have independent native caches but share the caller's filename.
                lock (LinkedCacheFileGates.GetOrAdd(shard.Path, static _ => new object()))
                    SaveDriverPipelineCache(cache, shard.Path, MaxPipelineCacheBytes, out _);
            }

            _vk.DestroyPipelineCache(_device, cache, null);
        }

        private PipelineCache ResolveGuestPipelineCache(Lazy<PipelineCache>? source)
        {
            DriverCacheShard? shard = null;
            if (source is not null)
            {
                lock (_pipelineCacheShards) _pipelineCacheShardSources?.TryGetValue(source, out shard);
            }
            if (shard is not null) MarkShardDirty(shard, compile: true);
            var cache = source?.Value ?? default;
            return cache.Handle != 0 ? cache : _pipelineCache;
        }

        private PipelineCache LoadGuestPipelineCache(string key, DriverCacheShard shard)
        {
            var path = shard.Path;
            byte[] data = [];
            try
            {
                if (File.Exists(path) &&
                    !PipelineCacheSignature.TryUnwrap(DriverCacheSignature(), File.ReadAllBytes(path), out data))
                    Console.Error.WriteLine($"[LOADER][INFO] Vulkan cache shard invalidated: path={path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan cache shard read failed: {exception.Message}");
            }

            var started = Stopwatch.GetTimestamp();
            var result = TryCreatePipelineCache(data, out var cache);
            if (result != Result.Success && data.Length != 0)
                result = TryCreatePipelineCache([], out cache);
            if (result != Result.Success)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan cache shard unavailable: key={key} result={result}");
                return default;
            }

            if (data.Length == 0)
                return cache;

            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (QueryPipelineCacheSize(cache) is { } size)
                lock (shard) shard.SavedBytes = size;
            if (milliseconds >= SlowShardLoadMilliseconds)
                Console.Error.WriteLine($"[LOADER][INFO] Vulkan cache shard loaded: key={key} bytes={data.Length} ms={milliseconds:F1}");
            return cache;
        }

        private void SaveGuestPipelineCaches()
        {
            var savedShards = 0;
            nuint savedBytes = 0;
            foreach (var shard in GuestCacheShards())
            {
                long generation;
                long compileGeneration;
                bool canSkipUnchanged;
                nuint previousBytes;
                lock (shard)
                {
                    if (!shard.Dirty || !shard.Cache.IsValueCreated || shard.Cache.Value.Handle == 0) continue;
                    generation = shard.Generation;
                    compileGeneration = shard.CompileGeneration;
                    canSkipUnchanged = !PipelineCompileScheduler.Enabled || compileGeneration == shard.SavedCompileGeneration;
                    previousBytes = shard.SavedBytes;
                }

                var cache = shard.Cache.Value;
                // A new generation can change contents without changing blob size.
                if (canSkipUnchanged && QueryPipelineCacheSize(cache) is { } size && size == previousBytes)
                {
                    lock (shard) if (shard.Generation == generation) shard.Dirty = false;
                    continue;
                }

                if (SaveDriverPipelineCache(cache, shard.Path, MaxPipelineCacheBytes, out var bytes))
                {
                    lock (shard)
                    {
                        if (shard.Generation == generation) shard.Dirty = false;
                        shard.SavedBytes = bytes;
                        shard.SavedCompileGeneration = compileGeneration;
                    }
                    savedShards++;
                    savedBytes += bytes;
                }
            }

            if (savedShards != 0)
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan cache shards saved: directory={_pipelineCacheShardDirectory} shards={savedShards} bytes={savedBytes}");
        }

        private void DestroyGuestPipelineCaches()
        {
            foreach (var shard in GuestCacheShards())
            {
                if (shard.Cache.IsValueCreated && shard.Cache.Value.Handle != 0)
                    _vk.DestroyPipelineCache(_device, shard.Cache.Value, null);
            }
            lock (_pipelineCacheShards)
            {
                _pipelineCacheShards.Clear();
                _pipelineCacheShardSources?.Clear();
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> LinkedCacheFileGates = new(StringComparer.OrdinalIgnoreCase);
    }
}
