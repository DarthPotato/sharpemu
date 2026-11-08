// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

internal sealed unsafe class NativeDevice : IDisposable
{
    private readonly Vk vk = Vk.GetApi();
    private Instance instance;
    private Device device;
    private uint maxPushDescriptors;
    private uint maxPushConstants;
    private (uint Images, uint Samplers) runtimeCapacity;
    private bool meshEnabled;
    public string Description { get; private set; } = "";

    public NativeDevice()
    {
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    private void Initialize()
    {
        var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version13 };
        var create = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
        Check(vk.CreateInstance(&create, null, out instance), "vkCreateInstance");
        uint count = 0;
        Check(vk.EnumeratePhysicalDevices(instance, &count, null), "enumerate devices");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices) Check(vk.EnumeratePhysicalDevices(instance, &count, p), "enumerate devices");
        PhysicalDevice physical = default;
        PhysicalDeviceProperties properties = default;
        foreach (var candidate in devices)
        {
            vk.GetPhysicalDeviceProperties(candidate, out var props);
            var name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "";
            if (props.VendorID == 0x10de && name.Contains("RTX 3080", StringComparison.OrdinalIgnoreCase))
            { physical = candidate; properties = props; break; }
        }
        if (physical.Handle == 0) throw new NotSupportedException("RTX 3080 not found; refusing to benchmark another GPU with the RTX profile");
        if (properties.ApiVersion < Vk.Version13) throw new NotSupportedException("Vulkan 1.3 required");

        uint extensionCount = 0;
        Check(vk.EnumerateDeviceExtensionProperties(physical, (byte*)null, &extensionCount, null), "enumerate extensions");
        var extensionProperties = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* p = extensionProperties)
            Check(vk.EnumerateDeviceExtensionProperties(physical, (byte*)null, &extensionCount, p), "enumerate extensions");
        var available = new HashSet<string>();
        foreach (var ext in extensionProperties) available.Add(SilkMarshal.PtrToString((nint)ext.ExtensionName)!);
        if (!available.Contains("VK_KHR_push_descriptor")) throw new NotSupportedException("push descriptors required");

        // Feature policy copied from Presenter.Device.Setup.CreateDevice, not a presenter instance.
        // Query extension nodes only when advertised. All enabled values are queried first.
        var v12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features };
        var v13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var draw = new PhysicalDeviceShaderDrawParametersFeatures { SType = StructureType.PhysicalDeviceShaderDrawParametersFeatures };
        var robustness = new PhysicalDeviceRobustness2FeaturesEXT { SType = StructureType.PhysicalDeviceRobustness2FeaturesExt };
        var mesh = new PhysicalDeviceMeshShaderFeaturesEXT { SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt };
        var barycentric = new PhysicalDeviceFragmentShaderBarycentricFeaturesKHR { SType = StructureType.PhysicalDeviceFragmentShaderBarycentricFeaturesKhr };
        var atomic = new PhysicalDeviceShaderImageAtomicInt64FeaturesEXT { SType = StructureType.PhysicalDeviceShaderImageAtomicInt64FeaturesExt };
        var maintenance8 = new PhysicalDeviceMaintenance8FeaturesKHR { SType = StructureType.PhysicalDeviceMaintenance8FeaturesKhr };
        var maintenance5 = new PhysicalDeviceMaintenance5FeaturesKHR { SType = StructureType.PhysicalDeviceMaintenance5FeaturesKhr };
        var minLod = new PhysicalDeviceImageViewMinLodFeaturesEXT { SType = StructureType.PhysicalDeviceImageViewMinLodFeaturesExt };
        var unified = new PhysicalDeviceUnifiedImageLayoutsFeaturesKHR { SType = StructureType.PhysicalDeviceUnifiedImageLayoutsFeaturesKhr };
        var clip = new PhysicalDeviceDepthClipEnableFeaturesEXT { SType = StructureType.PhysicalDeviceDepthClipEnableFeaturesExt };
        var clipControl = new PhysicalDeviceDepthClipControlFeaturesEXT { SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt };
        var colorWrite = new PhysicalDeviceColorWriteEnableFeaturesEXT { SType = StructureType.PhysicalDeviceColorWriteEnableFeaturesExt };
        void* chain = null;
        void Link(void* node) { ((BaseOutStructure*)node)->PNext = (BaseOutStructure*)chain; chain = node; }
        var extensions = new List<string> { "VK_KHR_push_descriptor" };
        void Extension(string name, void* node)
        {
            if (!available.Contains(name)) return;
            extensions.Add(name); Link(node);
        }
        Link(&v12); Link(&v13); Link(&draw);
        Extension("VK_EXT_robustness2", &robustness);
        Extension("VK_EXT_mesh_shader", &mesh);
        Extension("VK_KHR_fragment_shader_barycentric", &barycentric);
        Extension("VK_EXT_shader_image_atomic_int64", &atomic);
        Extension("VK_KHR_maintenance8", &maintenance8);
        Extension("VK_KHR_maintenance5", &maintenance5);
        Extension("VK_EXT_image_view_min_lod", &minLod);
        Extension("VK_KHR_unified_image_layouts", &unified);
        Extension("VK_EXT_depth_clip_enable", &clip);
        Extension("VK_EXT_depth_clip_control", &clipControl);
        Extension("VK_EXT_color_write_enable", &colorWrite);
        foreach (var name in new[] { "VK_EXT_shader_viewport_index_layer", "VK_NV_fill_rectangle", "VK_KHR_portability_subset" })
            if (available.Contains(name)) extensions.Add(name);
        var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = chain };
        vk.GetPhysicalDeviceFeatures2(physical, &features);

        var props12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var props13 = new PhysicalDeviceVulkan13Properties { SType = StructureType.PhysicalDeviceVulkan13Properties, PNext = &props12 };
        var push = new PhysicalDevicePushDescriptorPropertiesKHR { SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr, PNext = &props13 };
        var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &push };
        vk.GetPhysicalDeviceProperties2(physical, &properties2);
        maxPushDescriptors = push.MaxPushDescriptors;
        maxPushConstants = properties.Limits.MaxPushConstantsSize;
        runtimeCapacity = RuntimeDescriptorHeap.Capacities(new(
            properties.Limits.MaxPerStageDescriptorSampledImages, properties.Limits.MaxPerStageDescriptorSamplers,
            props12.MaxPerStageDescriptorUpdateAfterBindSampledImages, props12.MaxPerStageDescriptorUpdateAfterBindSamplers,
            props12.MaxDescriptorSetUpdateAfterBindSampledImages, props12.MaxDescriptorSetUpdateAfterBindSamplers,
            props12.MaxUpdateAfterBindDescriptorsInAllPools));
        bool exactFloat16 = v12.ShaderFloat16 && props12.ShaderRoundingModeRtefloat16 && props12.ShaderDenormPreserveFloat16 &&
            props12.RoundingModeIndependence == ShaderFloatControlsIndependence.IndependenceAll &&
            props12.DenormBehaviorIndependence == ShaderFloatControlsIndependence.IndependenceAll;
        if (!exactFloat16 || !v12.RuntimeDescriptorArray || !v12.ShaderSampledImageArrayNonUniformIndexing ||
            !v12.DescriptorBindingSampledImageUpdateAfterBind || !v12.DescriptorBindingPartiallyBound ||
            !v12.DescriptorBindingUpdateUnusedWhilePending || !robustness.NullDescriptor ||
            !v13.SubgroupSizeControl || props13.MinSubgroupSize > 32 || props13.MaxSubgroupSize < 32 ||
            (props13.RequiredSubgroupSizeStages & ShaderStageFlags.ComputeBit) == 0)
            throw new NotSupportedException("Device no longer satisfies the recorded RTX3080 translation profile");
        // Retain only features enabled by the presenter. In particular shaderInt8 and
        // storageBuffer16BitAccess are NOT enabled there; do not enable all supported bits.
        var next12 = v12.PNext;
        v12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features, PNext = next12,
            TimelineSemaphore = v12.TimelineSemaphore, BufferDeviceAddress = v12.BufferDeviceAddress,
            ShaderSharedInt64Atomics = v12.ShaderSharedInt64Atomics, ShaderFloat16 = exactFloat16,
            ShaderOutputLayer = v12.ShaderOutputLayer, ShaderOutputViewportIndex = v12.ShaderOutputViewportIndex,
            SamplerFilterMinmax = v12.SamplerFilterMinmax, DescriptorIndexing = true, RuntimeDescriptorArray = true,
            ShaderSampledImageArrayNonUniformIndexing = true, DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingPartiallyBound = true, DescriptorBindingUpdateUnusedWhilePending = true
        };
        var next13 = v13.PNext;
        v13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features, PNext = next13,
            DynamicRendering = true, Synchronization2 = true, SubgroupSizeControl = true,
            ComputeFullSubgroups = mesh.MeshShader && v13.ComputeFullSubgroups
        };
        mesh.TaskShader = false; mesh.MultiviewMeshShader = false;
        mesh.PrimitiveFragmentShadingRateMeshShader = false; mesh.MeshShaderQueries = false;
        meshEnabled = mesh.MeshShader;
        atomic.SparseImageInt64Atomics = false;
        robustness.RobustBufferAccess2 = robustness.RobustBufferAccess2 && features.Features.RobustBufferAccess;
        var supported = features.Features;
        features.Features = new PhysicalDeviceFeatures
        {
            TessellationShader = supported.TessellationShader, DepthBounds = supported.DepthBounds,
            GeometryShader = supported.GeometryShader, OcclusionQueryPrecise = supported.OcclusionQueryPrecise,
            IndependentBlend = supported.IndependentBlend, DualSrcBlend = supported.DualSrcBlend,
            VertexPipelineStoresAndAtomics = supported.VertexPipelineStoresAndAtomics,
            FragmentStoresAndAtomics = supported.FragmentStoresAndAtomics,
            ShaderInt64 = supported.ShaderInt64, ShaderFloat64 = supported.ShaderFloat64,
            ShaderClipDistance = supported.ShaderClipDistance, ShaderCullDistance = supported.ShaderCullDistance,
            ShaderImageGatherExtended = supported.ShaderImageGatherExtended,
            ShaderStorageImageExtendedFormats = supported.ShaderStorageImageExtendedFormats,
            ShaderStorageImageReadWithoutFormat = supported.ShaderStorageImageReadWithoutFormat,
            ShaderStorageImageWriteWithoutFormat = supported.ShaderStorageImageWriteWithoutFormat,
            TextureCompressionBC = supported.TextureCompressionBC, RobustBufferAccess = supported.RobustBufferAccess,
            DepthBiasClamp = supported.DepthBiasClamp, SampleRateShading = supported.SampleRateShading,
            SamplerAnisotropy = supported.SamplerAnisotropy, DrawIndirectFirstInstance = supported.DrawIndirectFirstInstance
        };
        uint queueCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, &queueCount, null);
        var queues = new QueueFamilyProperties[queueCount];
        fixed (QueueFamilyProperties* p = queues) vk.GetPhysicalDeviceQueueFamilyProperties(physical, &queueCount, p);
        var family = Array.FindIndex(queues, q => (q.QueueFlags & (QueueFlags.ComputeBit | QueueFlags.GraphicsBit)) == (QueueFlags.ComputeBit | QueueFlags.GraphicsBit));
        if (family < 0) throw new NotSupportedException("No graphics+compute queue family");
        float priority = 1;
        var queue = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = (uint)family, QueueCount = 1, PQueuePriorities = &priority };
        var pointers = extensions.Select(s => SilkMarshal.StringToPtr(s)).ToArray();
        try
        {
            fixed (nint* p = pointers)
            {
                var deviceCreate = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, PNext = &features,
                    QueueCreateInfoCount = 1, PQueueCreateInfos = &queue, EnabledExtensionCount = (uint)pointers.Length, PpEnabledExtensionNames = (byte**)p };
                Check(vk.CreateDevice(physical, &deviceCreate, null, out device), "vkCreateDevice");
            }
        }
        finally { foreach (var p in pointers) SilkMarshal.Free(p); }
        Description = JsonSerializer.Serialize(new
        {
            Device = SilkMarshal.PtrToString((nint)properties.DeviceName), properties.DriverVersion, properties.ApiVersion,
            Extensions = extensions, ExactFloat16 = exactFloat16, ShaderInt8 = false,
            SubgroupSize = 32, RuntimeImages = runtimeCapacity.Images, RuntimeSamplers = runtimeCapacity.Samplers,
            maxPushDescriptors, RobustBufferAccess = (bool)supported.RobustBufferAccess,
            RobustBufferAccess2 = (bool)robustness.RobustBufferAccess2, RobustImageAccess2 = (bool)robustness.RobustImageAccess2,
            SharedInt64Atomics = (bool)v12.ShaderSharedInt64Atomics, ImageInt64Atomics = (bool)atomic.ShaderImageInt64Atomics
        }, SpirvDump.Json);
    }

    public double Compile((Blob Metadata, byte[] Bytes)[] shaders, uint pushBytes)
    {
        var reflected = shaders.Select(s => SpirvReflection.Reflect(s.Bytes)).ToArray();
        if (pushBytes > maxPushConstants) throw new NotSupportedException("Push constants exceed device limit");
        var bindings = new Dictionary<(uint Set, uint Binding), NativeBinding>();
        var visibility = new Dictionary<(uint Set, uint Binding), ShaderStageFlags>();
        for (var i = 0; i < shaders.Length; i++)
        {
            foreach (var b in shaders[i].Metadata.Bindings)
            {
                Merge(b);
                visibility[(b.Set, b.Binding)] = visibility.GetValueOrDefault((b.Set, b.Binding)) | reflected[i].Stage;
            }
        }
        foreach (var r in reflected)
            foreach (var b in r.Bindings)
            {
                Merge(b);
                visibility[(b.Set, b.Binding)] = visibility.GetValueOrDefault((b.Set, b.Binding)) | r.Stage;
            }
        void Merge(NativeBinding b)
        {
            if (bindings.TryGetValue((b.Set, b.Binding), out var existing))
            {
                if (existing.Type != b.Type) throw new InvalidDataException("Conflicting descriptor types");
                b = b with { Count = Math.Max(existing.Count, b.Count), Runtime = existing.Runtime || b.Runtime };
            }
            bindings[(b.Set, b.Binding)] = b;
        }
        // Reproduce complete presenter heap layouts, including unused heap bindings.
        if (bindings.Keys.Any(k => k.Set == 1))
        {
            Merge(new(1, RuntimeDescriptorTable.HeapSampledImageBinding, DescriptorType.SampledImage, runtimeCapacity.Images));
            Merge(new(1, RuntimeDescriptorTable.HeapSamplerBinding, DescriptorType.Sampler, runtimeCapacity.Samplers));
        }
        if (bindings.Keys.Any(k => k.Set == 2))
            for (uint i = 0; i < BindlessHeap.ArrayCount; i++)
                Merge(new(2, i, i < BindlessHeap.ImageKinds.Length ? DescriptorType.SampledImage : DescriptorType.Sampler, BindlessHeap.Capacity));
        if (bindings.Keys.Any(k => k.Set > 2)) throw new NotSupportedException("Unexpected descriptor set");
        var sets = new DescriptorSetLayout[bindings.Count == 0 ? 1 : checked((int)bindings.Keys.Max(k => k.Set) + 1)];
        PipelineLayout layout = default;
        var modules = new ShaderModule[shaders.Length];
        try
        {
            for (uint set = 0; set < sets.Length; set++)
            {
                var entries = bindings.Values.Where(b => b.Set == set).OrderBy(b => b.Binding).ToArray();
                var heapStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit |
                    (set == 2 && meshEnabled ? ShaderStageFlags.MeshBitExt : 0);
                var native = entries.Select(b => new DescriptorSetLayoutBinding(b.Binding, b.Type, b.Count,
                    set == 0 ? visibility[(set, b.Binding)] : heapStages)).ToArray();
                if (entries.Any(b => b.Runtime && b.Set == 0)) throw new NotSupportedException("Unbounded set-0 descriptor");
                var flags = native.Select(_ => DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.UpdateUnusedWhilePendingBit).ToArray();
                fixed (DescriptorSetLayoutBinding* p = native)
                fixed (DescriptorBindingFlags* f = flags)
                {
                    var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = (uint)native.Length, PBindingFlags = f };
                    var create = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
                        PNext = set > 0 && native.Length > 0 ? &bindingFlags : null,
                        Flags = set > 0 && native.Length > 0 ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit :
                            set == 0 && native.Sum(b => (long)b.DescriptorCount) <= maxPushDescriptors ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0,
                        BindingCount = (uint)native.Length, PBindings = p };
                    Check(vk.CreateDescriptorSetLayout(device, &create, null, out sets[set]), "vkCreateDescriptorSetLayout");
                }
            }
            var stages = reflected.Aggregate((ShaderStageFlags)0, (s, r) => s | r.Stage);
            var push = new PushConstantRange(stages, 0, pushBytes);
            fixed (DescriptorSetLayout* p = sets)
            {
                var create = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)sets.Length, PSetLayouts = p, PushConstantRangeCount = pushBytes == 0 ? 0u : 1u, PPushConstantRanges = &push };
                Check(vk.CreatePipelineLayout(device, &create, null, out layout), "vkCreatePipelineLayout");
            }
            var stageInfos = new PipelineShaderStageCreateInfo[shaders.Length];
            var names = new nint[shaders.Length];
            try
            {
                var subgroup = new PipelineShaderStageRequiredSubgroupSizeCreateInfo { SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo, RequiredSubgroupSize = shaders[0].Metadata.SubgroupSize };
                for (var i = 0; i < shaders.Length; i++)
                {
                    fixed (byte* bytes = shaders[i].Bytes)
                    {
                        var create = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)shaders[i].Bytes.Length, PCode = (uint*)bytes };
                        Check(vk.CreateShaderModule(device, &create, null, out modules[i]), "vkCreateShaderModule");
                    }
                    names[i] = SilkMarshal.StringToPtr(reflected[i].EntryPoint);
                    stageInfos[i] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = reflected[i].Stage, Module = modules[i], PName = (byte*)names[i],
                        PNext = reflected[i].Stage == ShaderStageFlags.ComputeBit && subgroup.RequiredSubgroupSize != 0 ? &subgroup : null };
                }
                Pipeline pipeline = default;
                Result result;
                double elapsed;
                if (stageInfos.Length == 1 && stageInfos[0].Stage == ShaderStageFlags.ComputeBit)
                {
                    var create = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Layout = layout, Stage = stageInfos[0] };
                    var started = Stopwatch.GetTimestamp();
                    result = vk.CreateComputePipelines(device, default, 1, &create, null, out pipeline);
                    elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                else (result, pipeline, elapsed) = CompileGraphics(stageInfos, reflected, layout);
                if (pipeline.Handle != 0) vk.DestroyPipeline(device, pipeline, null);
                Check(result, "vkCreate*Pipelines (VkPipelineCache = VK_NULL_HANDLE)");
                return elapsed;
            }
            finally { foreach (var name in names) if (name != 0) SilkMarshal.Free(name); }
        }
        finally
        {
            foreach (var module in modules) if (module.Handle != 0) vk.DestroyShaderModule(device, module, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(device, layout, null);
            foreach (var set in sets) if (set.Handle != 0) vk.DestroyDescriptorSetLayout(device, set, null);
        }
    }

    private (Result, Pipeline, double) CompileGraphics(PipelineShaderStageCreateInfo[] stages, SpirvReflection[] reflection, PipelineLayout layout)
    {
        if (stages[0].Stage != ShaderStageFlags.VertexBit || stages.Skip(1).Any(s => s.Stage != ShaderStageFlags.FragmentBit))
            throw new NotSupportedException("Minimal graphics benchmark supports vertex + optional pixel only; mesh/tessellation excluded");
        var attributes = reflection[0].Inputs.OrderBy(p => p.Key).Select((p, i) => new VertexInputAttributeDescription(p.Key, (uint)i, p.Value, 0)).ToArray();
        var bindings = attributes.Select(a => new VertexInputBindingDescription(a.Binding, 16, VertexInputRate.Vertex)).ToArray();
        var outputs = reflection.LastOrDefault(r => r.Stage == ShaderStageFlags.FragmentBit)?.Outputs ?? [];
        var formats = Enumerable.Range(0, outputs.Count == 0 ? 0 : checked((int)outputs.Keys.Max() + 1))
            .Select(i => outputs.GetValueOrDefault((uint)i, Format.Undefined)).ToArray();
        if (formats.Length > 8) throw new NotSupportedException("More than eight color attachments");
        var blends = formats.Select(_ => new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit }).ToArray();
        fixed (VertexInputAttributeDescription* a = attributes)
        fixed (VertexInputBindingDescription* b = bindings)
        fixed (PipelineShaderStageCreateInfo* s = stages)
        fixed (Format* f = formats)
        fixed (PipelineColorBlendAttachmentState* c = blends)
        {
            var vertex = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo, VertexAttributeDescriptionCount = (uint)attributes.Length, PVertexAttributeDescriptions = a, VertexBindingDescriptionCount = (uint)bindings.Length, PVertexBindingDescriptions = b };
            var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewport = new Viewport(0, 0, 1, 1, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1));
            var view = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, PViewports = &viewport, ScissorCount = 1, PScissors = &scissor };
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1 };
            var sample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = (uint)blends.Length, PAttachments = c };
            var depth = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, MaxDepthBounds = 1 };
            var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = (uint)formats.Length, PColorAttachmentFormats = f };
            var create = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, PNext = &rendering,
                StageCount = (uint)stages.Length, PStages = s, PVertexInputState = &vertex, PInputAssemblyState = &assembly,
                PViewportState = &view, PRasterizationState = &raster, PMultisampleState = &sample, PColorBlendState = &blend, PDepthStencilState = &depth, Layout = layout };
            var started = Stopwatch.GetTimestamp();
            var result = vk.CreateGraphicsPipelines(device, default, 1, &create, null, out var pipeline);
            return (result, pipeline, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static void Check(Result result, string call)
    { if (result != Result.Success) throw new InvalidOperationException($"{call}: {result}"); }

    public void Dispose()
    {
        if (device.Handle != 0) { vk.DestroyDevice(device, null); device = default; }
        if (instance.Handle != 0) { vk.DestroyInstance(instance, null); instance = default; }
        vk.Dispose();
    }
}
