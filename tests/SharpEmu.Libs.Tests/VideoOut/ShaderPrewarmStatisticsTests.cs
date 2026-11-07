// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ShaderPrewarmStatisticsTests
{
    [Fact]
    public void EachReasonLogsOnceEvenWhenDifferentStagesFailConcurrently()
    {
        var messages = new ConcurrentBag<string>();
        var statistics = new ShaderPrewarmStatistics(messages.Add);
        Parallel.For(0, 100, index => statistics.StageFailure(false, (ulong)index, "ps", (ulong)index, 0,
            $"The shader resource plan is invalid: hash={index} error=tracking failed"));
        Assert.Single(messages);
        Assert.Contains("unique_stage_failures=[compute=0,graphics=100]", statistics.Summary());
        Assert.Contains("\"graphics:tracking failed\":100", statistics.Summary());
    }

    [Fact]
    public void ResourceTrackingReasonsIgnoreShaderLocationsAndTemporaryValueIds()
    {
        var messages = new List<string>();
        var statistics = new ShaderPrewarmStatistics(messages.Add);
        statistics.StageFailure(false, 1, "ps", 1, 0x100,
            "The shader resource plan is invalid: hash=0x01 error=shader resource tracking: hash=0x01 stage=Pixel pc=0x7B0 ImageHandle is undefined from VReadlaneB32@0x55C (value: ScalarBufferWord#21449).");
        statistics.StageFailure(false, 2, "ps", 2, 0x200,
            "The shader resource plan is invalid: hash=0x02 error=shader resource tracking: hash=0x02 stage=Pixel pc=0x910 ImageHandle is undefined from VReadlaneB32@0x5DC (value: ScalarBufferWord#29296).");
        Assert.Single(messages);
        Assert.Contains("unique_stage_failures=[compute=0,graphics=2]", statistics.Summary());
        Assert.Contains("ScalarBufferWord#21449", messages[0]);
    }

    [Fact]
    public void FanoutIsCountedAsPipelinesAndAttributedToUniqueStages()
    {
        var statistics = new ShaderPrewarmStatistics();
        statistics.StageFailure(false, 1, "ps", 0xAA, 0x100, "The shader resource plan is invalid: hash=AA error=tracking failed");
        statistics.StageFailure(false, 2, "ps", 0xBB, 0x200, "The shader resource plan is invalid: hash=BB error=tracking failed");
        Parallel.For(0, 400, _ => statistics.DependentFailure(false, 1));
        statistics.DependentFailure(false, 1, 2);
        statistics.StageFailure(true, 3, "cs", 0xCC, 0x300, "decode failed");
        statistics.DependentFailure(true, 3);
        statistics.MissingPipelines(true, 2);
        statistics.MissingPipelines(false, 3);
        statistics.MissingRecord(true, "code", "absent");
        statistics.MissingRecord(true, "code", "absent");
        statistics.MissingRecord(false, "stage", "absent");
        statistics.MissingRecord(false, "code", "absent");
        statistics.Compiled(true);
        statistics.Compiled(false);
        statistics.PipelineFailure(true, Result.ErrorOutOfDeviceMemory, "oom");
        statistics.PipelineFailure(false, Result.ErrorUnknown, "unknown");
        statistics.PipelineFailure(false, null, "layout failed");
        var summary = statistics.Summary();
        Assert.Contains("compiled=[compute=1,graphics=1]", summary);
        Assert.Contains("unique_stage_failures=[compute=1,graphics=2]", summary);
        Assert.Contains("dependent_pipelines=[compute=1,graphics=401]", summary);
        Assert.Contains("graphics:ps:0x00000000000000AA:0x0000000000000001\":401", summary);
        Assert.Contains("graphics:ps:0x00000000000000BB:0x0000000000000002\":1", summary);
        Assert.Contains("\"graphics:tracking failed\":2", summary);
        Assert.Contains("missing_pipelines=[compute=2,graphics=3]", summary);
        Assert.Contains("\"compute:code\":1", summary);
        Assert.Contains("\"graphics:stage\":1", summary);
        Assert.Contains("\"graphics:code\":1", summary);
        Assert.Contains("\"compute:ErrorOutOfDeviceMemory\":1", summary);
        Assert.Contains("\"graphics:ErrorUnknown\":1", summary);
        Assert.Contains("\"graphics:layout failed\":1", summary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HeapSelectionIncludesBothStages(bool bindless, bool runtime)
    {
        var vertex = new BindingLayout { UsesBindlessHeap = bindless };
        var pixel = new BindingLayout { Descriptors = runtime ? [new(DescriptorBindingKind.RuntimeDescriptorTable, [])] : [] };
        Assert.Equal(new PipelineLayoutHeaps(bindless, runtime), PipelineLayoutHeaps.For(vertex, pixel));
        Assert.Equal(new PipelineLayoutHeaps(bindless, runtime), PipelineLayoutHeaps.For(pixel, vertex));
        Assert.Equal(default, PipelineLayoutHeaps.For(null));
    }
}
