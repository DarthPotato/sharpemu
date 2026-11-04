// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The data loads each program performs on the device (ShaderResourcePlan.DeviceDataReads), in a text file beside the
// pipeline cache: a later session plans them from the first use, so the program compiles once and the pipeline cache
// holds it, instead of compiling again when it learns them. SHARPEMU_VK_PIPELINE_CACHE=0 keeps none.
internal sealed class DeviceDataReadStore(string? path)
{
    private readonly Dictionary<(ShaderStage Stage, ulong Hash), HashSet<uint>> _programs = [];
    private readonly object _gate = new();
    private bool _loaded;

    public static DeviceDataReadStore Open() => new(
        Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE") == "0"
            ? null
            : VulkanPipelineCacheStorage.ResolvePath(VideoOutExports.GetApplicationTitleId(),
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")) + ".device-data-reads");

    public HashSet<uint>? Load(ShaderStage stage, ulong hash)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _programs.TryGetValue((stage, hash), out var pcs) ? new HashSet<uint>(pcs) : null;
        }
    }

    public void Save(ShaderStage stage, ulong hash, IEnumerable<uint> pcs)
    {
        if (path is null)
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();
            _programs[(stage, hash)] = new HashSet<uint>(pcs);
            try
            {
                var lines = _programs.Select(static program =>
                    $"{(uint)program.Key.Stage} {program.Key.Hash:X16} {string.Join(',', program.Value.Order().Select(static pc => pc.ToString("X")))}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + $".{Environment.ProcessId}.tmp";
                File.WriteAllLines(temporary, lines);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[GPU][WARN][DEVICE_DATA_READS] The learned loads could not be kept: {exception.Message}");
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || !uint.TryParse(fields[0], out var stage) ||
                    !ulong.TryParse(fields[1], System.Globalization.NumberStyles.HexNumber, null, out var hash))
                {
                    continue;
                }

                var pcs = new HashSet<uint>();
                foreach (var pc in fields[2].Split(','))
                {
                    if (uint.TryParse(pc, System.Globalization.NumberStyles.HexNumber, null, out var value))
                    {
                        pcs.Add(value);
                    }
                }

                _programs[((ShaderStage)stage, hash)] = pcs;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _programs.Clear();
            Console.Error.WriteLine($"[GPU][WARN][DEVICE_DATA_READS] The learned loads could not be read: {exception.Message}");
        }
    }
}
