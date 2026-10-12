// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

/// <summary>
/// The chime played with a trophy notification. It is synthesized here, so no third-party audio
/// ships with the launcher: a few bell tones rising in an E major arpeggio, one more for each grade.
/// </summary>
/// <remarks>
/// It plays through its own waveOut device rather than PlaySound, which the title music already
/// holds; a chime must not stop the music. Like the title music, this is Windows-only.
/// </remarks>
internal static class TrophySound
{
    private const int SampleRate = 44100;
    private const double Volume = 0.42;

    // E6, G#6, B6, E7 and B5 below them: the platinum starts lower and climbs further.
    private static readonly double[] BronzeNotes = [1318.51, 1975.53];
    private static readonly double[] GoldNotes = [1318.51, 1661.22, 1975.53];
    private static readonly double[] PlatinumNotes = [987.77, 1318.51, 1661.22, 1975.53, 2637.02];

    private static readonly Dictionary<int, byte[]> Rendered = [];

    public static bool Enabled { get; set; } = true;

    public static void Play(int grade)
    {
        if (!Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        byte[] pcm;
        lock (Rendered)
        {
            if (!Rendered.TryGetValue(grade, out pcm!))
            {
                pcm = Render(grade);
                Rendered[grade] = pcm;
            }
        }

        _ = Task.Run(() => PlayPcm(pcm));
    }

    // 16-bit mono PCM. Each note is a struck bell: a few harmonics that die away, the higher ones
    // faster, with a short attack so the onset does not click.
    internal static byte[] Render(int grade)
    {
        var notes = grade switch { 1 => PlatinumNotes, 2 => GoldNotes, _ => BronzeNotes };
        var spacing = grade == 1 ? 0.085 : 0.075;
        var ring = grade switch { 1 => 1.9, 2 => 1.3, 3 => 1.1, _ => 1.0 };
        // Silver and platinum ring a little brighter than bronze and gold.
        var brightness = grade is 1 or 3 ? 1.35 : 1.0;
        var length = (int)((spacing * (notes.Length - 1) + ring) * SampleRate);
        var mix = new double[length];
        for (var n = 0; n < notes.Length; n++)
        {
            var start = (int)(n * spacing * SampleRate);
            // The last note of the arpeggio is the one that rings.
            var decay = n == notes.Length - 1 ? 3.2 / ring : 7.0;
            AddBell(mix, start, notes[n], decay, brightness, n == notes.Length - 1 ? 1.0 : 0.8);
        }

        if (grade == 1)
        {
            // A soft E5 underneath gives the platinum some body.
            AddBell(mix, 0, 659.25, 1.6, 0.5, 0.35);
        }

        var peak = mix.Max(Math.Abs);
        var scale = peak == 0 ? 0 : Volume / peak * short.MaxValue;
        var pcm = new byte[length * 2];
        for (var i = 0; i < length; i++)
        {
            // Fade the last 30 ms so the buffer ends on silence.
            var tail = Math.Min(1.0, (length - i) / (0.03 * SampleRate));
            var sample = (short)Math.Clamp(mix[i] * scale * tail, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }

        return pcm;
    }

    private static void AddBell(double[] mix, int start, double frequency, double decay, double brightness, double gain)
    {
        ReadOnlySpan<(double Ratio, double Amplitude)> partials = [(1.0, 1.0), (2.0, 0.32), (3.0, 0.11), (4.2, 0.05)];
        const double attack = 0.004;
        for (var i = start; i < mix.Length; i++)
        {
            var t = (i - start) / (double)SampleRate;
            var envelope = Math.Min(1.0, t / attack);
            var sum = 0.0;
            for (var p = 0; p < partials.Length; p++)
            {
                var (ratio, amplitude) = partials[p];
                var level = p == 0 ? amplitude : amplitude * brightness;
                sum += level * Math.Exp(-t * decay * (1 + p * 0.9)) * Math.Sin(2 * Math.PI * frequency * ratio * t);
            }

            mix[i] += gain * envelope * sum;
        }
    }

    private static void PlayPcm(byte[] pcm)
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = SampleRate * 2,
            BlockAlign = 2,
            BitsPerSample = 16,
        };
        if (waveOutOpen(out var device, WaveMapper, ref format, 0, 0, 0) != 0)
        {
            return;
        }

        var data = Marshal.AllocHGlobal(pcm.Length);
        var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        try
        {
            Marshal.Copy(pcm, 0, data, pcm.Length);
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)pcm.Length }, header, false);
            var size = (uint)Marshal.SizeOf<WaveHeader>();
            if (waveOutPrepareHeader(device, header, size) != 0)
            {
                return;
            }

            if (waveOutWrite(device, header, size) == 0)
            {
                // Wait for the device to finish the buffer, with a margin in case it never reports.
                var deadline = Environment.TickCount64 + pcm.Length * 1000L / (SampleRate * 2) + 2000;
                while ((Marshal.PtrToStructure<WaveHeader>(header).Flags & WaveHeaderDone) == 0 &&
                       Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(50);
                }
            }

            _ = waveOutReset(device);
            _ = waveOutUnprepareHeader(device, header, size);
        }
        finally
        {
            _ = waveOutClose(device);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }
    }

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WaveHeaderDone = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out nint device, uint deviceId, ref WaveFormat format, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(nint device);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(nint device);
}
