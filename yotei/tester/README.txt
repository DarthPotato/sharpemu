Ghost of Yotei test build for SharpEmu (2026-11-22)
====================================================

This is an experimental build from a personal fork of SharpEmu, a PS5
emulator. It runs one game, Ghost of Yotei, at about 11-12 fps on the
machine it was made on, with rough graphics. It is not an official SharpEmu
release. Please send feedback to the person who gave you this build, not
to the SharpEmu project.

Build: https://github.com/DarthPotato/sharpemu, branch yotei3,
commit 5b83c559. Windows x64. Nothing to install.
Licence: GPL. This build includes a GPL-3.0-or-later component (the
package decompressor), so the program as distributed is GPL-3.0; see
LICENSE.txt and the licenses folder.

NEW IN THIS BUILD (compared with 2026-11-13)
- Game version 02.002.000 is supported, and so is 01.512.000. The two
  speed-ups (the game's quality ladder and the 1920x1080 hold) now work on
  both versions.
- Brought up to date with SharpEmu as of 2026-11-20. That includes
  SharpEmu's own Ghost of Yotei fixes (terrain), faster command
  processing, and a package installer (see 2b).
- About 13% faster than the 2026-11-13 build on the same machine.
- The bright patch near the player's feet is gone.
- Shaders are built again on the first start (this build changed them).


1. WHAT YOU NEED
----------------
- Windows 10 or 11, 64-bit.
- 32 GB of RAM (24 GB works with little else open). The emulator uses
  15-16 GB while the game runs.
- A graphics card with Vulkan support and a current driver. The game used
  about 9 GB of video memory on the test machine (RTX 3080 10 GB). Cards
  with less have not been tried.
- An SSD with room for the game folder (version 02.002.000 is about
  205 GB unpacked; 01.512.000 about 185 GB), plus a few GB for this
  build's shader cache.
- Your own dump of Ghost of Yotei, PPSA26344, version 02.002.000 or
  01.512.000. No game files are in this zip. Other versions start without
  the speed-ups and have not been tried.
- The game's eboot.bin must be decrypted. A decrypted eboot.bin starts
  with the bytes 4F 15 3D 1D; the encrypted retail one starts with
  54 14 F5 EE and cannot run in any emulator.


2. SET UP
---------
1. Before you unzip: right-click the zip, choose Properties, and tick
   "Unblock" if the box is there. This avoids Windows warnings later.
2. Unzip into a new, empty folder on an SSD, for example C:\yotei-test.
   Do not unzip over an older build, and do not use Program Files: the
   build writes its shader cache, saves and log next to SharpEmu.exe.
   If you ran an older build before, copy its user\savedata folder
   across. Saves from 01.512.000 load in 02.002.000.

2a. If your game is already an unpacked folder (eboot.bin and sce_sys
    directly inside it): put it anywhere on an SSD, for example
    D:\PPSA26344. Do not change, rename or remove anything inside it.

2b. If your game is a .pkg file (a fake or debug PS5 package, not a
    retail one), this build can unpack it. Open a terminal in the build's
    folder and run:
        SharpEmu.exe --install-pkg "D:\path\to\game.pkg" "D:\PPSA26344"
    It needs the full unpacked size free (about 205 GB for 02.002.000)
    on top of the package itself, and it takes a while. Only base game
    packages are supported. If the eboot.bin it unpacks is the encrypted
    one (see section 1), keep your decrypted eboot.bin and the files that
    came with it (fakelib, sce_module) together in their own folder and
    use the second way of starting the game in section 3.


3. START THE GAME
-----------------
Drag the game's eboot.bin onto run-yotei.bat.

Or from a terminal:
    run-yotei.bat "D:\PPSA26344\eboot.bin"

If your decrypted eboot.bin is in its own folder, apart from the game
files, give the game folder as a second argument:
    run-yotei.bat "D:\decrypted\eboot.bin" "D:\PPSA26344"
The game's libraries (fakelib, sce_module) are loaded from eboot.bin's
folder; everything else comes from the game folder. Nothing in either
folder is changed.

- If Windows shows "Windows protected your PC", choose More info, then
  Run anyway.
- On the very first start the game may show a notice about Digital
  Deluxe items. Press Cross (Z or Enter on the keyboard) to continue.
- The window title shows the game version and the frame rate (fps).
- To quit, close the window.

Optional, if you were also given a save folder: before the first start,
copy it to user\savedata\PPSA26344 next to SharpEmu.exe (create the
folders). The game then loads straight into the open world, and your
numbers can be compared directly with ours.


4. WHAT TO EXPECT
-----------------
The first session is slow. This is normal:
- Several minutes on a "Warming up shaders" screen or a black loading
  screen with a spinner before anything appears.
- Freezes whenever something new comes on screen. Each shader is built
  the first time it is needed, and some take the graphics driver 30-40
  seconds. Freezes of up to a minute have been seen.
- Built shaders are kept in user\pipeline_cache next to SharpEmu.exe.
  Do not delete that folder.

From the second session on:
- The "Warming up shaders" screen rebuilds everything seen before. It
  normally takes from seconds to a few minutes.
- The short warm-up relies on the graphics driver's own shader cache.
  After a graphics driver update, or if that cache is cleared (driver
  clean-up tools, "clear shader cache" options), the warm-up builds the
  large shaders again. That took 15-25 minutes on the RTX 3080. Let it
  finish; it is not a hang as long as the counter moves now and then.
- Places you have already seen run without the long freezes. This is the
  session that shows the real frame rate.

Frame rate on the machine this was made on (Ryzen 5 3600, RTX 3080),
standing still in the open world:
    version 02.002.000   about 11.7 fps
    version 01.512.000   about 12.3 fps
The CPU is the limit there, so a faster CPU should give more.


5. CONTROLS
-----------
A DualSense or Xbox controller is picked up automatically. Buttons map by
position (Xbox A = Cross, B = Circle, X = Square, Y = Triangle,
View = Create, Menu = Options).

The map is the touchpad press. On a DualSense, press the touchpad. An
Xbox controller has no touchpad: press T on the keyboard.

Keyboard:
    Arrow keys         D-pad
    W A S D            Left stick
    I J K L            Right stick
    Z or Enter         Cross
    X or Esc           Circle
    C                  Square
    V                  Triangle
    Q / E              L1 / R1
    R / F              L2 / R2
    Tab or Backspace   Options (menu)
    T / Y              Touchpad press, left / right (map)


6. THE TEST, AND WHAT TO SEND BACK
----------------------------------
1. Session one: play for 20-30 minutes, freezes and all, then close the
   window.
2. Session two: start again, wait for the warm-up, get outdoors. Stand
   still for a minute and note the fps in the window title. Then walk or
   ride for a minute and note the fps again. Play on as long as you like,
   then close the window.
3. Send back:
   - sharpemu-log.txt (the latest session) and sharpemu-log-previous.txt
     (the one before). Both are next to SharpEmu.exe. Zip them; they can
     be tens of MB. Each start moves the old log to "previous", so copy
     the logs away first if you play more than two sessions.
   - The game version (it is in the window title), the fps you noted,
     and roughly where in the game you were.
   - Anything that looked broken. Screenshots help.
   - Your CPU, graphics card and RAM (they are also in the log).

The log has a frame-rate line for every second, every shader build, and
any error, so it is all that is needed to work out what happened. It also
contains the folder paths the build and the game were run from, which can
include your Windows user name.

If the game does not start, freezes for more than five minutes, or
crashes: send the log anyway, with a line about what was on screen.

This build has so far only run on an AMD CPU with an NVIDIA RTX 3080.
Reports from Intel CPUs and from other graphics cards are the most useful
ones.


7. OPTIONAL SWITCHES
--------------------
To use one, open run-yotei.bat in Notepad and add the line above the
line that starts with "%~dp0SharpEmu.exe". Please say in your report
which ones you used.

    set SHARPEMU_YOTEI_LADDER=0
        Full detail. The default is a low row of the game's own quality
        ladder, which makes the game send about 45% fewer draws. Much
        slower (about 6 fps instead of 11-12 on the test machine).

    set SHARPEMU_YOTEI_RENDER_INDEX=game
        Lets the game choose its internal resolution. The default holds
        it at 1920x1080 (the game upscales to 4K itself). Slower.

    set SHARPEMU_YOTEI_RENDER_SIZE=1280x720
        Renders below 1920x1080. Only helps when the graphics card is
        the limit; it made no difference on the test machine.

    set SHARPEMU_FAST_FLOAT16=0
        Exact 16-bit float arithmetic in shaders. About 7% slower. Try it
        if the picture looks wrong in a way the known issues below do not
        cover, and say so.

The three YOTEI switches only work on game versions 01.512.000 and
02.002.000 and change no game file; on any other version they do nothing.


8. KNOWN ISSUES
---------------
- Low frame rate.
- Freezes the first time you reach a new place.
- Lower detail than on the console at the default settings: fewer
  shadows, shorter draw distance for small objects, 1920x1080 internal
  resolution.
- A scene that is somewhat too bright, and now and then texture flicker
  when video memory is nearly full. Close other games and graphics-heavy
  programs.
- On 02.002.000 the log repeats a warning about a "stencil plane". It
  is known and does not stop the game.
