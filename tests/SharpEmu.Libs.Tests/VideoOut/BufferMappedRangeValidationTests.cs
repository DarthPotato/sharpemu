// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class BufferMappedRangeValidationTests
{
    private sealed class Memory : ICpuMemory
    {
        public ulong Prefix = 64;
        public readonly List<(ulong Address, ulong Size)> Queries = [];
        public bool CanRead(ulong address, ulong size)
        {
            Queries.Add((address, size));
            return address == 0x1000 && size != 0 && size <= Prefix;
        }
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    private static IRenderHost Create(Memory memory)
    {
        var presenter = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.PresenterType);
        PresenterUnderTest.PresenterType.GetField("_guestMemory", PresenterUnderTest.InstanceMembers)!.SetValue(presenter, memory);
        return (IRenderHost)presenter;
    }

    [Fact]
    public void FullyMappedRangeUsesOneQueryAndDoesNotCacheAcrossCalls()
    {
        using var fatal = new FatalScope();
        var memory = new Memory();
        var host = Create(memory);
        Assert.Equal(64UL, host.ClampMappedSize(0x1000, 64));
        Assert.Equal(new[] { (0x1000UL, 64UL) }, memory.Queries);
        memory.Prefix = 13;
        Assert.Equal(13UL, host.ClampMappedSize(0x1000, 64));
        memory.Prefix = 0;
        Assert.Throws<SchedulerFatalException>(() => host.ClampMappedSize(0x1000, 64));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(13UL)]
    [InlineData(63UL)]
    public void PartialRangeRetainsExactReadablePrefix(ulong prefix)
    {
        var memory = new Memory { Prefix = prefix };
        Assert.Equal(prefix, Create(memory).ClampMappedSize(0x1000, 64));
    }

    [Theory]
    [InlineData(0UL, 64UL)]
    [InlineData(0x1000UL, 0UL)]
    [InlineData(ulong.MaxValue - 15, 32UL)]
    public void InvalidRangeFailsWithoutQueryingMemory(ulong address, ulong size)
    {
        using var fatal = new FatalScope();
        var memory = new Memory();
        Assert.Throws<SchedulerFatalException>(() => Create(memory).ClampMappedSize(address, size));
        Assert.Empty(memory.Queries);
    }
}
