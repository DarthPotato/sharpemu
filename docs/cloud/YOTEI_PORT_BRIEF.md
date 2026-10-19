# Brief for an audit/improvement session: SharpEmu Ghost of Yōtei port

You are working on SharpEmu (C#/.NET 10, PS5 emulator, `SharpEmu.slnx`, read `AGENTS.md` first).
This repository's default branch is `yotei-port-probe`: upstream SharpEmu (`sharpemu/sharpemu`, merged
up to its #1061) plus our port of the fixes that make Ghost of Yōtei render. The goal is a better emulator
overall: **every game that works upstream must keep working**, and Ghost of Yōtei must render correctly
and run faster.

You cannot run the game (no game files here; never add firmware, keys or game assets) and probably have
no NVIDIA GPU, so GPU device tests may skip. The owner validates in game on an RTX 5070. Your job is the
analysis and the code that a CPU-side test can check, plus precise instructions for what must be measured
on the GPU.

## Hard rules

- Root-cause fixes only. **No environment switches, no feature flags, no workarounds, no fabricated
  success values, no game-specific hacks.** Behaviour may depend on real device capabilities (Vulkan
  features/properties), never on a toggle.
- Keep the upstream shader compiler (`src/SharpEmu.ShaderCompiler*`) and its structure. Do not bring back
  the rewritten compiler of branch `yotei-good` (structured-CF rewrite, register model, paired lanes
  internals). Port ideas, not files.
- Small, separate commits, each with a test, each explainable in a few lines (upstream rejected a 30k-line
  PR; we land small root-cause PRs). One topic per branch: `cloud/<topic>`.
- English for code, commits and comments; the report (see Deliverable) in French.
- Commits are authored by the owner: run `git config user.name "foufouadi"` and `git config user.email "attar.foued@gmail.com"` first. No Co-Authored-By trailer, no mention of Claude, Anthropic or "Generated with Claude Code" anywhere, and never open a pull request (push `cloud/*` branches only).

## History you need

- `yotei-good` (= upstream PR #1097 head 44b10094 + an upstream merge): our older branch with a rewritten
  compiler. It renders Yōtei at ~19 fps in the village intro (RTX 5070) but regressed other games
  (Demon's Souls, Astro Bot) and was rejected upstream. It also contains xnetcat/kuba's reverted work
  (upstream PR #1066, still open/unmerged): visibility points, main-queue readbacks, GPU-side indirect
  draws, render scale, etc.
- `yotei-port-probe` (this repo): upstream + targeted ports. Commits since upstream are the history of
  `81bb204b..HEAD` (and the merge of upstream #1061). Audits of what was ported and what was deliberately
  left out are in this branch's history and summarized below.

## Measured state (RTX 5070, emulated wave64 unless noted)

Rendering: the village intro renders (night scene, a few blocky stencil artifacts) once the slow phase is
reached (~8 min after launch). The ~10 fps phase before it is a black load/fade in every build.

Compile cost (fixed already, keep):
- `06fc0a43` runtime-format device buffer loads go through one `DontInline` function (they were a
  per-access 4-iteration generic-format loop: 50CD 38.5 s -> 9.2 s). `DontInline` is forbidden for lookup
  functions whose result indexes descriptors (NVIDIA miscompile), allowed for loaded data.
- `d8b9e512` f16<->f32 conversions are native `OpFConvert` when the device guarantees exact f16 rounding
  (shaderFloat16 + RTE16 + DenormPreserve16 + independence); integer emulation otherwise.
  `7bf02fad` fixed subnormal ties in that integer emulation. 50CD now 4.1 s (yotei-good's compiler: 3.2 s).
- Paired wave64 lowering (upstream default on 32-lane hosts) duplicates the body: 50CD 11.3 s vs 4.1 s,
  prewarm 571 s vs 111 s, and **no runtime gain measured** (same fps). Deciding the lowering from
  measured cost is open.

Runtime, village phase: port 0.1 fps vs yotei-good ~19 fps.
- Draws per frame: port ~10 000, yotei-good ~450. Not explained yet. Suspect how GPU-driven multi-draw
  indirect is executed (yotei-good resolved non-indexed/indexed indirect counts on the GPU; check the
  port's `DrawIndirect*`/`DrawIndexMultiInstanced`/indirect paths and whether counts are read on the CPU
  and expanded into one host draw each).
- CPU waits on GPU (sum over the phase, port vs good):
  - `GuestBufferCache.TrySynchronizeCpuRead` -> download: 59 s / 1450 waits (~40 ms each) vs 33 s / 4168 (~8 ms).
  - `GuestImageCache.AwaitPublishingDownloads` (texture refresh): 26 s vs 43 s.
  - `Presenter.RunPendingOperations` inside `RenderExecutor.Dispatch` (full GPU finish): 14 s vs 0.6 s.
  - `GuestImageCache.InsertImage` waiting for retired images: 13 s / 906 waits vs ~0.
- Image churn: every frame the same 3840x2160 surface is used as depth D32Sfloat and as color R32Sfloat;
  `ResolveDepthOverlap` deletes one and creates the other (`depth-overlap-replace`, ~2 per frame, 33 MB
  each, copy included), plus the stencil plane R8Uint 4K is released/re-associated/reloaded every frame
  (`depth-association`, `stencil-plane-release`). yotei-good has the same overlap code; in the port the
  retired bytes push the P22 memory budget (`RetirementOverBudget` -> `Finish`) into full GPU drains.
  (`SHARPEMU_IMAGE_CHURN_TRACE=1` gives per-reason counts.)
- Graphics pipelines: ~87 created per frame in steady state (yotei-good: 0); the same vertex/pixel shader
  pairs are recreated 20-40 times. `GraphicsPipelineKey` contains `VertexProgramId`/`PixelProgramId`
  (program instance ids), so a new program permutation with identical SPIR-V creates new pipelines.
- GPU-bound share is now small in the slow phase (GpuCompletionWait 2-50 % depending on the window); the
  CPU translation side and the waits above dominate.

## Ideas from another emulator's Yōtei work (KytyPS5 fork, not public, reported results)

Check whether the same problems exist here; port the idea only if it applies:
- Async compute queue ordering: a queue that never waits (cloth simulation) was scheduled before a GPU
  readback the emulator was waiting on; running non-waiting queues after the waiting ones removed ~3.5 ms
  from the critical path.
- Submit to the GPU earlier whenever the GPU goes idle (cuts per-frame GPU wait).
- Main GPU translation thread was the bottleneck (~44 ms CPU per frame open world): make it cheaper and
  move per-draw preparation to helper threads; better prediction in a draw "scout" thread.
- Cache validated bindless texture sets instead of rebuilding them every draw (~+6 %).
- Cheaper number handling in heavy shaders (FSR3, cloth, volumetric fog) in the translator.
- Shader lists checked as numbers not text, write tracking only where needed, faster resource lookups.

Public references you may read (fetch from GitHub): `fxpw/KytyPS5` (PS5 fork of Kyty), `InoriRus/Kyty`,
`mattias800/prosper`, `shadps4-emu/shadPS4` (PS4, mature buffer/texture cache and GPU-driven paths).
Use them for clean-room reasoning; do not copy code with incompatible licences.

## What to do

1. Audit `git diff 81bb204b..HEAD` (our port on top of upstream) and the comparison with branch
   `yotei-good`, which is pushed to this repository (`git diff yotei-port-probe yotei-good`; diff
   removals too, not only additions: one regression came from an upstream rule that #926 had removed and
   the port silently restored). Look for: inconsistencies, dead or duplicated code from the port, leftover workarounds or
   env switches (list each `Environment.GetEnvironmentVariable` that changes behaviour rather than only
   logging), missing pieces whose absence explains the measurements above, and bugs.
2. For each measured problem above, find the root cause in code and propose the fix (with the exact
   functions involved). Rank by expected impact on Yōtei fps and by risk to other games.
3. Implement the fixes that are safe and CPU-testable, one branch each (`cloud/<topic>`), each with unit
   tests (`tests/SharpEmu.Libs.Tests`, `tests/SharpEmu.ShaderCompiler.Tests`). Build with the .NET 10 SDK
   pinned in `global.json`. Candidates, in order:
   a. Pipeline key by program content (stop recreating identical pipelines).
   b. D32 <-> R32F same-surface handling without delete/recreate each frame (check Vulkan aliasing rules,
      layouts, barriers; preserve newest contents; there is an existing same-layout alias mechanism
      `SynchronizeAliases`/`IsSameLayoutAlias` for color/color).
   c. Remove full GPU drains caused by retirement accounting (wait only for what the allocation needs).
   d. Stencil plane re-association every frame.
   e. Draw count explosion / indirect handling.
4. Do not push to any other repository. Push only your `cloud/<topic>` branches here.

## Deliverable

A report `docs/cloud/RAPPORT.md` (in French) on a branch `cloud/report`, containing:
- what you found (each with file:line, evidence and why it matters),
- what you changed (branch, commits, tests, expected effect),
- what you did not change and why,
- exactly what the owner must measure on the GPU/in game to validate each change (scene, counters:
  fps, draws/frame, `[PERF][RENDER]` phases, GPU_WAIT_SITE, IMAGE_CHURN, pipelines/window),
- a ranked list of next steps.
