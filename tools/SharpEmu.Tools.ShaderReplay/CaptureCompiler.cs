// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.ShaderCompiler;

sealed class CapturedShader : IGuestCompiledShader
{
    public byte[] Payload => [];
    public string PayloadFileExtension => "spv";
}
internal sealed class CaptureCompiler : IGuestGpuBackend
{
    private static Exception Unsupported() => new NotSupportedException("The fake compiler does not implement this call.");

    public List<ShaderCompileRequest> Requests { get; } = new();

    public string? Rejection { get; set; }

    public List<CapturedShader> Shaders { get; } = new();

    public int Compilations { get; private set; }

    public string BackendName => "Fake";

    public bool LinkedShaderCallsSupported { get; set; } = true;


    public bool TryCompileProgram(ShaderCompileRequest request, out IGuestCompiledShader? shader, out string error)
    {
        Requests.Add(request);
        if (Rejection is { } rejection)
        {
            shader = null;
            error = rejection;
            return false;
        }
        var compiled = new CapturedShader();
        Shaders.Add(compiled);
        shader = compiled;
        error = string.Empty;
        return true;
    }

    public void CountShaderCompilation() => Compilations++;

    public IGuestCompiledShader GetDepthOnlyFragmentShader() => throw Unsupported();

    public void EnsureStarted(uint width, uint height) => throw Unsupported();




    public void HideSplashScreen() => throw Unsupported();

    public void Submit(byte[] bgraFrame, uint width, uint height) => throw Unsupported();

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) => throw Unsupported();

    public bool TrySubmitGuestImage(int videoOutHandle, int displayBufferIndex, ulong address, uint width, uint height, uint pitchInPixel, ulong flipRequestId) => throw Unsupported();

    public void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots) => throw Unsupported();

    public IdleOutcome SubmitDone(ICpuMemory memory) => throw Unsupported();

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) => throw Unsupported();

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) => false;

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() => throw Unsupported();

    public void RequestClose() => throw Unsupported();
}

