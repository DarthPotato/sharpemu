@echo off
rem Ghost of Yotei test launcher.
rem Usage: drag eboot.bin onto this file, or:
rem   run-yotei.bat "D:\PPSA26344\eboot.bin"
rem   run-yotei.bat "D:\decrypted\eboot.bin" "D:\PPSA26344"   (eboot.bin kept apart from the game files)
if "%~1"=="" (
  echo Drag the game's eboot.bin onto this file, or run: run-yotei.bat "D:\path\to\PPSA26344\eboot.bin"
  echo See README.txt for the full instructions.
  pause
  exit /b 1
)
cd /d "%~dp0"
rem Keep the log of the session before this one.
if exist sharpemu-log.txt move /y sharpemu-log.txt sharpemu-log-previous.txt >nul
set SHARPEMU_VK_PIPELINE_CACHE=file
set SHARPEMU_LOG_VIDEOOUT_FPS=1
rem Optional second argument: the game folder (eboot.bin's own folder is used when it is not given).
if not "%~2"=="" set SHARPEMU_APP0_DIR=%~2
"%~dp0SharpEmu.exe" "%~1" --log-file "%~dp0sharpemu-log.txt"
