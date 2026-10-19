# Brief 3: remove the bottleneck, not shave it

Rules from `docs/cloud/YOTEI_PORT_BRIEF.md` still apply (root-cause fixes only, no env switches/flags/workarounds,
keep the upstream compiler, small commits with tests, every game that works upstream keeps working, commits
authored `foufouadi <attar.foued@gmail.com>`, never mention Claude/Anthropic, no pull request, push only `cloud/*`
branches). Your reports 1 and 2 (`cloud/report`, `cloud/report-2`) are merged: all seven fix branches are in
`yotei-port-probe` (merges up to 1ca2821a).

**The owner does not want 5 % gains.** Ghost of Yōtei on the port runs the village intro at **0.1 fps**; the
reference branch `yotei-good` runs the same scene at **2-3 fps (20 fps in lighter shots)**, boots to an image in
~4 minutes and uses ~10 GB. Your job is to find why the port is ~20x slower and remove that bottleneck, with fixes
whose expected effect is an order of magnitude, each argued from the logs. Think as long as you need.

## Data (real logs, gzip, in `docs/cloud/data/`)

All on the owner's RTX 5070 (12 GB), Windows, same save, `-NoPaired` unless noted, with
`SHARPEMU_PROFILE_PERFORMANCE=1 SHARPEMU_PROFILE_RENDER=1 SHARPEMU_PROFILE_RENDER_REPORT_S=10`:
- `yotei-bisect-cloud7.log.gz` — **current port** (all 7 cloud fixes), default wave mode. Village: 0.1 fps,
  ~6 300 image creates/uploads per 10 s window, process 19 GB.
- `yotei-bisect-cloud3.log.gz` — port with report-1 fixes only. Ends in ErrorDeviceLost.
- `yotei-bisect-cmp-port.log.gz` — port before both reports (and before several translator fixes); ends in
  OutOfDeviceMemory.
- `yotei-bisect-cmp-good.log.gz` — **yotei-good** (reference), same profiling. ~19 fps in the 30 fps intro shots,
  later 0.5-1 fps windows then 17-20 fps.
- `yotei-bisect-churn.log.gz` — port with `SHARPEMU_IMAGE_CHURN_TRACE=1` (per-reason deletion counts, loading
  phase only, the summary is per 600 frames).

Useful lines: `[PERF][RENDER]` (10 s windows: fps and time per phase), `videoout submitted_fps ... draws= pipelines=`,
`[PERF][GPU_WAIT_SITE]` (where the CPU waits on the GPU, with call chain), `ImageMemory reason=` (live images,
allocated, gpu_modified, thresholds), `ImageMemoryPaths` (lookup/collection counters), `ImageLifetime`
(first 4096 image events: create/delete with reason), `Image cache budget` (budget and thresholds),
`Slow pipeline creation` (compile times).

In cloud7 the top CPU waits in the village are now:
1. `TryDownloadToGuest < CollectCore < InsertImage < LookUpImageCore` 20.4 s / 320 (allocation-time collection
   reading images back before deleting them),
2. `ReleaseRetiredImagesCore < RunPendingOperations < Dispatch` 19.3 s / 73,
3. `ReleaseRetiredImagesCore < FinishRetiredImagesForAllocation < InsertImage` 16.8 s / 2272,
4. `ReleaseRetiredImagesCore < InsertImage < LookUpImageCore` 11.8 s / 170.
So the churn moved from the mid-frame sweep (fixed) to the allocation-time collection at the critical level:
the image working set seems to stay above the critical threshold all the time. The thresholds are the same
code as yotei-good (P22). Hypothesis to confirm or kill: the port keeps more images alive for the same
surfaces than yotei-good (format aliases P13 `SynchronizeAliases`, dynamic-resolution siblings P25, per-size
stencil storage P24, D32/R32F depth/color recreation, stencil proxies, retired-but-not-released images,
GPU-modified images that cannot be evicted without readback), or counts memory differently
(`CollectionMemoryBytes`, `AccountedSize`, pool blocks), so it lives at the critical level and evicts the
working set on every allocation.

## Look elsewhere too

- `yotei-good` vs `yotei-port-probe` on everything the image/buffer caches keep alive and how memory is
  accounted (additions and removals).
- KytyPS5 (`fxpw/KytyPS5`, public): its author reports Yōtei open world at 17-19 fps on an RTX 5070 Ti and that
  the remaining bottleneck there is the CPU translation thread; earlier wins came from ordering async compute
  queues so a non-waiting queue (cloth) runs after queues the emulator waits on, submitting earlier when the
  GPU goes idle, caching validated bindless texture sets. Read how Kyty's texture/buffer cache handles
  depth/color aliasing, render-target reuse at one address with different formats, memory limits and eviction
  (it does not recreate its working set every frame).
- shadPS4 (`shadps4-emu/shadPS4`): texture cache (`video_core/texture_cache`), image aliasing/views over one
  allocation, buffer cache and memory tracker, how it avoids per-frame image recreation and full GPU drains.
- `mattias800/prosper` if useful.
Clean-room reasoning only (don't copy code with incompatible licences).

## Also still open (lower priority than the churn)
- Cold shader compile: prewarm 520 s in cloud7 (translator changes invalidate caches), graphics pipeline
  creates up to 49.7 s (`Slow pipeline creation: kind=graphics`). yotei-good boots in ~4 min.
- ErrorDeviceLost at the end of long runs (cloud3).
- Draw count: report 2 argued the per-second draw rate matches yotei-good; yotei-good skips non-default
  geometry/NGG draws the port runs as mesh shaders — check if that matters for cost.

## Deliverable
`docs/cloud/RAPPORT_3.md` (French) on `cloud/report-3`:
1. The bottleneck, proven from the logs (quote the lines, compute the numbers).
2. Why yotei-good / Kyty / shadPS4 do not have it (cite their code).
3. Fixes on `cloud/<topic>` branches with tests, each with its expected effect on fps / memory / waits and
   why it is that big. Prefer structural fixes (e.g. one image per surface with views, accounting that matches
   what is really alive, eviction that never evicts the current working set) over tuning.
4. What the owner must check in game, in one run, and what each counter should read if the fix works.
