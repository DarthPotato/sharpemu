// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

public enum ImageRole : byte
{
    Texture,
    StorageImage,
    ColorTarget,
    DepthTarget,
    DisplaySurface,
}

// What a consumer asks the cache for: the guest image, the view it needs and the role it binds.
public struct ImageRequest
{
    public ImageDescription Description;
    public ImageViewDescription View;
    public ImageRole Role;
    internal ulong TraceTextureMetadataAddress;
    // The traced descriptor's text, by TextureDescriptorTraceText id (0 = none). A request holds no
    // reference, so the request-sized locals of hot lookups need no zeroing.
    internal int TraceTextureDescriptorId;
    internal bool TraceMetadataCompress;
    internal bool TraceWriteCompress;

    public ImageRequest(in ImageDescription description, in ImageViewDescription view, ImageRole role)
    {
        Description = description;
        View = view;
        Role = role;
    }

    internal readonly string? TraceTextureDescriptor => TextureDescriptorTraceText.Get(TraceTextureDescriptorId);

    // A refresh of an existing image needs no view.
    public static ImageRequest Refresh(in ImageDescription description, ImageRole role) => new(description, ImageViewDescription.Default, role);
}

// Descriptor texts of traced texture requests, one id per distinct text; only image tracing adds any.
internal static class TextureDescriptorTraceText
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Ids = new(StringComparer.Ordinal);
    private static readonly List<string> Texts = [];

    public static int Intern(string text)
    {
        lock (Gate)
        {
            if (!Ids.TryGetValue(text, out var id))
            {
                Texts.Add(text);
                id = Texts.Count;
                Ids.Add(text, id);
            }

            return id;
        }
    }

    public static string? Get(int id)
    {
        if (id == 0)
        {
            return null;
        }

        lock (Gate)
        {
            return Texts[id - 1];
        }
    }
}
