@echo off
rem Install the patch into the game folder (Windows Command Prompt version of install.sh).
rem Usage: install.cmd [source_dir]   (default: build\release, falls back to _local_only\release)
rem Before copying, checks that the game files are the UCP 6.4b originals or already patched by this project
rem (tools\BuildHelper prepare-target) and keeps UCP 6.4b *.orig backups of the replaced files.
rem Game folder is auto-detected (tools\find_moo\find_moo.cmd).
rem Optional: set MOO_DIR=<game folder>  or  set GAME=<...\MasterOfOrion_Data>
setlocal EnableExtensions EnableDelayedExpansion
set "SYS=%SystemRoot%\System32"
set "ROOT=%~dp0"
if "!ROOT:~-1!"=="\" set "ROOT=!ROOT:~0,-1!"
if defined GAME goto :have_game
call "!ROOT!\tools\find_moo\find_moo.cmd" || exit /b 1
set "GAME=!MOO_DIR!\MasterOfOrion_Data"
echo Game folder: !MOO_DIR!   [found via !MOO_HOW!]
:have_game
set "SRC=%~1"
if not defined SRC set "SRC=!ROOT!\build\release"
"!SYS!\tasklist.exe" | "!SYS!\findstr.exe" /i "MasterOfOrion" >nul && (echo Close the game first. & exit /b 1)

rem build layout (flat) or archive layout (Managed\, StreamingAssets\)
set "DLLDIR=!SRC!"
set "SWFDIR=!SRC!"
rem an explicitly given folder must contain a build; only the default falls back to the archived release
if not "%~1"=="" if not exist "!SRC!\MooFix.dll" (echo !SRC! does not contain a build - MooFix.dll missing. & exit /b 1)
if not exist "!SRC!\MooFix.dll" (
  set "SRC=!ROOT!\_local_only\release"
  set "DLLDIR=!ROOT!\_local_only\release\Managed"
  set "SWFDIR=!ROOT!\_local_only\release\StreamingAssets"
)
if not exist "!DLLDIR!\MooFix.dll" (echo No build found. Run build.cmd first. & exit /b 1)

rem the target check needs the BuildHelper built by build.cmd and the .NET SDK
set "HELP=!ROOT!\build\tools\BuildHelper\BuildHelper.dll"
if not exist "!HELP!" (echo !HELP! not found. Run build.cmd first. & exit /b 1)
"!SYS!\where.exe" dotnet >nul 2>&1 || if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;!PATH!"
"!SYS!\where.exe" dotnet >nul 2>&1 || (echo dotnet not found. Run setup.cmd. & exit /b 1)

echo == checking the game files
dotnet "!HELP!" prepare-target "!GAME!" "!DLLDIR!\Assembly-CSharp.dll" "!DLLDIR!\Assembly-CSharp-firstpass.dll" "!SWFDIR!\EmpireManagement.swf" || (echo Installation cancelled. & exit /b 1)

echo == installing
for %%f in (Assembly-CSharp.dll Assembly-CSharp-firstpass.dll MooFix.dll) do (
  copy /y "!DLLDIR!\%%f" "!GAME!\Managed\%%f" >nul || (echo copy failed: %%f & exit /b 1)
)
copy /y "!SWFDIR!\EmpireManagement.swf" "!GAME!\StreamingAssets\EmpireManagement.swf" >nul || (echo copy failed: EmpireManagement.swf & exit /b 1)
rem profiling builds (build\perf) also need MooPerf.dll; release builds must not leave it behind
if exist "!DLLDIR!\MooPerf.dll" (copy /y "!DLLDIR!\MooPerf.dll" "!GAME!\Managed\MooPerf.dll" >nul) else if exist "!GAME!\Managed\MooPerf.dll" del "!GAME!\Managed\MooPerf.dll"
echo Installed from !SRC!
exit /b 0
