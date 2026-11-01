// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using Silk.NET.Vulkan;

// This partial identifies Vulkan device-loss failures.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // The pooled depth-feedback snapshots belong to the render thread's recording host;
        // an allocation failing on a worker or the command thread leaves them alone.
        private void ReleaseFeedbackSnapshotsOnRenderThread()
        {
            if (OnCommandThread || OnResolveWorker ||
                Thread.CurrentThread.Name?.StartsWith("SharpEmu render worker", StringComparison.Ordinal) == true)
            {
                return;
            }

            _recording?.DestroyFeedbackSnapshotPool();
        }

        private Result CreateImageWithRecovery(in ImageCreateInfo info, out Image image)
        {
            var create = info;
            var result = _vk.CreateImage(_device, &create, null, out image);
            if ((result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory) && _imageCache is not null)
            {
                ReleaseFeedbackSnapshotsOnRenderThread();
                _imageCache.ReclaimForAllocation();
                result = _vk.CreateImage(_device, &create, null, out image);
            }
            return result;
        }

        private Result AllocateMemoryWithRecovery(in MemoryAllocateInfo info, out DeviceMemory memory)
        {
            var result = _deviceInfo.AllocateMemory(info, out memory);
            if ((result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory) && _imageCache is not null)
            {
                // Presentation/feedback resources compete with guest images on the same heap.
                ReleaseFeedbackSnapshotsOnRenderThread();
                _imageCache.ReclaimForAllocation();
                result = _deviceInfo.AllocateMemory(info, out memory);
            }
            return result;
        }

        private static void Check(Result result, string operation)
        {
            if (result == Result.ErrorDeviceLost)
            {
                if (Gpu.Vulkan.GpuCheckpoints.Enabled)
                    Gpu.Vulkan.GpuCheckpoints.DumpOnce();
                throw new VulkanDeviceLostException(operation);
            }

            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{operation} failed with {result}.");
            }
        }

        // Typed so the frame-boundary catch can recognize device loss without
        // depending on the exact wording of the exception message.
        private sealed class VulkanDeviceLostException(string operation)
            : InvalidOperationException($"{operation} failed with {Result.ErrorDeviceLost}.");

        private bool TryMarkDeviceLost(Exception exception)
        {
            // Prefer the typed signal; fall back to the message for losses that
            // surface through other layers (e.g. Silk.NET bindings).
            if (exception is not VulkanDeviceLostException &&
                !exception.Message.Contains(nameof(Result.ErrorDeviceLost), StringComparison.Ordinal))
            {
                return false;
            }

            if (Gpu.Vulkan.GpuCheckpoints.Enabled)
                Gpu.Vulkan.GpuCheckpoints.DumpOnce();
            _deviceLost = true;
            if (!_deviceLostLogged)
            {
                _deviceLostLogged = true;
                var submit = string.IsNullOrEmpty(_lastSubmitDebugName)
                    ? "submit=<none>"
                    : $"submit={_lastSubmitDebugName}";
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                    $"{submit} {exception.Message}");
            }

            return true;
        }
    }
}
