// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static class SpirvInstanceCount
{
    public const uint PushWordCount = 6;

    public static byte[] Create()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var uintType = module.TypeInt(32, false);
        var boolType = module.TypeBool();
        uint Constant(uint value) => module.Constant(uintType, value);
        uint Operation(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, uintType, left, right);
        uint Compare(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, boolType, left, right);
        var array = module.TypeRuntimeArray(uintType);
        module.AddDecoration(array, SpirvDecoration.ArrayStride, 4);
        var structure = module.TypeStruct(array);
        module.AddDecoration(structure, SpirvDecoration.Block);
        module.AddMemberDecoration(structure, 0, SpirvDecoration.Offset, 0);
        var bufferPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, structure);
        var wordPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, uintType);
        var buffers = new uint[4];
        for (uint index = 0; index < buffers.Length; index++)
        {
            buffers[index] = module.AddGlobalVariable(bufferPointer, SpirvStorageClass.StorageBuffer);
            module.AddDecoration(buffers[index], SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(buffers[index], SpirvDecoration.Binding, index);
        }
        var pushArray = module.TypeArray(uintType, PushWordCount);
        module.AddDecoration(pushArray, SpirvDecoration.ArrayStride, 4);
        var pushStructure = module.TypeStruct(pushArray);
        module.AddDecoration(pushStructure, SpirvDecoration.Block);
        module.AddMemberDecoration(pushStructure, 0, SpirvDecoration.Offset, 0);
        var push = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.PushConstant, pushStructure), SpirvStorageClass.PushConstant);
        var pushWord = module.TypePointer(SpirvStorageClass.PushConstant, uintType);
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        uint LoadPush(uint index) => module.AddInstruction(SpirvOp.Load, uintType,
            module.AddInstruction(SpirvOp.AccessChain, pushWord, push, Constant(0), Constant(index)));
        uint Pointer(int buffer, uint index) => module.AddInstruction(SpirvOp.AccessChain, wordPointer, buffers[buffer], Constant(0), index);
        uint Read(int buffer, uint index) => module.AddInstruction(SpirvOp.Load, uintType, Pointer(buffer, index));
        void If(uint condition, Action body)
        {
            var yes = module.AllocateId();
            var end = module.AllocateId();
            module.AddStatement(SpirvOp.SelectionMerge, end, 0);
            module.AddStatement(SpirvOp.BranchConditional, condition, yes, end);
            module.AddLabel(yes);
            body();
            module.AddStatement(SpirvOp.Branch, end);
            module.AddLabel(end);
        }
        // One invocation preserves stream ordering. Count zero leaves the register unchanged.
        var active = Compare(SpirvOp.ULessThan, LoadPush(4), Read(1, LoadPush(1)));
        for (uint word = 0; word < 5; word++)
        {
            If(Compare(SpirvOp.ULessThan, Constant(word), LoadPush(5)), () =>
            {
                var destination = Pointer(3, Operation(SpirvOp.IAdd, LoadPush(3), Constant(word)));
                module.AddStatement(SpirvOp.Store, destination, Constant(0));
                If(active, () => module.AddStatement(SpirvOp.Store, destination,
                    Read(0, Operation(SpirvOp.IAdd, LoadPush(0), Constant(word)))));
            });
        }
        If(active, () => module.AddStatement(SpirvOp.Store, Pointer(2, LoadPush(2)),
            Read(0, Operation(SpirvOp.IAdd, LoadPush(0), Constant(1)))));
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [.. buffers, push]);
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 1, 1, 1);
        return module.Build();
    }
}
