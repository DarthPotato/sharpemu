// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class UnreadableIndirectBufferTests
{
    // Ghost of Yotei, new game: an INDIRECT_BUFFER whose 48-bit target is outside the tracked guest range
    // made the operand read FailFast the process. The buffer is now skipped with a warning.
    [Theory]
    [InlineData(0UL)]
    [InlineData(0x0000_0200_0000_0000UL)]
    [InlineData(0x0000_FFFF_FFFF_F000UL)]
    public void IndirectBufferToAnUnreadableAddress_IsSkippedAndTheStreamContinues(ulong target)
    {
        var runner = new StreamRunner();
        var call = StreamRunner.Packet(PacketOpcode.IndirectBuffer, StreamRunner.Low(target), StreamRunner.High(target), 4);
        var original = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            runner.Run(call, StreamRunner.Packet(PacketOpcode.NumInstances, 7));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(7u, runner.Interpreter.InstanceCount);
        if (target != 0)
        {
            // Address 0 is ignored by the packet itself; an unmapped target is skipped with a warning.
            Assert.Contains("[GPU][WARN][INDIRECT_BUFFER]", output.ToString());
        }
    }
}
