// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private PipelineCompileScheduler? _pipelineCompiles;
        private readonly object _pipelineCompileInitGate = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<Action> _pipelineProducers = new();
        void IShaderPipelineHost.RegisterPipelineProducer(Action stop) => _pipelineProducers.Add(stop);
        private readonly PipelineCompileBudget _pipelineCompileBudget = new(PipelineCompileScheduler.WorkerCount);
        PipelineCompileScheduler? IShaderPipelineHost.PipelineCompiles
        {
            get
            {
                if (!PipelineCompileScheduler.Enabled) return null;
                lock (_pipelineCompileInitGate)
                    return _pipelineCompiles ??= new PipelineCompileScheduler(PipelineCompileScheduler.WorkerCount);
            }
        }
    }
}
