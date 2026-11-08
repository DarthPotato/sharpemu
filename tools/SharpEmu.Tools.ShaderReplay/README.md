# Offline shader replay

## Optimizer investigation

The builder optimizer defaults off. Set `SHARPEMU_SPIRV_OPTIMIZE=1` explicitly
for an optimized replay. `manifest.json` records the full process-constant
`SpirvOptimizationPolicy.CacheIdentity`, including each pass switch. Each replay
mode needs a fresh process. See `docs/sharpemu-gui-undocumented-env-vars.md` for
`SHARPEMU_SPIRV_OPT_FORWARD`, `STORE_ELISION`, `UNREAD`, `DCE`, `LANE_MASK`,
`HOIST`, `INHERIT` and `EXPRESSIONS` (all with the same prefix).

For the current corpus, build with `Build-Isolated.ps1 -Version 0.0.7-pulse`.
The older frozen corpus uses `0.0.6-mono`. The build script checks that no
emulator runs from this worktree's artifacts directory before building.

Validate every stage in one or more dump directories:

```powershell
./tools/SharpEmu.Tools.ShaderReplay/Validate-Spirv.ps1 `
  -DumpRoot .tmp/miscompile/dumps-after `
  -OutputPrefix .tmp/miscompile/validation `
  -Validator C:/VulkanSDK/1.4.363.0/Bin/spirv-val.exe
```

The default target is Vulkan 1.3, matching the presenter's requested API and
the emitted SPIR-V 1.5. It checks each blob against its manifest SHA-256 and
writes every result to CSV plus complete failure diagnostics to JSON.
`-Modes forward` can select named subdirectories; the default includes all.

Compare outputs with the bounded CPU interpreter:

```powershell
dotnet <isolated-output>/SharpEmu.Tools.ShaderReplay.dll --semantic-check `
  <unoptimized-dump> <optimized-dump> .tmp/semantic-check 16 131072
```

The final arguments are random input count (default 16) and maximum module
bytes (default 131072). Stage sets and host profiles must match. The interpreter
models private/function/output storage, numeric operations, branches, loops,
simultaneous phis, bitcasts, a sparse synthetic physical heap and shaderData
R32Uint texel buffers. Subgroups execute 32 lanes; collectives require identical
control traces across all lanes. Barycentric interpolation uses a fully covered
synthetic triangle, equal clip w and one center sample. No GPU work is submitted.

Textures, derivatives, shared writes, partial subgroup participation, unsupported
instructions and observed undefined values skip a stage with an explicit reason.
An unselected undefined `OpSelect` operand is safe. Larger modules skip at the
byte limit. JSON retains every stage and any mismatch seed/output; Markdown
summarizes coverage. Compute stages without observable writes only compare
termination/control, so those checks cannot establish memory-output equivalence.
Passing samples do not establish equivalence for other inputs, helper lanes,
different coverage/sample patterns, unmodeled resources or driver execution.

## Translation replay

Build this project in Release, then run the generated executable with two arguments:

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet artifacts/bin/Release/net10.0/SharpEmu.Tools.ShaderReplay.dll <recording-directory> <output-prefix>
```

The directory must contain `shader-prewarm.bin` and `shader-prewarm-graphics.bin`.
The tool copies them to a unique temporary directory before opening the runtime
loader, which may truncate an incomplete record. It also copies linked
`vulkan-pipeline-cache.bin.linked/callees.bin` when present and redirects the linked
callee store to the isolated directory. Offline replay never reads the game dump
or creates a Vulkan device, window, pipeline, or shader module. `--native-bench`
explicitly creates Vulkan objects, without a presenter or window, as described below.

Requests come from the runtime prewarm planner. The capture backend returns dummy
module payloads and retains the final `ShaderCompileRequest`; each request is then
translated directly, on one thread. Three untimed compilations warm up each stage.
Only the direct translator call is timed (including serialization and any retry).
Request planning, CFG counting and SHA-256 hashing are outside that interval.
Use the same .NET environment, input files and machine for comparisons. Allocation
counts include allocations performed by that translator call.

Prewarm schema fingerprints include assembly versions in generic property type
names. When replaying recordings from before a version bump, build this tool and
its dependencies with the recording's version, for example
`-p:Version=0.0.6-mono` for the November 7 pre-merge corpus. This changes assembly
metadata, not translator source or emitted SPIR-V. Use a separate `BaseOutputPath`
when keeping several compiler revisions for comparison.

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
control independence with `shaderFloat16`, so the presenter enables exact float16
conversion. `LoadComputeDeviceLimits` prints its startup diagnostic before
`CreateDevice` assigns that flag; the early `enabled=False` line does not report
the final host capability. Update the profile when comparing another device or
presenter policy.

Build safely beside a running emulator (all outputs **and intermediate files**
go under `.tmp/native-build`; nothing writes the repository's `artifacts`):

```powershell
./tools/SharpEmu.Tools.ShaderReplay/Build-Isolated.ps1
$replay = '.tmp/native-build/artifacts/bin/Release/net10.0/SharpEmu.Tools.ShaderReplay.dll'
```

`Build-Isolated.ps1` uses one MSBuild worker and disables shared compilation. Its
`-Version` defaults to `0.0.6-mono` for this recording corpus. It also accepts
`-OutputRoot`, `-PackageRoot` (to share an existing NuGet cache in a separate tree),
and `-Project` (for the standalone tests).

Dump final translated bytes and a `manifest.json` with request-derived set-0
descriptor types/counts, required compute subgroup size, push constants, stage
identity, SHA-256, translator assembly SHA-256, host profile, and graphics pairs:

```powershell
dotnet $replay <recording-directory> .tmp/replay-defaults --dump-spirv .tmp/dumps/defaults
$env:SHARPEMU_SPIRV_OPTIMIZE = '0'
dotnet $replay <recording-directory> .tmp/replay-optimize0 --dump-spirv .tmp/dumps/optimize0
Remove-Item Env:SHARPEMU_SPIRV_OPTIMIZE
```

Translate a pre-merge comparison by exporting/checking out `9dd52c00`, copying
the **entire current ShaderReplay tool directory** over its old tool directory,
then building with that tree's `Build-Isolated.ps1`. Copy no translator/runtime
source files. The new tool was built successfully against both revisions. Use
the same frozen recordings, assembly version, selection, and host settings.

Both dump and native modes accept `--hash HEX[,HEX...]` (all specialized variants
of each hash), or `--csv replay.csv --top N --rank spirv_bytes|translation_ms`
(exact stage IDs; default rank is size). Combining them takes their union. For
graphics selection, dumps retain the companion stage and native timings compile
the recorded vertex/pixel pair together. Native mode defaults to the seven slow
compute hashes from the November 7 loading-screen investigation. Explicit
selection replaces this default; include the seven with `--hash` if also using
`--top`. The original corpus has 58 variants of those seven hashes.

Run the comparison only after the game exits:

```powershell
dotnet $replay --native-bench .tmp/native-ab --repeat 2 `
  --mode defaults=.tmp/dumps/defaults `
  --mode optimize0=.tmp/dumps/optimize0 `
  --mode premerge=.tmp/dumps/premerge
```

`--validate-only` performs selection, SHA checks, SPIR-V structure/reflection and
cross-mode variant matching, writing `<prefix>.plan.json` without Vulkan. This
does not run `spirv-val` or create native layouts. Without it, parent and worker
refuse to run while any `SharpEmu.exe` process exists. `--smoke` restricts a native
run to one mode, two selected compute shaders <=64 KiB each, and two repeats.

The native device explicitly selects NVIDIA RTX 3080, queries capabilities, and
enables the presenter's shader-affecting core/extension features, including exact
float16, shared/image int64 atomics, BDA, subgroup32, descriptor indexing,
robustness2, barycentrics and mesh support. Shader int8 is **disabled**, matching
the presenter. Runtime image/sampler and call-record heaps use presenter counts,
flags and stage visibility, without allocating descriptor pools or resources.
Set-0 metadata is merged with reflection of each blob, independently per mode.

Each measured observation launches a fresh process/device. A GUID is inserted
as a legal `OpSourceExtension` before debug/annotation/type declarations; no IDs
are allocated and the SPIR-V bound is unchanged. The first compile with that
nonce is followed by a second process compiling the **same** bytes. Every repeat
uses a new nonce. Mode order rotates across variants and reverses on alternate
repeats. `VkPipelineCache` is always null. Only `vkCreate*Pipelines` is timed;
process/device/module/layout/reflection overhead is excluded. No commands run.

**Different bytes do not guarantee that a driver uses a different cache key.**
A driver can strip debug instructions before hashing. The summary requires every
fresh repeat to take >=10 ms and >=2x its corresponding same-nonce compile to
label the probe as supporting a cold/hit distinction. Otherwise it says
`INCONCLUSIVE; do not claim cold`. This is timing evidence, not an NVIDIA cache
miss counter. Small shaders often cannot distinguish cold/hit overhead. Do not
interpret inconclusive data as a cold A/B comparison; inspect large-shader probes.
The tool never clears or changes the machine's driver cache.

Outputs: `<prefix>.csv` retains each nonce, repeat, phase, original SPIR-V bytes
and driver-call milliseconds; `<prefix>.md` contains per-variant medians and
per-mode totals/medians; `<prefix>.device.json` records device/driver/features.
Worker JSON files remain beside the outputs for audit. Mode names use letters,
digits, underscore or dash. Input errors or Vulkan failures stop the run with a
partial CSV rather than producing a successful comparison of unequal selections.

Graphics uses recorded vertex/pixel pairings and a minimal compatible dynamic
rendering pipeline: triangle list, one sample, numeric vertex/output formats,
and disabled depth/blend. It is not a recreation of game render states. Mesh and
tessellation pipelines, matrices/arrays at vertex attributes and unusual graphics
interfaces are excluded or rejected explicitly. Graphics was not GPU-tested.

CPU-only tests, including optional Vulkan SDK `spirv-val` validation:

```powershell
./tools/SharpEmu.Tools.ShaderReplay/Build-Isolated.ps1 `
  -Project tests/SharpEmu.Tools.ShaderReplay.Tests/SharpEmu.Tools.ShaderReplay.Tests.csproj
dotnet vstest .tmp/native-build/artifacts/bin/Release/net10.0/SharpEmu.Tools.ShaderReplay.Tests.dll
```
