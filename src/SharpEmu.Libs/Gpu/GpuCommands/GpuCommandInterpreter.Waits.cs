// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Packets;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    internal static long RelaxedWaitHits;
    internal static long RelaxedWaitFallbacks;
    private const uint RewindValidBit = 1u << 31;
    private const uint RewindKnownBits = 0x8100_0000u;

    // Polls the label once; an unmet condition suspends the submission at this packet.
    internal void WaitOnMemory(uint compareFunction, ulong address, ulong reference, ulong mask, uint waitOperation, bool is64Bit)
    {
        if (address == 0)
        {
            throw _host.Fatal($"The wait address is zero: compare={compareFunction} reference=0x{reference:X16}.");
        }

        if (!WaitOperation.IsValid(waitOperation))
        {
            throw _host.Fatal($"The wait operation is not supported: operation=0x{waitOperation:X8} address=0x{address:X16}.");
        }

        if (!WaitOperation.ShouldExecute(waitOperation, ConditionalWaitEnabled))
        {
            return;
        }

        // A deferred label is visible here at once; guest CPU threads see it after the dump before it.
        if (!_host.TryReadDeferredLabel(QueueId, address, is64Bit ? 8UL : 4UL, out var deferred, out var value))
        {
            Suspend();
            return;
        }
        var stale = false;
        if (!deferred) value = ReadWaitOperand(address, is64Bit, allowStale: true, out stale);
        if (!WaitOperation.TryCompare(value, reference, mask, compareFunction, out var satisfied))
        {
            throw _host.Fatal($"The wait compare function is unknown: function={compareFunction} address=0x{address:X16}.");
        }

        if (stale)
        {
            if (satisfied)
            {
                Interlocked.Increment(ref RelaxedWaitHits);
            }
            else
            {
                Interlocked.Increment(ref RelaxedWaitFallbacks);
                value = ReadWaitOperand(address, is64Bit, allowStale: false, out _);
                _ = WaitOperation.TryCompare(value, reference, mask, compareFunction, out satisfied);
            }
        }

        if (!satisfied && TryReadOwnWrite(address, is64Bit, out var ownValue))
        {
            _ = WaitOperation.TryCompare(ownValue, reference, mask, compareFunction, out satisfied);
        }

        if (!satisfied)
        {
            Suspend();
        }
        else
        {
            _host.CpuGpuVisibilityBoundary();
        }
    }

    // The wait bit is PredicationZPassWaitOp: 0 (kWaitForQueryResults) stalls the command
    // processor until the results arrive, 1 (kDoNotPredicateIfQueryResultsNotReady) never
    // stalls and leaves the tagged packets unpredicated while the results are outstanding.
    internal void SetPredication(uint condition, uint operation, uint waitOperation, ulong address)
    {
        switch (operation)
        {
            case 0:
                // Clearing predication reads no memory, so no GPU result can be outstanding.
                PredicateSkip = false;
                Interlocked.Increment(ref _predicationClears);
                break;
            case 3:
            {
                if (address == 0)
                {
                    throw _host.Fatal("The predication address is zero.");
                }

                if (waitOperation != 0)
                {
                    // The results are outstanding while work is queued behind this packet, and
                    // the game asked for the packets to run unpredicated in that case.
                    PredicateSkip = false;
                    Interlocked.Increment(ref _predicationBoolUnpredicated);
                    break;
                }

                Interlocked.Increment(ref _predicationBoolWaiting);
                _host.FlushAndWait();
                var value = ReadQword(address);
                PredicateSkip = condition switch
                {
                    0 => value != 0,
                    1 => value == 0,
                    _ => throw _host.Fatal($"The predication condition is unknown: condition=0x{condition:X8} address=0x{address:X16}."),
                };
                TracePredication(condition, waitOperation, address, value);
                VisibilityResultTrace.Predicate(QueueId, SubmitId, address, value, condition, waitOperation, PredicateSkip);
                break;
            }

            default:
                throw _host.Fatal($"The predication operation is unknown: operation=0x{operation:X8} address=0x{address:X16}.");
        }
    }

    private static long _predicationTraceCount;
    private readonly Queue<(ulong Address, ulong Value, long Evaluation, long Timestamp)> _skippedPredicates = new();

    // Each evaluation decides which later predicated packets run; see docs/image-clear-tracing.md.
    private void TracePredication(uint condition, uint waitOperation, ulong address, ulong value)
    {
        if (!Images.ImageClearTrace.Enabled) return;
        var evaluation = Interlocked.Increment(ref _predicationTraceCount);
        RecheckSkippedPredicates(evaluation);
        if (evaluation > 256) return;
        Console.Error.WriteLine($"[GPU][TRACE] Predication address=0x{address:X16} value=0x{value:X16} condition={condition} " +
            $"wait={waitOperation} skip={PredicateSkip} evaluation={evaluation}");
        if (evaluation <= 32) _host.TraceWritersOf($"predicate=0x{address:X16}", address, sizeof(ulong));
        if (PredicateSkip && _skippedPredicates.Count < 64)
            _skippedPredicates.Enqueue((address, value, evaluation, System.Diagnostics.Stopwatch.GetTimestamp()));
    }

    // A value that changes after its evaluation shows a writer that ran too late for the skip decision.
    private void RecheckSkippedPredicates(long evaluation)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        for (var remaining = _skippedPredicates.Count; remaining > 0; remaining--)
        {
            var entry = _skippedPredicates.Dequeue();
            var current = _host.TryReadGuest(entry.Address, bytes) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes) : entry.Value;
            if (current != entry.Value)
            {
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(entry.Timestamp).TotalMilliseconds;
                Console.Error.WriteLine($"[GPU][TRACE] PredicationChanged address=0x{entry.Address:X16} evaluated=0x{entry.Value:X16} " +
                    $"now=0x{current:X16} evaluation={entry.Evaluation} laterEvaluations={evaluation - entry.Evaluation} elapsedMs={elapsed:F3}");
                continue;
            }
            if (evaluation - entry.Evaluation < 64) _skippedPredicates.Enqueue(entry);
        }
    }

    internal void WaitForConstantEngine()
    {
        if (ConstantEngineCount <= DrawEngineCount && !ConstantEngineComplete)
        {
            Suspend();
        }
    }

    internal void WaitForDrawEngineDifference(uint difference)
    {
        if (DrawEngineCount > ConstantEngineCount)
        {
            throw _host.Fatal($"The DE counter is ahead of the CE counter: de={DrawEngineCount} ce={ConstantEngineCount}.");
        }

        if (ConstantEngineCount - DrawEngineCount >= difference)
        {
            Suspend();
        }
    }

    internal void WaitForRewind(bool valid)
    {
        if (!valid)
        {
            Suspend();
        }
        else
        {
            _host.CpuGpuVisibilityBoundary();
        }
    }

    internal uint WaitRegisterMemory32Packet(in PacketContext packet, ReadOnlySpan<uint> payload) =>
        WaitRegisterMemoryPacket(packet, payload, is64Bit: false);

    internal uint WaitRegisterMemory64Packet(in PacketContext packet, ReadOnlySpan<uint> payload) =>
        WaitRegisterMemoryPacket(packet, payload, is64Bit: true);

    private uint WaitRegisterMemoryPacket(in PacketContext packet, ReadOnlySpan<uint> payload, bool is64Bit)
    {
        Diagnostics.DispatchRunTally.NoteWait();
        var valueDwords = is64Bit ? 2u : 1u;
        var payloadDwords = 4u + (valueDwords * 2u);
        var opcode = is64Bit ? PacketOpcode.WaitRegisterMemory64 : PacketOpcode.WaitRegisterMemory;
        if (packet.Opcode != opcode || packet.CustomCode != 0 || packet.Length != payloadDwords + 1)
        {
            throw _host.Fatal($"The wait packet header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var alignmentMask = is64Bit ? 0x7u : 0x3u;
        var control = payload[0];
        var address = (payload[1] & ~alignmentMask) | ((ulong)(payload[2] & 0x3FFFFu) << 32);
        var reference = ReadValue(payload, 3, is64Bit);
        var mask = ReadValue(payload, 3 + (int)valueDwords, is64Bit);
        if ((control & 0x10u) == 0)
        {
            throw _host.Fatal($"A register-space wait is not supported: control=0x{control:X8} address=0x{packet.PacketAddress:X16}.");
        }

        WaitOnMemory(control & 0x7u, address, reference, mask, WaitOperation.Decode(control, is64Bit), is64Bit);
        return payloadDwords;
    }

    private static ulong ReadValue(ReadOnlySpan<uint> payload, int index, bool is64Bit) =>
        is64Bit ? payload[index] | ((ulong)payload[index + 1] << 32) : payload[index];

    private static readonly bool TraceConditionalExecute = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COND_EXEC") == "1";
    private static int _conditionalExecuteTraces;
    private static long _conditionalExecuteEvents;

    // A COND_EXEC predicate that earlier GPU work may still write costs the command thread a GPU round
    // trip. One that read 0 ConditionPredictAfter times in a row is predicted 0 (block skipped) without
    // waiting; every ConditionVerifyEvery-th use is read for real. A mismatch restarts the streak and
    // doubles the zeros needed before predicting again (Yotei sets its flag once while loading, then
    // never); after ConditionMaxMismatches prediction stops for that address. Only "skip" is
    // predicted: a wrongly executed block could wait forever.
    private static readonly bool PredictConditions =
        Environment.GetEnvironmentVariable("SHARPEMU_PREDICT_COND_EXEC") != "0";
    internal const int ConditionPredictAfter = 8;
    internal const int ConditionVerifyEvery = 16;
    internal const int ConditionMaxMismatches = 8;
    private sealed class ConditionPredictor { public int ZeroStreak; public int SinceVerified; public int Mismatches; }
    private Dictionary<ulong, ConditionPredictor>? _conditionPredictors;

    private uint ReadCondition(ulong address)
    {
        if (!PredictConditions || !_host.HasGpuWrittenCommandData(address, sizeof(uint)))
        {
            return ReadDword(address);
        }

        var predictors = _conditionPredictors ??= new();
        if (!predictors.TryGetValue(address, out var predictor))
        {
            predictors[address] = predictor = new ConditionPredictor();
        }

        var predicting = predictor.Mismatches < ConditionMaxMismatches &&
            predictor.ZeroStreak >= ConditionPredictAfter << predictor.Mismatches;
        if (predicting && ++predictor.SinceVerified < ConditionVerifyEvery)
        {
            SharpEmu.Libs.Diagnostics.ProducerWaitProfile.Path("cond_exec.predicted");
            return 0;
        }

        var value = ReadDword(address);
        if (predicting)
        {
            predictor.SinceVerified = 0;
            if (value != 0)
            {
                predictor.Mismatches++;
                Console.Error.WriteLine(predictor.Mismatches < ConditionMaxMismatches
                    ? $"[GPU][WARN][COND_EXEC_PREDICT] predicate 0x{address:X16} read 0x{value:X8} after a predicted 0; predicting again after {ConditionPredictAfter << predictor.Mismatches} zeros."
                    : $"[GPU][WARN][COND_EXEC_PREDICT] predicate 0x{address:X16} read 0x{value:X8} after a predicted 0; prediction stops for it.");
            }
        }

        predictor.ZeroStreak = value == 0 ? predictor.ZeroStreak + 1 : 0;
        return value;
    }

    internal uint ConditionalExecutePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var payloadDwords = packet.Length - 1;
        if (payloadDwords < 4)
        {
            throw _host.Fatal($"The conditional-execute packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        var address = ((ulong)payload[1] << 32) | (payload[0] & 0xFFFF_FFFCu);
        var control = payload[0] & 0x3u;
        var executeCount = payload[3] & 0x3FFFu;
        if (control != 0 || payload[2] != 0 || address == 0 || payloadDwords + executeCount >= packet.Remaining)
        {
            throw _host.Fatal(
                $"The conditional-execute packet is not supported: control={control} reserved=0x{payload[2]:X8} " +
                $"address=0x{address:X16} count={executeCount} remaining={packet.Remaining}.");
        }

        var predicate = ReadCondition(address);
        if (TraceConditionalExecute && (predicate != 0 || ++_conditionalExecuteEvents % 50 == 1) &&
            Interlocked.Increment(ref _conditionalExecuteTraces) <= 48)
        {
            var block = packet.PacketAddress + (payloadDwords + 1) * sizeof(uint);
            Console.Error.WriteLine($"[GPU][TRACE] COND_EXEC n={_conditionalExecuteEvents} packet=0x{packet.PacketAddress:X16} predicate=0x{address:X16} value=0x{predicate:X8} " +
                $"count={executeCount} queue={QueueId} block={DescribePackets(block, executeCount)}");
        }
        if (predicate != 0) return payloadDwords;
        TraceSkippedPackets("conditional-execute", packet.PacketAddress + (payloadDwords + 1) * sizeof(uint), executeCount, (uint)address);
        return payloadDwords + executeCount;
    }

    internal uint SetPredicationPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var payloadDwords = packet.Length - 1;
        if (payloadDwords < 2)
        {
            throw _host.Fatal($"The predication packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        const uint flagsMask = 0x0007_1100u;
        uint flags;
        ulong address;
        if (payloadDwords >= 3 && (payload[0] & ~flagsMask) == 0 && payload[2] <= 0xFFFFu)
        {
            flags = payload[0];
            address = ((ulong)payload[2] << 32) | (payload[1] & 0xFFFF_FFF0u);
        }
        else
        {
            flags = payload[1];
            address = (payload[0] & 0xFFFF_FFF0u) | ((ulong)(payload[1] & 0xFFu) << 32);
        }

        SetPredication((flags >> 8) & 0x1u, (flags >> 16) & 0x7u, (flags >> 12) & 0x1u, address);
        return payloadDwords;
    }

    internal uint RewindPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(2, PacketOpcode.Rewind) || (payload[0] & ~RewindKnownBits) != 0)
        {
            throw _host.Fatal($"The rewind packet is not supported: header=0x{packet.Header:X8} body=0x{payload[0]:X8} address=0x{packet.PacketAddress:X16}.");
        }

        WaitForRewind((payload[0] & RewindValidBit) != 0);
        return 1;
    }

    internal uint IncrementCeCounterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireExactPacket(packet, PacketHeader.Make(2, PacketOpcode.IncrementCeCounter), payload[0], 1);
        ConstantEngineCount++;
        return 1;
    }

    internal uint IncrementDeCounterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireExactPacket(packet, PacketHeader.Make(2, PacketOpcode.IncrementDeCounter), payload[0], 0);
        DrawEngineCount++;
        return 1;
    }

    internal uint WaitOnCeCounterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireExactPacket(packet, PacketHeader.Make(2, PacketOpcode.WaitOnCeCounter), payload[0], 1);
        WaitForConstantEngine();
        return 1;
    }

    internal uint WaitOnDeCounterDiffPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(2, PacketOpcode.WaitOnDeCounterDiff))
        {
            throw _host.Fatal($"The DE counter wait header is not supported: header=0x{packet.Header:X8}.");
        }

        WaitForDrawEngineDifference(payload[0]);
        return 1;
    }

    private void RequireExactPacket(in PacketContext packet, uint header, uint body, uint expectedBody)
    {
        if (packet.Header != header || body != expectedBody)
        {
            throw _host.Fatal($"The packet is not supported: header=0x{packet.Header:X8} body=0x{body:X8} address=0x{packet.PacketAddress:X16}.");
        }
    }
}
