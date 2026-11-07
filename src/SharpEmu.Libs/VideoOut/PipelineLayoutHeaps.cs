// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

// Shared by runtime creation and replay, including the pixel stage's set requirements.
internal readonly record struct PipelineLayoutHeaps(bool Bindless, bool Runtime)
{
    internal static PipelineLayoutHeaps For(BindingLayout? first, BindingLayout? second = null) => new(
        first?.UsesBindlessHeap == true || second?.UsesBindlessHeap == true,
        first?.Find(DescriptorBindingKind.RuntimeDescriptorTable) is not null ||
        second?.Find(DescriptorBindingKind.RuntimeDescriptorTable) is not null);
}
