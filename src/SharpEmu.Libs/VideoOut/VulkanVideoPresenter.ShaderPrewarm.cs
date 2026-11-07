// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const int PrewarmProgressBatch = 256;

        private sealed record ShaderPrewarmCompletion(string StampPath, string CoverageStamp);
        private ShaderPrewarmCompletion? _shaderPrewarmCompletion;
        private byte[] _loadedPrewarmCache = [];

        private ShaderPrewarmList? _shaderPrewarmList;
        private ConcurrentDictionary<string, byte>? _prewarmedShaderIdentities;
        private ConcurrentDictionary<string, byte>? _runtimeComputeIdentities;
        private Thread[] _shaderPrewarmThreads = [];
        private volatile bool _shaderPrewarmStopping;
        private volatile bool _shaderPrewarmFinished;
        private int _shaderPrewarmCompiled;
        private ShaderPrewarmStatistics? _shaderPrewarmStatistics;
        private ShaderPrewarmStatistics PrewarmStatistics => LazyInitializer.EnsureInitialized(
            ref _shaderPrewarmStatistics, static () => new ShaderPrewarmStatistics());
        private int _shaderPrewarmFailed;
        private int _shaderPrewarmRuntimeHits;
        private readonly ConcurrentQueue<ulong> _shaderPrewarmCompleted = new();
        private readonly ConcurrentDictionary<ulong, Lazy<PrewarmedStage?>> _prewarmStages = new();
        private readonly ConcurrentBag<ShaderModule> _prewarmStageModules = [];

        private sealed record PrewarmedStage(ShaderProgram Handle, ShaderProgramInfo Program);

        private readonly record struct PrewarmWork(ComputePrewarmRecord? Compute, ShaderCodeCapture? Code, GraphicsPrewarmItem? Graphics);

        ShaderPrewarmList? IShaderPipelineHost.ShaderPrewarm => Volatile.Read(ref _shaderPrewarmList);

        private void StartShaderPrewarm()
        {
            if (!StartShaderPrewarmCore())
            {
                SetShaderPrewarmState(false);
            }
        }

        private bool StartShaderPrewarmCore()
        {
            if (_shaderPrewarmList is not null ||
                _pipelineCacheShardDirectory is not null ||
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_PREWARM") == "0")
            {
                _loadedPrewarmCache = [];
                return false;
            }

            var directory = Path.GetDirectoryName(VulkanPipelineCacheStorage.ResolvePath(
                VideoOutExports.GetApplicationTitleId(),
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")));
            if (string.IsNullOrEmpty(directory) || ShaderPrewarmList.Open(directory) is not { } list)
            {
                _loadedPrewarmCache = [];
                return false;
            }

            _prewarmedShaderIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            _runtimeComputeIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            Volatile.Write(ref _shaderPrewarmList, list);
            var compiler = GuestGpu.Current;
            var stamp = list.CoverageStamp(ShaderPrewarmStamp(compiler));
            if (list.LoadedComputeCount == 0 && list.LoadedGraphicsCount == 0)
            {
                _loadedPrewarmCache = [];
                _shaderPrewarmFinished = true;
                return false;
            }

            if (CanSkipShaderPrewarm(list, stamp, _loadedPrewarmCache))
            {
                Volatile.Write(ref _shaderPrewarmCompletion, new(list.StampPath, stamp));
                _loadedPrewarmCache = [];
                _shaderPrewarmFinished = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Shader prewarm skipped: compute={list.LoadedComputeCount} graphics={list.LoadedGraphicsCount} " +
                    "reason=cache-and-recording-match");
                return false;
            }

            _loadedPrewarmCache = [];
            // Progress alone does not prove that the saved cache contains those pipelines.
            list.ClearProgress();
            var computes = list.LoadedComputes((kind, key) => PrewarmStatistics.MissingRecord(true, kind, key));
            var graphics = list.LoadedGraphics((kind, key) => PrewarmStatistics.MissingRecord(false, kind, key));
            PrewarmStatistics.MissingPipelines(true, list.LoadedComputeCount - computes.Count);
            PrewarmStatistics.MissingPipelines(false, list.LoadedGraphicsCount - graphics.Count);
            Interlocked.Add(ref _shaderPrewarmFailed,
                list.LoadedComputeCount - computes.Count + list.LoadedGraphicsCount - graphics.Count);
            var started = Stopwatch.GetTimestamp();
            var pendingCount = computes.Count + graphics.Count;
            if (pendingCount == 0)
            {
                FinishShaderPrewarm(list, stamp, started);
                return false;
            }

            var work = new ConcurrentQueue<PrewarmWork>(
                graphics.Select(static item => new PrewarmWork(null, null, item))
                    .Concat(computes.Select(static item => new PrewarmWork(item.Record, item.Code, null))));
            var workerCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            var remaining = workerCount;
            Volatile.Write(ref _shaderPrewarmTotal, pendingCount);
            Volatile.Write(ref _shaderPrewarmProgress, 0);
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm started: compute={computes.Count} graphics={graphics.Count} resumed=0 workers={workerCount}");
            SetShaderPrewarmState(true);
            _shaderPrewarmThreads = new Thread[workerCount];
            for (var index = 0; index < workerCount; index++)
            {
                _shaderPrewarmThreads[index] = new Thread(() =>
                {
                    SubmissionScheduler.ThrowOnFatal = true;
                    while (!_shaderPrewarmStopping && work.TryDequeue(out var item))
                    {
                        if (item.Graphics is { } pipeline)
                        {
                            if (PrewarmGraphicsPipeline(pipeline, compiler))
                            {
                                _shaderPrewarmCompleted.Enqueue(pipeline.Identity);
                            }
                        }
                        else if (PrewarmComputePipeline(item.Compute!, item.Code!, compiler))
                        {
                            _shaderPrewarmCompleted.Enqueue(ShaderPrewarmList.Identity(item.Compute!));
                        }

                        if (Interlocked.Increment(ref _shaderPrewarmProgress) % PrewarmProgressBatch == 0)
                        {
                            FlushShaderPrewarmProgress(list, stamp);
                        }
                    }

                    FlushShaderPrewarmProgress(list, stamp);
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        ReleasePrewarmStages();
                        FinishShaderPrewarm(list, stamp, started);
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"SharpEmu shader prewarm {index}",
                };
                _shaderPrewarmThreads[index].Start();
            }

            return true;
        }

        private void FlushShaderPrewarmProgress(ShaderPrewarmList list, string stamp)
        {
            var batch = new List<ulong>();
            while (_shaderPrewarmCompleted.TryDequeue(out var identity))
            {
                batch.Add(identity);
            }

            list.AppendProgress(stamp, batch);
        }

        private static bool CanSkipShaderPrewarm(ShaderPrewarmList list, string stamp, byte[] cache) =>
            cache.Length != 0 && list.IsStampCurrent(ShaderPrewarmList.CacheStamp(stamp, cache));

        private static string ShaderPrewarmStampPart(Type type) =>
            type.Assembly.ManifestModule.ModuleVersionId.ToString("N");

        private string ShaderPrewarmStamp(IGuestGpuBackend compiler)
        {
            var stamp = BuildShaderPrewarmStamp(compiler.GetType(), DriverCacheSignature());
            var frontends = Agc.AgcExports.ShaderFrontendStamp();
            return frontends.Length == 0 ? stamp : $"{stamp}|frontends-v1|{frontends}";
        }

        private string BuildShaderPrewarmStamp(Type compilerType, string driverSignature) =>
            string.Join(
                '|',
                "success-only-v2",
                ShaderPrewarmHost.CapabilityStamp(this),
                ShaderPrewarmStampPart(typeof(ShaderProgramCache)),
                ShaderPrewarmStampPart(typeof(Gen5ShaderTranslator)),
                ShaderPrewarmStampPart(typeof(Gen5SpirvTranslator)),
                ShaderPrewarmStampPart(compilerType),
                compilerType.FullName,
                ((IShaderPipelineHost)this).SharedInt64AtomicsEnabled,
                ((IShaderPipelineHost)this).ExecGuardElisionEnabled,
                _supportsShaderSignedZeroInfNanPreserveFloat32,
                _supportsExactFloat16Conversion,
                SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                Volatile.Read(ref _nativeSubgroupSize),
                _canRequireComputeSubgroup32,
                _canRequireComputeSubgroup64,
                _maxComputeWorkgroupSubgroups,
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_HALF_MASKS"),
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_LANE_LOCAL"),
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_PAIRED"),
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MAX_STEPS"),
                Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_CF"),
                Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_FORWARD_BLOCKS"),
                Environment.GetEnvironmentVariable("SHARPEMU_EXEC_GUARD_ELISION"),
                Environment.GetEnvironmentVariable("SHARPEMU_EXEC_SELECT_ELIMINATION"),
                Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_DATA_READS"),
                Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_DATA_READS_ONLY"),
                driverSignature.TrimEnd('\n'));

        private bool PrewarmComputePipeline(
            ComputePrewarmRecord record,
            ShaderCodeCapture code,
            IGuestGpuBackend compiler)
        {
            if (!ShaderProgramCache.TryCompilePrewarm(
                    record, code, compiler, this, out var compiled, out var layout, out var error,
                    frontendOf: Agc.AgcExports.ShaderFrontendForDomain))
            {
                var identity = ShaderPrewarmList.Identity(record);
                PrewarmStatistics.StageFailure(true, identity, "cs", record.Hash, record.Address, error);
                PrewarmStatistics.DependentFailure(true, identity);
                Interlocked.Increment(ref _shaderPrewarmFailed);
                return false;
            }

            var payload = compiled!.Payload;
            Result? nativeResult = null;
            var bindings = new List<DescriptorSetLayoutBinding>();
            DescriptorSetLayout setLayout = default;
            PipelineLayout pipelineLayout = default;
            ShaderModule module = default;
            try
            {
                CollectLayoutBindings(bindings, layout!, ShaderStage.Compute);
                setLayout = CreateDescriptorSetLayout(bindings, out _, out _);
                var heaps = PipelineLayoutHeaps.For(layout);
                pipelineLayout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit, heaps.Bindless, heaps.Runtime);
                module = CreateShaderModule(payload);
                var pipeline = CompileComputePipeline(_vk, _device, _pipelineCache, module, pipelineLayout,
                    ComputeRequiredSubgroupSize(record.Info), result => nativeResult = result);
                _vk.DestroyPipeline(_device, pipeline, null);
                _prewarmedShaderIdentities![VulkanPipelineCacheStorage.CompiledShaderIdentity(payload)] = 0;
                Interlocked.Increment(ref _shaderPrewarmCompiled);
                PrewarmStatistics.Compiled(true);
                return true;
            }
            catch (Exception exception)
            {
                PrewarmStatistics.PipelineFailure(true, nativeResult, exception.Message);
                Interlocked.Increment(ref _shaderPrewarmFailed);
                return false;
            }
            finally
            {
                if (module.Handle != 0)
                {
                    _vk.DestroyShaderModule(_device, module, null);
                }

                if (pipelineLayout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, pipelineLayout, null);
                }

                if (setLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
                }
            }
        }

        private bool PrewarmGraphicsPipeline(GraphicsPrewarmItem item, IGuestGpuBackend compiler)
        {
            var vertex = PrewarmStageProgram(item.Vertex, compiler);
            var pixel = item.Pixel is { } pixelSource ? PrewarmStageProgram(pixelSource, compiler) : null;
            if (vertex is null || (item.Pixel is not null && pixel is null))
            {
                var failedStages = new List<ulong>(2);
                if (vertex is null) failedStages.Add(item.Vertex.Identity);
                if (item.Pixel is not null && pixel is null) failedStages.Add(item.Pixel.Identity);
                PrewarmStatistics.DependentFailure(false, failedStages.ToArray());
                Interlocked.Increment(ref _shaderPrewarmFailed);
                return false;
            }

            var record = item.Record;
            var rectangleList = record.StaticParameters.Topology == PrimitiveTopology.PatchList;
            if (rectangleList && !_supportsFillRectangle && !_supportsRectangleGeometry)
            {
                PrewarmStatistics.PipelineFailure(false, null, "Rectangle compatibility variants are not recorded.");
                Interlocked.Increment(ref _shaderPrewarmFailed);
                return false;
            }

            var description = new GraphicsPipelineDescription
            {
                Rendering = record.Rendering,
                VertexInput = record.VertexInput,
                VertexInfo = record.VertexInfo,
                VertexProgram = vertex.Handle,
                VertexStage = vertex.Program,
                PixelInfo = item.Pixel?.Record.Options.PixelInfo,
                PixelProgram = pixel?.Handle ?? default,
                PixelStage = pixel?.Program,
                StaticParameters = record.StaticParameters,
            };
            var bindings = new List<DescriptorSetLayoutBinding>();
            Result? nativeResult = null;
            DescriptorSetLayout setLayout = default;
            PipelineLayout pipelineLayout = default;
            try
            {
                CollectLayoutBindings(bindings, description.VertexStage, ShaderStage.Vertex);
                if (description.PixelStage is { } pixelStage)
                {
                    CollectLayoutBindings(bindings, pixelStage, ShaderStage.Pixel);
                }

                setLayout = CreateDescriptorSetLayout(bindings, out _, out _);
                var heaps = PipelineLayoutHeaps.For(description.VertexStage.Bindings, description.PixelStage?.Bindings);
                pipelineLayout = CreatePipelineLayout(setLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, heaps.Bindless, heaps.Runtime);
                var pipeline = rectangleList
                    ? CreateRenderPipeline(description, PrimitiveTopology.TriangleList, pipelineLayout,
                        _supportsFillRectangle ? PolygonMode.FillRectangleNV : PolygonMode.Fill,
                        expandRectangles: !_supportsFillRectangle, prewarm: true, creationResultObserver: result => nativeResult = result)
                    : CreateRenderPipeline(description, record.StaticParameters.Topology, pipelineLayout, prewarm: true,
                        creationResultObserver: result => nativeResult = result);
                _vk.DestroyPipeline(_device, pipeline, null);
                Interlocked.Increment(ref _shaderPrewarmCompiled);
                PrewarmStatistics.Compiled(false);
                return true;
            }
            catch (Exception exception)
            {
                PrewarmStatistics.PipelineFailure(false, nativeResult, exception.Message);
                Interlocked.Increment(ref _shaderPrewarmFailed);
                return false;
            }
            finally
            {
                if (pipelineLayout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, pipelineLayout, null);
                }

                if (setLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
                }
            }
        }

        private PrewarmedStage? PrewarmStageProgram(PrewarmStage stage, IGuestGpuBackend compiler) =>
            _prewarmStages.GetOrAdd(stage.Identity, _ => new Lazy<PrewarmedStage?>(() => CompilePrewarmStage(stage, compiler))).Value;

        private PrewarmedStage? CompilePrewarmStage(PrewarmStage stage, IGuestGpuBackend compiler)
        {
            var record = stage.Record;
            var code = stage.Code;
            var label = record.Stage == ShaderStage.Vertex ? "vs" : "ps";
            try
            {
                IShaderFrontend? frontend = null;
                if (code.FrontendDomain != 0 && (frontend = Agc.AgcExports.ShaderFrontendForDomain(code.FrontendDomain)) is null)
                {
                    PrewarmStatistics.StageFailure(false, stage.Identity, label, record.Hash, record.Registered.CodeAddress,
                        $"The shader frontend 0x{code.FrontendDomain:X16} is not registered.");
                    return null;
                }

                var registered = record.Registered with { Frontend = frontend, FrontendUserData = code.FrontendUserData };
                var source = new ShaderSource(registered, record.Hash, new uint[record.UserDataCount], record.UserDataBase, record.Stage);
                var programs = new ShaderProgramCache(code.CreateContext(), compiler, new ShaderPrewarmHost(this, shader =>
                {
                    var module = CreateShaderModule(shader.Payload);
                    _prewarmStageModules.Add(module);
                    if (!_supportsFillRectangle && _supportsRectangleGeometry)
                        _rectangleStageCode[module.Handle] = shader.Payload;
                    return module.Handle;
                }));
                var (handle, program) = programs.CompilePrewarmStage(source, record.Options, record.Specialization, record.PushDataCursor, record.RuntimeDescriptors);
                return new PrewarmedStage(handle, program);
            }
            catch (Exception exception)
            {
                PrewarmStatistics.StageFailure(false, stage.Identity, label, record.Hash, record.Registered.CodeAddress, exception.Message);
                return null;
            }
        }

        private void ReleasePrewarmStages()
        {
            _prewarmStages.Clear();
            while (_prewarmStageModules.TryTake(out var module))
            {
                _rectangleStageCode.TryRemove(module.Handle, out _);
                _vk.DestroyShaderModule(_device, module, null);
            }
        }

        private void FinishShaderPrewarm(ShaderPrewarmList list, string stamp, long started)
        {
            var runtime = _runtimeComputeIdentities!;
            var prewarmed = _prewarmedShaderIdentities!;
            var matched = runtime.Keys.Count(prewarmed.ContainsKey);
            if (_shaderPrewarmStopping)
            {
                SetShaderPrewarmState(false);
                return;
            }

            if (Volatile.Read(ref _shaderPrewarmFailed) == 0)
            {
                Volatile.Write(ref _shaderPrewarmCompletion, new(list.StampPath, stamp));
            }
            _shaderPrewarmFinished = true;
            SetShaderPrewarmState(false);
            _pipelineCacheDirty = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm done: compiled={Volatile.Read(ref _shaderPrewarmCompiled)} " +
                $"failed={Volatile.Read(ref _shaderPrewarmFailed)} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} " +
                $"runtime_compute_modules={runtime.Count} matched={matched} {PrewarmStatistics.Summary()}");
        }

        private void NoteRuntimeComputeModule(string identity)
        {
            if (_runtimeComputeIdentities is not { } runtime || _prewarmedShaderIdentities is not { } prewarmed)
            {
                return;
            }

            runtime[identity] = 0;
            if (!_shaderPrewarmFinished || !prewarmed.ContainsKey(identity))
            {
                return;
            }

            var hits = Interlocked.Increment(ref _shaderPrewarmRuntimeHits);
            if (hits is 1 or 10 or 100 or 1000)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Shader prewarm hits: {hits} of runtime_compute_modules={runtime.Count}");
            }
        }

        private void StopShaderPrewarm()
        {
            _shaderPrewarmStopping = true;
            SetShaderPrewarmState(false);
            foreach (var thread in _shaderPrewarmThreads)
            {
                thread.Join(TimeSpan.FromSeconds(10));
            }

            _shaderPrewarmThreads = [];
            if (Interlocked.Exchange(ref _shaderPrewarmList, null) is { } list)
            {
                list.Dispose();
            }
        }
    }
}
