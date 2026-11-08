// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// SHARPEMU_TRACE_SYNC_CHAIN=<seconds>: once, that many seconds after launch, records six presented frames and
// prints them in time order ([SYNC_CHAIN] lines, milliseconds from the first flip of the capture): every wait
// of the command thread (round trips and their reads, pending writers, idle), the render thread (GPU waits,
// idle) and of any thread on GPU readbacks (with the faulting guest address); command-stream slices and how
// long each submission waited to start; guest threads' slow HLE imports, submissions and parked waits (all
// imports but the bulk ones on unnamed threads, i.e. the game's main thread), plus the guest resume rate.
// Shows which results each frame waits for, in which order, and who waits meanwhile. Off: one static check
// per site.
internal static class SyncChainTrace
{
    internal enum Kind : byte
    {
        Flip,
        RoundTrip,
        Read,
        PendingWait,
        ProducerIdle,
        GpuWait,
        RenderIdle,
        Import,
        Admit,
        Slice,
        Park,
    }

    private sealed class ParkStart
    {
        public long Start;
        public string? Label;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ParkStart> Parks = new();
    private static long _resumes;
    private static long _protectCallsAtStart;
    private static long _protectPagesAtStart;
    private static long[] _guardAtStart = new long[5];

    private static long[] GuardCounters() =>
    [
        Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.GuardRelaxCalls),
        Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.GuardRelaxPages),
        Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.GuardRestrictCalls),
        Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.GuardRestrictPages),
        Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.Faults),
    ];

    // A guest thread yields its host thread for a blocking wait, and resumes later (possibly elsewhere).
    public static void NotePark(object context, string export, string wakeKey)
    {
        if (!Recording)
        {
            return;
        }

        var park = Parks.GetOrCreateValue(context);
        park.Start = Stopwatch.GetTimestamp();
        park.Label = export + "/" + wakeKey;
    }

    public static void NoteResume(object context)
    {
        if (!Recording)
        {
            return;
        }

        Interlocked.Increment(ref _resumes);
        if (Parks.TryGetValue(context, out var park) && park.Start != 0)
        {
            Note(Kind.Park, park.Start, park.Label);
            park.Start = 0;
        }
    }

    private struct Event
    {
        public long Start;
        public long End;
        public long Extra;
        public Kind Kind;
        public int Queue;
        public string? Label;
        public string? Purpose;
        public int Thread;
        public string? ThreadName;
        public string? Stack;
    }

    private const int Frames = 6;
    private const int Capacity = 32768;
    private static readonly long StartAfter =
        double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SYNC_CHAIN"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency) : 0;
    public static readonly bool Enabled = StartAfter != 0;

    private static readonly Event[] Events = Enabled ? new Event[Capacity] : [];
    private static int _count;
    // 0 waiting for the start time (recording begins at the first flip after it), 2 recording, 3 done.
    private static int _state;
    private static int _flips;

    public static bool Recording => Enabled && Volatile.Read(ref _state) == 2;

    // The guest address whose CPU fault this thread is resolving (0 when none).
    [ThreadStatic] public static ulong FaultAddress;

    public static long Begin() => Recording ? Stopwatch.GetTimestamp() : 0;

    public static void Note(Kind kind, long start, string? label = null, int queue = -2, long extra = 0)
    {
        if (start == 0 || !Recording)
        {
            return;
        }

        var end = Stopwatch.GetTimestamp();
        var index = Interlocked.Increment(ref _count) - 1;
        if (index >= Capacity)
        {
            return;
        }

        Events[index] = new Event
        {
            Start = start,
            End = end,
            Extra = extra != 0 ? extra : kind == Kind.GpuWait ? (long)Interlocked.Exchange(ref FaultAddress, 0) : 0,
            Kind = kind,
            Queue = queue,
            Label = label,
            Purpose = ProducerWaitProfile.CurrentPurpose,
            Thread = Environment.CurrentManagedThreadId,
            ThreadName = System.Threading.Thread.CurrentThread.Name,
            Stack = kind == Kind.GpuWait && end - start >= Stopwatch.Frequency / 2000 ? CompactStack() : null,
        };
    }

    private static string CompactStack() =>
        string.Join("<", new StackTrace(2, false).GetFrames().Take(14).Select(static frame => frame.GetMethod())
            .Where(static method => method is not null && method.DeclaringType != typeof(SyncChainTrace))
            .Select(static method => method!.DeclaringType?.Name + "." + method.Name));

    // Guest threads: an HLE import that took 0.2 ms or more (waits show where the game blocks), and every
    // command buffer submission.
    public static void NoteImport(long start, string name)
    {
        var elapsed = Stopwatch.GetTimestamp() - start;
        if (elapsed >= Stopwatch.Frequency / 5000 || name.Contains("Submit", StringComparison.Ordinal) ||
            (System.Threading.Thread.CurrentThread.Name is null && !IsBulkImport(name)))
        {
            Note(Kind.Import, start, name);
        }
    }

    private static bool IsBulkImport(string name) =>
        name.StartsWith("mem", StringComparison.Ordinal) || name.StartsWith("sceAgcCb", StringComparison.Ordinal) ||
        name.StartsWith("sceAgcDcb", StringComparison.Ordinal) || name.StartsWith("sceAgcSet", StringComparison.Ordinal) ||
        name.StartsWith("scePthreadMutex", StringComparison.Ordinal) || name.StartsWith("pthread_mutex", StringComparison.Ordinal);

    // Render thread, once per presented frame.
    public static void NoteFlip()
    {
        if (!Enabled)
        {
            return;
        }

        var state = Volatile.Read(ref _state);
        if (state == 0)
        {
            if (Stopwatch.GetTimestamp() >= StartAfter)
            {
                _protectCallsAtStart = Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.Calls);
                _protectPagesAtStart = Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.Pages);
                _guardAtStart = GuardCounters();
                SharpEmu.HLE.Diagnostics.HostProtectCounters.Recording = true;
                Volatile.Write(ref _state, 2);
                Note(Kind.Flip, Stopwatch.GetTimestamp());
            }

            return;
        }

        if (state != 2)
        {
            return;
        }

        Note(Kind.Flip, Stopwatch.GetTimestamp());
        if (++_flips < Frames)
        {
            return;
        }

        Volatile.Write(ref _state, 3);
        Thread.Sleep(5);
        Dump();
    }

    private static void Dump()
    {
        var count = Math.Min(Volatile.Read(ref _count), Capacity);
        var events = Events.AsSpan(0, count).ToArray();
        Array.Sort(events, static (left, right) => left.Start.CompareTo(right.Start));
        // Times count from the first flip; an admission can start before it.
        var origin = events.Length > 0 ? events[0].Start : 0;
        foreach (var e in events)
        {
            if (e.Kind == Kind.Flip)
            {
                origin = e.Start;
                break;
            }
        }
        var text = new StringBuilder();
        foreach (var e in events)
        {
            var at = (e.Start - origin) * 1000.0 / Stopwatch.Frequency;
            var duration = (e.End - e.Start) * 1000.0 / Stopwatch.Frequency;
            if (e.Kind is not (Kind.Flip or Kind.Import) && duration < 0.05)
            {
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"[SYNC_CHAIN] t={at:F2} dur={duration:F2} thread={e.Thread} kind={e.Kind} name={(e.ThreadName ?? "-").Replace(' ', '_')}");
            if (e.Queue != -2)
            {
                text.Append(CultureInfo.InvariantCulture, $" queue={e.Queue}");
            }

            if (e.Purpose is not null)
            {
                text.Append(" purpose=").Append(e.Purpose);
            }

            if (e.Label is not null)
            {
                text.Append(" label=").Append(e.Label);
            }

            if (e.Extra != 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" extra={e.Extra}");
            }

            if (e.Stack is not null)
            {
                text.Append(" stack=").Append(e.Stack);
            }

            text.AppendLine();
        }

        Console.Error.Write(text.ToString());
        var seconds = events.Length > 1 ? (events[^1].Start - origin) / (double)Stopwatch.Frequency : 0;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[SYNC_CHAIN] done events={count} resumes={Volatile.Read(ref _resumes)} resumes_per_s={(seconds > 0 ? Volatile.Read(ref _resumes) / seconds : 0):F0}" +
            $" protect_calls_per_s={(seconds > 0 ? (Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.Calls) - _protectCallsAtStart) / seconds : 0):F0}" +
            $" protect_pages_per_s={(seconds > 0 ? (Interlocked.Read(ref SharpEmu.HLE.Diagnostics.HostProtectCounters.Pages) - _protectPagesAtStart) / seconds : 0):F0}"));
        SharpEmu.HLE.Diagnostics.HostProtectCounters.Recording = false;
        foreach (var ((granule, kind), pages) in SharpEmu.HLE.Diagnostics.HostProtectCounters.Histogram.OrderByDescending(pair => pair.Value).Take(40))
        {
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[SYNC_CHAIN] protect_granule mib=0x{granule:X} kind={kind} pages={pages}"));
        }

        var guard = GuardCounters();
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[SYNC_CHAIN] guard_per_s relax_calls={(guard[0] - _guardAtStart[0]) / seconds:F0} relax_pages={(guard[1] - _guardAtStart[1]) / seconds:F0} restrict_calls={(guard[2] - _guardAtStart[2]) / seconds:F0} restrict_pages={(guard[3] - _guardAtStart[3]) / seconds:F0} gpu_faults={(guard[4] - _guardAtStart[4]) / seconds:F0}"));
    }
}
