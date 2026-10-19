# Brief 4: tessellation draws are 60-100x slower on the port

Rules of `docs/cloud/YOTEI_PORT_BRIEF.md` apply (root-cause fixes only, no env switches/flags/workarounds, keep the
upstream compiler, small commits with tests, games that work upstream keep working, commits authored
`foufouadi <attar.foued@gmail.com>`, never mention Claude/Anthropic, no pull request, push only `cloud/*`).
Report 3's two branches (resident mips, working-set eviction) are merged and validated in game: image
creations 153 267 -> 11 277 per run, village 0.1 -> 0.5-1.1 fps.

## Measurement (RTX 5070, same save, `SHARPEMU_PROFILE_GPU_DRAWS=1`, per-draw GPU timestamps)

Same Ghost of Yōtei tessellated draw (vertex/merged hull hash `0xAD291CED9B8A8CE8`, pixel `0x22025DD20C4A8141`,
topology PatchList, ~6 300-6 800 patches per draw, one instance, color target R16G16B16A16Sfloat):
- `yotei-good`: **4-18 ms per draw** (`[PERF][GPU_INTERVAL] kind=Draw pipeline=1396 count=7 total_ms=59.693 max_ms=8.900 max_args=6680,1,0`).
- port (`yotei-port-probe`): **700-1000 ms per draw** (`kind=Draw pipeline=1337 count=3 total_ms=2932.873 avg_ms=977.624 max_ms=995.701`).
Three of them in one submission exceed the Windows GPU timeout: the run ends in `vkQueueSubmit2 ErrorDeviceLost`
(longest submission 9.2 s). A depth-only DrawIndexed (vertex `0x9F1F009365AFC874`) also reaches 867 ms on the port.

## What to find

Tessellation was ported a few days ago (commits `5980fddd`..`29aabae3` on `yotei-port-probe`): compiler data/
lowering copied from yotei-good, but the SPIR-V side re-expressed on the upstream translator (cooperative waves in
`EmulatedWave64Lowering`, `_executingWave` guards in `StoreS`/`StoreV`/`Store`, `TryEmitCooperativeDispatcher`,
`Gen5SpirvTranslator.Tessellation.cs`, `Gen5TessellationBridge.cs`). Compare what both translators emit for a
merged LS/HS hull and the tess-eval stage (`yotei-good` is in this repo): control flow (dispatcher vs structured),
barriers, LDS exchange per instruction, per-lane loops, device-address accesses, how many invocations run per
patch, output control-point handling. Find why the port's version is two orders of magnitude slower and fix it.
Also look at the depth-only 867 ms draw if the cause is shared.

Constraints:
- Tessellation code paths are only taken by tessellated draws (merged LS/HS + tessellator); the SPIR-V of any
  non-tessellated shader must stay byte-identical (add a test that proves it for a representative set, e.g. the
  existing translator test programs).
- Keep all tessellation tests green (`Gen5TessellationTests`, `TessellationWaveDeviceTests`,
  `RenderHostDeviceTests.Tessellation`), lavapipe runs device tests.
- If you can build the same hull/domain test program with both translators (yotei-good's and the port's) and
  compare instruction counts / structure, do it and put the numbers in the report.

Deliverable: `docs/cloud/RAPPORT_4.md` (French) on `cloud/report-4`, fix branches `cloud/<topic>` with tests,
expected effect per fix, and what the owner must check in game (per-draw GPU time of pipeline vs `0xAD291CED...`
with `SHARPEMU_PROFILE_GPU_DRAWS=1`, no ErrorDeviceLost, tessellated geometry visually unchanged).
Logs: docs/cloud/data/yotei-bisect-gpudraws.log.gz (port), yotei-bisect-good-gpudraws.log.gz (yotei-good), both with SHARPEMU_PROFILE_GPU_DRAWS=1.
