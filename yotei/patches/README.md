# Ghost of Yōtei performance patches

**These patches now live in [DarthPotato/yotei-patches](https://github.com/DarthPotato/yotei-patches)**, together with the findings. The copies here are kept so older links keep working.

All files are for game version **02.002.000** (PPSA26344). They change the game's code in memory or at relink time, never the game files on disk. Enable only one ladder patch at a time.

| File | For | What it does |
|---|---|---|
| `GhostOfYotei.sharpemu.xml` | SharpEmu (`user/patches/`, PS-Game-Patch format) | **Performance mode** (ladder row 15), **Balanced mode** (row 11), **Output at 1080p** |
| `anyps5-yotei-02.002-ladder15.toml` | AnyPS5 relinker (`--patches`) | Quality ladder row 15 |
| `anyps5-yotei-02.002-ladder11.toml` | AnyPS5 relinker | Quality ladder row 11 |
| `anyps5-yotei-02.002-1080p-ladder15.toml` | AnyPS5 relinker | 1080p render and output (shahulmubeen-a's 02.002 patch) plus ladder row 15 |

**The quality ladder.** The game keeps one integer, the row of its own scalability table: row 0 is full quality, row 15 is LOD 0.1 with three fewer sun shadow views and small shadow casters culled, row 11 is LOD 0.2 with two fewer sun shadow views. The game also writes the row while it runs, so the patches change all 17 instructions that read it (`movsxd r64, [rip+row]`) into `mov r32, <row>; nop`. The game then sends about 45% fewer draws and dispatches at row 15.

**Measured** on a Ryzen 5 3600 + RTX 3080, open world, same save:
- SharpEmu main: 3.3 fps without, 9.2 fps with Performance mode.
- AnyKyty `yotei/best`, 1080p output: 15.6 fps at row 0, 23.8 fps at row 15.

**Output at 1080p (SharpEmu).** On SharpEmu main this crashes when the world loads ("The image view is invalid: imageFormat=130 viewFormat=122"); this fork fixes that in commit 50bf8df9. It is therefore not part of the ps5patches submission.
