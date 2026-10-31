using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ReuseProbeTests
{
    [Fact]
    public void HitsCountItemsAndExpireAfterThreeCompletedWindows()
    {
        var table = new ReuseWindowTable(8);
        var key = new ReuseKey(1, 2);
        Assert.False(table.Add(key)); Assert.False(table.Add(key));
        Assert.Equal(2, table.Items); Assert.Equal(1, table.Unique); Assert.Equal(1, table.Repeats);
        table.Next();
        Assert.True(table.Add(key)); Assert.True(table.Add(key));
        Assert.Equal(2, table.Previous); Assert.Equal(2, table.History); Assert.Equal(1, table.Repeats);
        table.Next(); table.Next(); table.Next();
        Assert.True(table.Add(key)); Assert.Equal(0, table.Previous);
        table.Next(); table.Next(); table.Next(); table.Next();
        Assert.False(table.Add(key)); Assert.Equal(0, table.History);
    }

    [Fact]
    public void FullTableStillFindsExistingKeysAndCountsOverflow()
    {
        var table = new ReuseWindowTable(2);
        table.Add(new(0, 1)); table.Add(new(0, 2)); table.Add(new(0, 3)); table.Add(new(0, 1));
        Assert.Equal(4, table.Items); Assert.Equal(2, table.Unique);
        Assert.Equal(1, table.Overflow); Assert.Equal(1, table.Repeats);
        table.Next(); Assert.True(table.Add(new(0, 2))); Assert.False(table.Add(new(0, 3)));
    }

    [Fact]
    public void ExactSampleDetectsForcedCollisionAndLengthDifference()
    {
        var sample = new ReuseCollisionSample();
        var key = new ReuseKey(7, 8);
        sample.Check(key, [1, 2]); sample.Check(key, [1, 2]);
        Assert.Equal(0, sample.Collisions);
        sample.Check(key, [1]); Assert.Equal(2, sample.Collisions);
        sample.Check(new(9, 10), [3]); Assert.Equal(2, sample.Collisions);
        sample.Check(key, new ulong[8193]); Assert.Equal(1, sample.Oversize);
    }

    [Fact]
    public void HashDistinguishesOrderAndLength()
    {
        var a = new ReuseHash(); a.Add(1); a.Add(2);
        var b = new ReuseHash(); b.Add(2); b.Add(1);
        var c = new ReuseHash(); c.Add(1); c.Add(2); c.Add(0);
        Assert.NotEqual(a.Key, b.Key); Assert.NotEqual(a.Key, c.Key);
    }

    [Fact]
    public void SwitchIsExplicitAndIndependentOfRenderProfiling()
    {
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_REUSE_PROBE") == "1", ReuseProbe.Enabled);
        if (ReuseProbe.Enabled) return;
        var before = ReuseProbe.Flips;
        if (ReuseProbe.Enabled) ReuseProbe.Flip();
        Assert.Equal(before, ReuseProbe.Flips);
    }

    [Fact]
    public void LatePayloadChangesDoNotChangeRecipeButDescriptorAndPermutationDo()
    {
        var probe = new ReuseProbeState();
        var program = new ShaderProgramInfo { Hash = 123, Stage = ShaderStageKind.Compute, Bindings = new BindingLayout() };
        var stage = new ShaderStageResources(program, new ResourceSnapshot { Buffers = [[1, 2, 3, 4]], UserData = [42] });
        probe.Record(stage, [42]);
        probe.Record(stage with { ObjectId = 9, Resources = new ResourceSnapshot { Buffers = [[1, 2, 3, 4]], UserData = [99] } }, [99]);
        Assert.Equal(1, probe.Table(true, false).Unique);
        Assert.Equal(1, probe.Table(true, false).Repeats);
        Assert.Equal(2, probe.Table(true, true).Unique);
        probe.Record(stage with { Resources = new ResourceSnapshot { Buffers = [[1, 2, 3, 5]] } }, [99]);
        Assert.Equal(2, probe.Table(true, false).Unique);
        var other = new ShaderProgramInfo { Hash = 123, Stage = ShaderStageKind.Compute, Bindings = program.Bindings };
        probe.Record(stage with { Program = other }, [42]);
        Assert.Equal(3, probe.Table(true, false).Unique);
        Assert.Equal(0, probe.Table(false, false).Items);
    }

    [Fact]
    public void RuntimeDescriptorWordsChangeRecipeWithoutGuestReads()
    {
        var probe = new ReuseProbeState();
        var program = new ShaderProgramInfo { Stage = ShaderStageKind.Pixel, Bindings = new BindingLayout() };
        var stage = new ShaderStageResources(program, new ResourceSnapshot());
        var runtime = new RuntimeDescriptorRegistry<object>();
        probe.Record(stage, [], runtime);
        uint[] words = [1, 2, 3, 4];
        runtime.Images.Add(RuntimeDescriptorKey.From(words), new RuntimeImageEntry<object>(words));
        probe.Record(stage, [], runtime);
        Assert.Equal(2, probe.Table(false, false).Unique);
        runtime.SamplerEntries.Add(RuntimeDescriptorKey.From(words), new RuntimeSamplerEntry(words, 1));
        probe.Record(stage, [], runtime);
        Assert.Equal(3, probe.Table(false, false).Unique);
    }
}
