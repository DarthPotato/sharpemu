# Offline shader replay

Build this project in Release, then run the generated executable with two arguments:

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet artifacts/bin/Release/net10.0/SharpEmu.Tools.ShaderReplay.dll <recording-directory> <output-prefix>
```

The directory must contain `shader-prewarm.bin` and `shader-prewarm-graphics.bin`.
The tool copies them to a unique temporary directory before opening the runtime
loader, which may truncate an incomplete record. It also copies linked
`vulkan-pipeline-cache.bin.linked/callees.bin` when present and redirects the linked
callee store to the isolated directory. It never reads the game dump or creates
a Vulkan device, window, pipeline, or shader module.

Requests come from the runtime prewarm planner. The capture backend returns dummy
module payloads and retains the final `ShaderCompileRequest`; each request is then
translated directly, on one thread. Three untimed compilations warm up each stage.
Only the direct translator call is timed (including serialization and any retry).
Request planning, CFG counting and SHA-256 hashing are outside that interval.
Use the same .NET environment, input files and machine for comparisons. Allocation
counts include allocations performed by that translator call.

CSV rows identify compute records by recording order and graphics stages by their
recorded identity; hashes alone do not identify specialized variants. Graphics
stages shared by several pipelines are compiled once. Failures are reported and
make the process exit unsuccessfully. The Markdown summary includes totals,
average/p95/max timings and the twenty slowest and largest stages.

`Rtx3080Host` is an explicit offline profile for this project's RTX 3080, checked
against `vulkaninfo` on 2026-11-07 and the current presenter policies:

- `VulkanVideoPresenter.Device.Setup.cs`: descriptor limits, float controls,
  mesh limits, clip distance, barycentrics, atomics and subgroup capabilities.
- `VulkanVideoPresenter.Pipelines.cs`: subgroup32/wave64 lowering, table blocks,
  exact float16 conversion, and NVIDIA's disabled EXEC guard elision.
- `VulkanVideoPresenter.BindlessHeap.cs`, `CallMarks.cs`, `LodStatistics.cs`:
  runtime call records, call marks and fragment subgroup reduction.

The device has subgroup size 32, RTE/denormal-preserving float16 and full float
control independence with `shaderFloat16`, so the **current** presenter enables
exact float16 conversion. Older run logs that reported it disabled predate this
profile. Update the profile when comparing another device or presenter policy.
