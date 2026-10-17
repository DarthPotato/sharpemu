// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // SHARPEMU_SHADER_SIZE_PROFILE=1 reports, per compiled module, the function-body
        // words and basic blocks each guest opcode emitted, to find what makes a module slow
        // to compile.
        private static readonly bool SizeProfileEnabled = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SIZE_PROFILE"), "1", StringComparison.Ordinal);

        private Dictionary<string, (int Count, long Words, long Labels)>? _sizeProfile;
        private int _sizeProfileLabelMark;

        private int SizeProfileMark()
        {
            if (!SizeProfileEnabled)
            {
                return 0;
            }

            _sizeProfileLabelMark = _module.LabelCount;
            return _module.FunctionWordCount;
        }

        private void SizeProfileRecord(string opcode, int mark)
        {
            if (!SizeProfileEnabled)
            {
                return;
            }

            _sizeProfile ??= [];
            _sizeProfile.TryGetValue(opcode, out var entry);
            _sizeProfile[opcode] = (
                entry.Count + 1,
                entry.Words + (_module.FunctionWordCount - mark),
                entry.Labels + (_module.LabelCount - _sizeProfileLabelMark));
        }

        private void SizeProfileReport(int moduleWords)
        {
            if (!SizeProfileEnabled || _sizeProfile is null)
            {
                return;
            }

            static string Format(KeyValuePair<string, (int Count, long Words, long Labels)> pair) =>
                $"{pair.Key}:{pair.Value.Count}x{pair.Value.Words / Math.Max(pair.Value.Count, 1)}={pair.Value.Words}/L{pair.Value.Labels}";

            var instructionWords = _sizeProfile.Values.Sum(static entry => entry.Words);
            var instructionLabels = _sizeProfile.Values.Sum(static entry => entry.Labels);
            var top = _sizeProfile.OrderByDescending(static pair => pair.Value.Words).Take(15).Select(Format);
            var topLabels = _sizeProfile.Where(static pair => pair.Value.Labels > 0)
                .OrderByDescending(static pair => pair.Value.Labels).Take(10).Select(Format);
            Console.Error.WriteLine(
                $"[SHADER][SIZE] stage={_stage} address=0x{_request.Program.Address:X16} hash=0x{_request.Hash:X16} " +
                $"lowering={_waveLowering.GetType().Name} instructions={_request.Program.Instructions.Count} " +
                $"module_words={moduleWords} function_words={_module.FunctionWordCount} labels={_module.LabelCount} " +
                $"instruction_words={instructionWords} instruction_labels={instructionLabels} " +
                $"top=[{string.Join(' ', top)}] top_labels=[{string.Join(' ', topLabels)}]");
        }
    }
}
