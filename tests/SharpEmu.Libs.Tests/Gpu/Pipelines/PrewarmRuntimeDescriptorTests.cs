// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A shader that selects its image through control flow is planned with run-time descriptors when the host can
// serve them. Prewarm must plan it the same way, or it fails on the descriptor a draw would have read at run time
// and the shader is compiled again during play.
[Collection(SchedulingStateCollection.Name)]
public sealed class PrewarmRuntimeDescriptorTests : IDisposable
{
    private const ulong Code = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong Srt = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong Materials = PipelineTestGuest.MemoryBase + 0x2_0000;
    private const ulong Heap = PipelineTestGuest.MemoryBase + 0x8_0000;
    private const uint MaterialBytes = 0x74;
    private const uint KeyOffset = 60;
    private const uint ImageType2D = 9;
    private const uint Format32Float = 22;

    private readonly FatalScope _fatal = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _fatal.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static uint Low(ulong value) => unchecked((uint)value);

    private static uint High(ulong value) => (uint)(value >> 32);

    private static uint[] UserData() =>
        [Low(Srt), High(Srt), 0x880, 0x00FFF000, 0x0A503F44, 0, 0x880, 0x00FFF000, 0x0A503F44, 0];

    private static void WriteScene(PipelineTestGuest guest, uint materialCount)
    {
        guest.WriteWords(Srt + 32,
            Low(Materials), High(Materials) | (MaterialBytes << 16), materialCount, 0x00FFF000,
            Low(Heap), High(Heap), materialCount * 32, 0x00FFF000);
        var records = new uint[materialCount * (MaterialBytes / 4)];
        for (uint material = 0; material < materialCount; material++)
            records[material * (MaterialBytes / 4) + KeyOffset / 4] = material;
        guest.WriteWords(Materials, records);
        for (uint key = 0; key < materialCount; key++)
            guest.WriteWords(Heap + key * 32, 0x2000 + key, Format32Float << 20, 0, 0xFAC | (ImageType2D << 28), 0, 0, 0, 0);
    }

    [Fact]
    public void ARuntimePlannedProgramIsPrewarmedWithTheSamePlan()
    {
        ShaderCompileRequest runtime;
        FakePipelineHost host;
        using (var list = ShaderPrewarmList.Open(_directory)!)
        {
            var guest = new PipelineTestGuest { };
            host = guest.Host;
            guest.Host.RuntimeImageDescriptorsSupported = true;
            guest.Host.ShaderPrewarm = list;
            guest.RegisterProgram(Code, Header, CapturedShaders.MaterialTablePixelShader);
            WriteScene(guest, 200);
            var cursor = 0u;
            guest.Programs.GetOrCompile(guest.Source(Code, ShaderStage.Compute, UserData()),
                PipelineTestGuest.ComputeOptions(), ref cursor, out _);
            runtime = Assert.Single(guest.Compiler.Requests);
            Assert.True(runtime.Resources.Info.UsesRuntimeDescriptors);
        }

        using var reloaded = ShaderPrewarmList.Open(_directory)!;
        var (record, code) = Assert.Single(reloaded.LoadedComputes());
        Assert.NotEqual(RuntimeDescriptorMode.Disabled, record.RuntimeDescriptors);

        var withoutHost = new FakeShaderCompiler();
        host.RuntimeImageDescriptorsSupported = false;
        Assert.False(ShaderProgramCache.TryCompilePrewarm(
            record, code, withoutHost, host, out _, out _, out var error));
        Assert.NotEmpty(error);

        var compiler = new FakeShaderCompiler();
        host.RuntimeImageDescriptorsSupported = true;
        Assert.True(ShaderProgramCache.TryCompilePrewarm(
            record, code, compiler, host, out _, out var layout, out error), error);
        var prewarm = Assert.Single(compiler.Requests);
        Assert.True(prewarm.Resources.Info.UsesRuntimeDescriptors);
        Assert.Equal(runtime.Bindings, prewarm.Bindings);
        Assert.Equal(runtime.Bindings, layout);
        Assert.Equal(runtime.Resources.Info.Images.Count, prewarm.Resources.Info.Images.Count);
        Assert.NotNull(layout!.Find(DescriptorBindingKind.RuntimeDescriptorTable));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyRuntimePlanResolvesOnWorkerOnlyWhenEnabled(bool enabled)
    {
        var guest = new PipelineTestGuest();
        guest.Host.RuntimeImageDescriptorsSupported = true;
        guest.RegisterProgram(Code, Header, CapturedShaders.MaterialTablePixelShader);
        WriteScene(guest, 200);
        var cache = new ShaderProgramCache(guest.Context, guest.Compiler, guest.Host) { RuntimeResolveWorkers = enabled };
        var source = guest.Source(Code, ShaderStage.Compute, UserData());
        var options = PipelineTestGuest.ComputeOptions();
        var cursor = 0u;
        var serial = cache.GetOrCompile(source, options, ref cursor, out var serialStage);
        Assert.True(serialStage.Program!.Resources!.Info.UsesRuntimeDescriptors);
        var serialCursor = cursor;
        var compiled = guest.Compiler.Requests.Count;
        guest.Host.RuntimeDescriptorResolutionRequiresSerial = true;
        Exception? failure = null;
        ShaderProgram worker = default;
        ShaderStageResources workerStage = default;
        var thread = new Thread(() =>
        {
            try
            {
                cursor = 0;
                worker = cache.GetOrCompile(source, options, ref cursor, out workerStage);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        thread.Join();
        if (!enabled)
        {
            Assert.IsType<SharpEmu.Libs.Gpu.GpuCommands.SerialResolveRequiredException>(failure);
            return;
        }
        Assert.Null(failure);
        Assert.Equal(serial, worker);
        Assert.Same(serialStage.Program, workerStage.Program);
        Assert.Equal(serialCursor, cursor);
        Assert.Equal(serialStage.Resources.UserData, workerStage.Resources.UserData);
        Assert.Equal(serialStage.Resources.FlattenedResourceTable, workerStage.Resources.FlattenedResourceTable);
        Assert.Equal(serialStage.Resources.DeviceAddressRanges, workerStage.Resources.DeviceAddressRanges);
        Assert.Equal(serialStage.Resources.Buffers, workerStage.Resources.Buffers);
        Assert.Equal(serialStage.Resources.Images, workerStage.Resources.Images);
        Assert.Equal(serialStage.Resources.Samplers, workerStage.Resources.Samplers);
        Assert.Equal(compiled, guest.Compiler.Requests.Count);
    }

    [Fact]
    public void RuntimeModeTransitionRemainsSerialAndDoesNotPublishOnWorker()
    {
        var guest = new PipelineTestGuest();
        guest.Host.RuntimeImageDescriptorsSupported = true;
        guest.RegisterProgram(Code, Header, CapturedShaders.MaterialTablePixelShader);
        WriteScene(guest, 2);
        var cache = new ShaderProgramCache(guest.Context, guest.Compiler, guest.Host) { RuntimeResolveWorkers = true };
        var source = guest.Source(Code, ShaderStage.Compute, UserData());
        var options = PipelineTestGuest.ComputeOptions();
        var cursor = 0u;
        cache.GetOrCompile(source, options, ref cursor, out var original);
        Assert.False(original.Program!.Resources!.Info.UsesRuntimeDescriptors);
        // A second view dimension makes the bounded table incompatible and requires a runtime plan.
        guest.WriteWords(Heap + 32 + 12, 0xFAC | (10u << 28));
        guest.Host.RuntimeDescriptorResolutionRequiresSerial = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cursor = 0;
            Assert.Throws<SharpEmu.Libs.Gpu.GpuCommands.SerialResolveRequiredException>(() =>
                cache.GetOrCompile(source, options, ref cursor, out _));
            Assert.False(ShaderProgramCache.LastEntry!.Plan.Info.UsesRuntimeDescriptors);
        }
        guest.Host.RuntimeDescriptorResolutionRequiresSerial = false;
        cursor = 0;
        cache.GetOrCompile(source, options, ref cursor, out var transitioned);
        Assert.True(transitioned.Program!.Resources!.Info.UsesRuntimeDescriptors);
    }
}
