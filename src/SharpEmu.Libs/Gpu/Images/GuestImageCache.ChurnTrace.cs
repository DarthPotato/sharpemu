// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Gpu.Images;

// SHARPEMU_IMAGE_CHURN_TRACE=1: why does a steady state delete and create the same images every frame?
// Every deletion names its reason. Per reason, a summary line every SummaryFrames frames counts the
// deletions and how many of them the same guest address replaced within RecreateWindowFrames frames.
// The first MaxEvents deletions after the first summary window also print individually with the top of
// the caller stack. All of it runs under the cache lock and costs nothing when the variable is unset.
public sealed partial class GuestImageCache
{
    private const ulong SummaryFrames = 600;
    private const ulong RecreateWindowFrames = 2;
    private const int MaxEvents = 64;

    private sealed class ChurnReason
    {
        public ulong Deletions;
        public ulong RecreatedWithinWindow;
        public readonly Dictionary<string, ulong> Shapes = new();
    }

    private static class ImageChurnTrace
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_CHURN_TRACE") == "1";
    }

    private readonly Dictionary<string, ChurnReason> _churnReasons = new();
    private readonly Dictionary<ulong, (ulong Frame, string Reason)> _churnLastDeletion = new();
    private int _churnEventsReported;

    private static string ChurnShape(CachedImage image) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{image.Backing.Format}:{image.Backing.Extent.Width}x{image.Backing.Extent.Height}x{image.Backing.Extent.Depth}");

    private void NoteImageDeleted(CachedImage image, string reason)
    {
        if (!_churnReasons.TryGetValue(reason, out var stats))
        {
            stats = new ChurnReason();
            _churnReasons[reason] = stats;
        }

        stats.Deletions++;
        var shape = ChurnShape(image);
        stats.Shapes[shape] = stats.Shapes.GetValueOrDefault(shape) + 1;
        var range = image.Description.Data;
        var hasAddress = !ImageDescription.IsEmptyRange(range);
        if (hasAddress)
        {
            _churnLastDeletion[range.Address] = (_collectionTick, reason);
        }

        if (_collectionTick < SummaryFrames || _churnEventsReported >= MaxEvents)
        {
            return;
        }

        _churnEventsReported++;
        var stack = new StackTrace(2, false);
        var callers = new StringBuilder();
        for (var index = 0; index < stack.FrameCount && index < 3; index++)
        {
            var method = stack.GetFrame(index)?.GetMethod();
            if (method is null) continue;
            if (callers.Length != 0) callers.Append('<');
            callers.Append(method.DeclaringType?.Name).Append('.').Append(method.Name);
        }

        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[GPU][IMAGE_CHURN] event={_churnEventsReported} frame={_collectionTick} reason={reason} " +
            $"address=0x{range.Address:X} guest_bytes={range.Size} format={image.Backing.Format} " +
            $"extent={image.Backing.Extent.Width}x{image.Backing.Extent.Height}x{image.Backing.Extent.Depth} " +
            $"gpu_modified={image.IsGpuModified} caller={callers}"));
    }

    private void NoteImageCreated(CachedImage image)
    {
        var range = image.Description.Data;
        if (ImageDescription.IsEmptyRange(range) ||
            !_churnLastDeletion.Remove(range.Address, out var deletion) ||
            _collectionTick - deletion.Frame > RecreateWindowFrames ||
            !_churnReasons.TryGetValue(deletion.Reason, out var stats))
        {
            return;
        }

        stats.RecreatedWithinWindow++;
    }

    private void ReportImageChurn(ulong frame)
    {
        // Forget deletions nothing can match any more, so the table stays small.
        if (_churnLastDeletion.Count > 4096)
        {
            var stale = new List<ulong>();
            foreach (var (address, deletion) in _churnLastDeletion)
            {
                if (frame - deletion.Frame > RecreateWindowFrames) stale.Add(address);
            }

            foreach (var address in stale) _churnLastDeletion.Remove(address);
        }

        if (frame == 0 || frame % SummaryFrames != 0)
        {
            return;
        }

        foreach (var (reason, stats) in _churnReasons.OrderByDescending(entry => entry.Value.Deletions))
        {
            var top = string.Join(',', stats.Shapes.OrderByDescending(shape => shape.Value).Take(3)
                .Select(shape => $"{shape.Key}={shape.Value}"));
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[GPU][IMAGE_CHURN] frame={frame} reason={reason} deletions={stats.Deletions} " +
                $"recreated_within_{RecreateWindowFrames}_frames={stats.RecreatedWithinWindow} top_formats={top}"));
        }

        _churnReasons.Clear();
    }
}
