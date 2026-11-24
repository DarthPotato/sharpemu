# Ghost of Yōtei (PPSA26344) emulation: findings

What this fork found while getting Ghost of Yōtei running faster, between October and November 2026. Everything was measured on a Ryzen 5 3600 + RTX 3080 (10 GB), Windows 11, standing in the open world on the same save, with warm shader caches, unless noted. It was made with AI assistance (Claude Code and Codex).

## 1. The game's own quality ladder

Yōtei has an internal scalability table. Each 20-byte row is {int, float LOD scale, int sun shadow view delta, float small shadow caster cull, int}, and there are 22 rows. One int holds the current row. Row 0 is full quality.

| Row | LOD scale | Sun shadow views | Small caster cull |
|---|---|---|---|
| 0 | 1.0 | +0 | 0 |
| 4 | 0.7 | −1 | 0 |
| 8 | 0.4 | −1 | 0 |
| 11 | 0.2 | −2 | 0 |
| 15 | 0.1 | −3 | 0.2 |
| 16–21 | 0.1 | −3 | 0.2, and the last int (8, 32, 128, 512, 768, 992) stops the player's body from being drawn |

Row 15 is the last row that only lowers detail. There the game sends about 45% fewer draws and dispatches.

| | 01.512.000 | 02.002.000 |
|---|---|---|
| Table | app+0x1F9BFC0 | app+0x20382E0 |
| Row int | app+0x4FE5B70 | app+0x509E1B0 |
| Static initializer (writes 0) | app+0xD64E1B | app+0xDE6E76 |
| Readers (`movsxd r64, [rip+row]`) | 16 | 17 |

`app+X` is the ELF virtual address in the unwrapped eboot; SharpEmu loads the image at 0x8_0000_0000. The game also writes the row at runtime through a pointer, for example row 14 over 15, so patching the initializer alone does nothing. The patches change all 17 readers to `mov r32, <row>; nop`. Patch files for SharpEmu and AnyPS5 are in [`patches/`](patches/).

Measured effect of row 15:

| Emulator | Full detail | Row 15 |
|---|---|---|
| SharpEmu main | 3.3 fps | 9.2 fps |
| This fork, 4K output | 6.8 fps | 12.7 fps |
| AnyKyty `yotei/best`, 1080p output | 15.6 fps | 23.8 fps |

## 2. Render and output size

The game renders at one of 16 sizes, (3840 − 128n) × (2160 − 72n), and its dynamic resolution settles at index 15 (1920×1080) in gameplay. It always upscales its output to 3840×2160.

| | 01.512.000 | 02.002.000 |
|---|---|---|
| Renderer object | app+0x4FDB4A0 | app+0x5093580 |
| Size table | app+0x1A785E0 | app+0x1AF8190 |
| Current index / override int | +0x327840 / +0x327848 | +0x32EE08 / +0x32EE10 |
| Override reader | app+0xD5E33C | app+0xDE01EC |

The 4K output size is a code constant. In 02.002 it appears as `movabs` at app+0xDDE5E5 and app+0xDE0271, and the size check is `mov edx, 0xF00` at app+0xDE020B plus `xor esi, 0x870` at app+0xDE021F; 01.512 used `cmp` there instead. shahulmubeen-a's 02.002 relinker patch for AnyPS5 sets all of these to 1080p.

1080p output doubles AnyKyty's frame rate (8.1 → 15.6 fps at full detail) but gives SharpEmu only +5–7%, because SharpEmu's frame is paced by its render thread's CPU work.

## 3. Benchmark: SharpEmu fork against AnyKyty (2026-11-24)

| Settings | This fork | AnyKyty `yotei/best` (4fb7613b70, PGO) |
|---|---|---|
| Full detail, render 1080p, output 4K | 6.8 fps | 8.1 fps |
| Full detail, render and output 1080p | 7.3 fps | 15.6 fps |
| Ladder row 15, render and output 1080p | 13.4 fps | **23.8 fps** |

Both emulators drew the same scene (AnyKyty: ~700 draws per frame at row 0, ~415 at row 15). AnyKyty used `KYTY_ASYNC_PIPELINES=0 KYTY_GPU_SPIN_US=0 KYTY_FALLBACKS=1`; 3–7 tessellation draws were skipped per run. A cold AnyKyty first run pushed committed memory close to 40 GB, so a growable page file is needed on a 24 GB machine.

## 4. SharpEmu-side findings (generic)

- **Wave64 pairing.** The cloth solver (compute 0xC2A548AAAE05D492) is one 1024-thread workgroup with a barrier per constraint step, and it took 7.75 ms a frame. Every frame paid that time in full: skipping the pass saved 7.0 ms. Running wave64 as two unpaired host warps (`SHARPEMU_WAVE64_PAIRED=0`) makes the halves meet at a rendezvous on every wave-mask branch (307 sites). `small` (pair only multi-wave workgroups, as upstream recommends) brought it to 4.25 ms and the game +7%.
- **Specialised buffer formats.** The translator decoded constant buffer formats at run time because its integer helpers didn't fold constants. Folding them cut the cloth shader's SPIR-V by 24% (6.76 → 5.12 MB) and its driver compile time by about 10%. GPU time was unchanged, because the driver had already folded it.
- **Stencil-plane proxy crash.** With 1080p output, a half-size B10G11R11 colour target shares memory with the stencil plane, and the image cache made it the plane's proxy. That caused "The image view is invalid: imageFormat=130 viewFormat=122". The fix is that only stencil-format images may stand in for the plane (commit 50bf8df9).
- **Where the render thread waits.** About 16 ms a frame goes to ~6 command-thread reads of GPU-written words: a COND_EXEC predicate plus descriptor-table words of direct and indirect dispatches. Each read flushes its writer's batch and waits for the newest writer anywhere in a 512 KB readback window. A narrower window would need sub-page dirty tracking: with page-granular tracking it crashed ("Cannot mark GPU-dirty pages as CPU-dirty"). AnyKyty's developers report the same page-granularity false alarms.
- **Fast 16-bit float.** The upscaler's accumulate pass went 11.9 → 3.5 ms with native f16 arithmetic. The cost is last-bit differences from the console's rounding.
- **Measured and dropped:** eager flip; readback batching; an early flush of hot readback writers (fired 170 times a frame, neutral); disabling eager readbacks (neutral); rendering below 1080p on this GPU (the CPU is the limit).

## Where the work is

- Fork and test builds: https://github.com/DarthPotato/sharpemu (branch `yotei3`, releases).
- AnyKyty Yōtei branch: https://github.com/Celegans12/AnyPS5-Ghost-of-Yotei (`yotei/best`), discussion https://github.com/boykopovar/AnyPS5/discussions/675.
- SharpEmu ladder patch: submitted to [sharpemu/ps5patches](https://github.com/sharpemu/ps5patches).
