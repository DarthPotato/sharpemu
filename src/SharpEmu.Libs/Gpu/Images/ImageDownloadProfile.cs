// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Gpu.Images;

// Counts image downloads (GPU image contents written back to guest memory or the buffer
// cache) by the reason they happened and by image shape, for the render profile report.
// Active only with SHARPEMU_PROFILE_RENDER=1.
internal static class ImageDownloadProfile
{
    internal enum Cause { Other, Eviction, OverlapDrop, PartialGpuWrite, BufferView }

    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1";

    private readonly record struct Key(Cause Cause, uint Width, uint Height, uint Layers, Silk.NET.Vulkan.Format Format, bool Tiled);

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, (long Count, long Bytes)> Totals = new();

    [ThreadStatic] private static Cause _current;

    // Tags downloads made on this thread until disposed; nested scopes keep the outer cause.
    internal readonly struct Scope : IDisposable
    {
        private readonly Cause _previous;
        private readonly bool _active;

        internal Scope(Cause cause)
        {
            _previous = _current;
            _active = Enabled && _previous == Cause.Other;
            if (_active)
            {
                _current = cause;
            }
        }

        public void Dispose()
        {
            if (_active)
            {
                _current = _previous;
            }
        }
    }

    internal static Scope Begin(Cause cause) => new(cause);

    internal static void Record(CachedImage image, ulong bytes)
    {
        if (!Enabled)
        {
            return;
        }

        ref readonly var info = ref image.Description;
        var key = new Key(_current, info.Extent.Width, info.Extent.Height, info.Resources.Layers, info.PixelFormat, info.IsTiled);
        lock (Gate)
        {
            Totals.TryGetValue(key, out var total);
            Totals[key] = (total.Count + 1, total.Bytes + (long)bytes);
        }
    }

    public static string TakeReport()
    {
        KeyValuePair<Key, (long Count, long Bytes)>[] entries;
        lock (Gate)
        {
            entries = Totals.ToArray();
            Totals.Clear();
        }

        var text = new StringBuilder("[PERF][IMAGE_DOWNLOAD]");
        foreach (var cause in entries.GroupBy(entry => entry.Key.Cause).OrderByDescending(group => group.Sum(entry => entry.Value.Bytes)))
        {
            text.Append(CultureInfo.InvariantCulture,
                $" {cause.Key}={cause.Sum(entry => entry.Value.Count)}/{cause.Sum(entry => entry.Value.Bytes) / (1024.0 * 1024.0):F1}MB");
        }

        foreach (var entry in entries.OrderByDescending(entry => entry.Value.Bytes).Take(10))
        {
            var key = entry.Key;
            text.Append(CultureInfo.InvariantCulture,
                $"\n[PERF][IMAGE_DOWNLOAD]   {key.Cause} {key.Width}x{key.Height}x{key.Layers} {key.Format} tiled={key.Tiled} n={entry.Value.Count} {entry.Value.Bytes / (1024.0 * 1024.0):F1}MB");
        }

        return text.ToString();
    }
}
