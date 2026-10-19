# Brief 2: find what the port is still missing (Ghost of Yōtei)

Read `AGENTS.md` and `docs/cloud/YOTEI_PORT_BRIEF.md` first (rules still apply: root-cause fixes only, no
env switches/flags/workarounds, keep the upstream compiler, small commits with tests, every game that works
upstream keeps working, commits authored `foufouadi <attar.foued@gmail.com>`, no mention of Claude/Anthropic,
no pull request, push only `cloud/*` branches). Your previous report is `docs/cloud/RAPPORT.md` on
`cloud/report`. Its three branches (pipeline depth bounds, retirement wait, GPU-read non-indexed indirect
draws) are now merged into `yotei-port-probe` (merge 218a2757).

Think thoroughly; this is a hunt for a missing fix, by code reading only (you cannot run the game).

## The gap that remains

The owner's reference build `yotei-good` (branch in this repo) boots to rendering in ~4 min, plays the intro
cinematic and in-game at 2-3 fps (20 fps in lighter scenes) with ~10 GB. The port, same machine (RTX 5070),
same save:

| | yotei-good | port (after your 3 fixes) |
|---|---|---|
| time to first visible frame | ~4 min | 8 min warm, 24 min after a pipeline-key change |
| village intro fps | 2-3 (20 in lighter shots) | 0.1 |
| images created + re-uploaded per 10 s window (village) | 100-600 | 4 000-7 800 (132 444 created over one run) |
| host draws per frame (`CountDraw`, includes dispatches/clears) | ~450 | ~10 000 |
| process memory | ~10 GB | 16-18 GB, growing |
| end of run | fine | vkQueueSubmit ErrorDeviceLost (twice, last recorded op a 1x1x1 DispatchDirect of shader 0x8000403900) |
| slowest graphics pipeline | — | 53 s (ps 0x40AFCE8B112936A0, 1.27 MB SPIR-V); 205 graphics compiles > 1 s |

Effect of your fixes measured in the village: pipelines created per window 87 -> 5-13; CPU-read waits 59 s/1450
-> 21 s/581; retirement waits unchanged in total (25 s, now `ReleaseRetiredImagesCore` from `InsertImage` and
`RunPendingOperations`) because the volume of retired images is unchanged. GPU_WAIT_SITE top in the village now:
1. `GuestBufferCache.TryDownloadAsync < ... < TrySynchronizeCpuRead` 20.9 s / 581
2. `ReleaseRetiredImagesCore < InsertImage < LookUpImageCore < ResolveImageBinding` 12.8 s / 688
3. `ReleaseRetiredImagesCore < RunPendingOperations < RenderExecutor.Dispatch` 11.9 s / 82
4. `AwaitPublishingDownloads < PopulateFromGuest < RefreshFromGuest < AcquireTextureView` 10.2 s / 527
5. `GpuRingBuffer.TryMap < ObtainBufferForImage < PopulateFromGuest` 7.4 s / 23

Known so far:
- In the loading phase the churn trace showed per 600 frames: `depth-overlap-replace` ~1200 (D32Sfloat <-> R32Sfloat
  3840x2160 at one address), `depth-association` + `stencil-plane-release` ~780 (R8Uint 4K stencil plane),
  `overlap-superseded`, `overlap-merged-into-mip`, `grow-replaced` small. The village volume (thousands per frame)
  is far larger and its reason is unknown.
- `GuestImageCache.Lookup.cs`: `LookupSlots` is 256 in the port, 4096 in yotei-good.
- `LookUpImageCore` / `ResolveOverlap` are nearly identical between the branches (except render scale), so the
  extra deletions likely come from a caller: GPU-write invalidation (`InvalidateMemoryFromGpu`,
  `InvalidateMemoryCopiesFromGpu`, `NoteDeviceAddressWrites`, P14 `PublishBeforeDrop`, P16 clean-read sync),
  collection (age in frames; at 0.1 fps many ticks per frame), stencil re-association, or the buffer cache
  marking pages dirty so images are refreshed.
- yotei-good contains xnetcat's work that the port excluded (upstream #1066, unmerged): visibility points /
  `NoteMemoryVisibilityPoint`, command-backing reads, main-queue readbacks, hot-path upload sinks, CPU write heat
  decay (`DecayCpuWriteHeat`), etc. Some of our own fixes in yotei-good were built on top of them. The port may
  miss one that prevents re-uploads (e.g. heat decay: "avoids permanent whole-ring copies").

## What to deliver

1. Find, by comparing `yotei-good` and `yotei-port-probe` (additions AND removals) on every path that deletes,
   invalidates, refreshes or re-uploads an image or buffer, the most likely cause(s) of: the image churn, the
   draw count gap, the memory growth, and the device loss. For each, file:line in both branches, the mechanism,
   and why it fits the numbers above.
2. Implement the safe fixes, one `cloud/<topic>` branch each, with unit tests (CPU-side image/buffer cache tests
   exist and lavapipe works for device tests). Include `LookupSlots` if justified.
3. For the 53 s graphics pipeline, inspect what the translator emits for big pixel shaders (no GPU timing here:
   reason from what made compute shaders slow: per-access generic decoders, integer f16 emulation — now native —,
   inlined helpers, loops) and fix what clearly applies.
4. Report `docs/cloud/RAPPORT_2.md` (French) on `cloud/report-2`: findings, branches, what to measure in game,
   ranked next steps, and for any hypothesis you could not confirm by reading, the exact one-line diagnostic the
   owner should enable (existing env traces: SHARPEMU_IMAGE_CHURN_TRACE, SHARPEMU_PROFILE_RENDER,
   SHARPEMU_PROFILE_PERFORMANCE) or add.
