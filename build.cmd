@echo off
rem ===========================================================================
rem  MOO2016_FCP - rebuild the performance patch from the original (UCP 6.4b) files.
rem  Windows Command Prompt version of build.sh (no bash tools required).
rem
rem  Usage:   build.cmd               release build into build\release
rem           build.cmd verify        + JIT-verify patched DLLs against the originals
rem           build.cmd perf          + profiling build into build\perf
rem           build.cmd verify perf   (any order)
rem  Missing tools: run setup.cmd first (checks and, after asking, installs them).
rem
rem  Optional environment variables (set before running):
rem    MOO_DIR       game folder (contains MasterOfOrion_Data); default: auto-detected (tools\find_moo\find_moo.cmd)
rem    GAME_MANAGED  the game's MasterOfOrion_Data\Managed folder (UnityEngine.dll etc.); default: from MOO_DIR
rem    ORIG_MANAGED  folder with the ORIGINAL (UCP 6.4b) Assembly-CSharp.dll / Assembly-CSharp-firstpass.dll; default: GAME_MANAGED
rem    ORIG_SWF      ORIGINAL (UCP 6.4b) EmpireManagement.swf; default: the game's StreamingAssets
rem                  (for each file, "<file>.orig" - the backup made by install.cmd - is used when the file itself is already patched)
rem    FFDEC_JAR     JPEXS FFDec jar; default: tools\ffdec\ffdec.jar, then the FFDec installer folders
rem  Requirements: .NET SDK 9+, Java 8+, JPEXS FFDec, Master of Orion with UCP 6.4b.
rem ===========================================================================
setlocal EnableExtensions EnableDelayedExpansion
set "SYS=%SystemRoot%\System32"
set "ROOT=%~dp0"
if "!ROOT:~-1!"=="\" set "ROOT=!ROOT:~0,-1!"
set "B=!ROOT!\build"
set "FAILLOG="

echo == checking tools
call "!ROOT!\tools\check_tools.cmd"
if not "!TOOLS_MISSING!"=="0" (
  echo ERROR: required tools are missing ^(see above^). Run  setup.cmd  - it explains and can install them.
  exit /b 1
)

if defined GAME_MANAGED goto :have_game
call "!ROOT!\tools\find_moo\find_moo.cmd" || exit /b 1
set "GAME_MANAGED=!MOO_DIR!\MasterOfOrion_Data\Managed"
echo Game folder: !MOO_DIR!   [found via !MOO_HOW!]
:have_game
if not exist "!GAME_MANAGED!\UnityEngine.dll" (echo ERROR: !GAME_MANAGED!\UnityEngine.dll not found - GAME_MANAGED/MOO_DIR does not point to the game. & exit /b 1)
if not defined ORIG_MANAGED set "ORIG_MANAGED=!GAME_MANAGED!"
if not defined ORIG_SWF set "ORIG_SWF=!GAME_MANAGED!\..\StreamingAssets\EmpireManagement.swf"

set "DO_VERIFY=0"
set "DO_PERF=0"
for %%a in (%*) do (
  if /i "%%a"=="verify" set "DO_VERIFY=1"
  if /i "%%a"=="perf" set "DO_PERF=1"
)
if not exist "!B!\pub" mkdir "!B!\pub"
if not exist "!B!\release" mkdir "!B!\release"
if not exist "!B!\perf" mkdir "!B!\perf"

echo == tools (the first build downloads NuGet packages)
for %%p in (BuildHelper FixPatcher PerfPatcher Verify) do (
  set "FAILLOG=!B!\log_build_%%p.txt"
  dotnet build -c Release "!ROOT!\tools\%%p" -o "!B!\tools\%%p" > "!FAILLOG!" 2>&1 || goto :fail
)
set "FAILLOG="
set "HELP=!B!\tools\BuildHelper\BuildHelper.dll"
set "FIX=!B!\tools\FixPatcher\FixPatcher.dll"
set "PERFP=!B!\tools\PerfPatcher\PerfPatcher.dll"
set "VER=!B!\tools\Verify\Verify.dll"

echo == original files (UCP 6.4b, checked by SHA-256)
if exist "!B!\orig" rmdir /s /q "!B!\orig"
dotnet "!HELP!" originals "!B!\orig" "!ORIG_MANAGED!\Assembly-CSharp.dll" "!ORIG_MANAGED!\Assembly-CSharp-firstpass.dll" "!ORIG_SWF!" || goto :fail
set "ORIG_MANAGED=!B!\orig\Managed"
set "ORIG_SWF=!B!\orig\StreamingAssets\EmpireManagement.swf"

echo == publicized reference assembly (compile-time only)
dotnet "!FIX!" publicize "!ORIG_MANAGED!\Assembly-CSharp.dll" "!B!\pub\Assembly-CSharp.dll" "!GAME_MANAGED!" || goto :fail

echo == MooFix / MooPerf
set "FAILLOG=!B!\log_build_MooFix.txt"
dotnet build -c Release "!ROOT!\src\MooFix" "-p:GameManaged=!GAME_MANAGED!" "-p:PubDir=!B!\pub" -o "!B!\moofix" > "!FAILLOG!" 2>&1 || goto :fail
set "FAILLOG=!B!\log_build_MooPerf.txt"
dotnet build -c Release "!ROOT!\src\MooPerf" -o "!B!\mooperf" > "!FAILLOG!" 2>&1 || goto :fail
set "FAILLOG="
copy /y "!B!\moofix\MooFix.dll" "!B!\release\" >nul

echo == inject prefix hooks
set "FAILLOG=!B!\patch_acs.log"
dotnet "!FIX!" patch "!ORIG_MANAGED!\Assembly-CSharp.dll" "!B!\release\MooFix.dll" "!B!\release\Assembly-CSharp.dll" "!GAME_MANAGED!" > "!FAILLOG!" 2>&1 || goto :fail
set "FAILLOG=!B!\patch_fp.log"
dotnet "!FIX!" patch "!ORIG_MANAGED!\Assembly-CSharp-firstpass.dll" "!B!\release\MooFix.dll" "!B!\release\Assembly-CSharp-firstpass.dll" "!GAME_MANAGED!" > "!FAILLOG!" 2>&1 || goto :fail
set "FAILLOG="
"!SYS!\findstr.exe" /c:"injected" "!B!\patch_acs.log" "!B!\patch_fp.log"

echo == SWF: replace method bodies (P-code) in EmpireManagement.swf
rem Method body indices are specific to the UCP 6.4b EmpireManagement.swf (see docs\05_SWF_PATCHING.md).
rem Only diffs are distributed (swf\patches); the original bodies come from the user's own SWF.
set "S=!B!\pcode"
if exist "!B!\pcode_export" rmdir /s /q "!B!\pcode_export"
if exist "!S!" rmdir /s /q "!S!"
set "FAILLOG=!B!\ffdec_export.log"
java -jar "!FFDEC_JAR!" -format script:pcode -selectclass ngd.ColonyWindow,ngd.ASRoot,fl.controls.ScrollBar -export script "!B!\pcode_export" "!ORIG_SWF!" > "!FAILLOG!" 2>&1
if not exist "!B!\pcode_export\scripts\ngd\ColonyWindow.pcode" (echo ERROR: FFDec P-code export failed. & goto :fail)
set "FAILLOG="
dotnet "!HELP!" apply "!B!\pcode_export" "!ROOT!\swf\patches" "!S!" || goto :fail
if exist "!B!\release\EmpireManagement.swf" del "!B!\release\EmpireManagement.swf"
set "FAILLOG=!B!\ffdec_release.log"
java -jar "!FFDEC_JAR!" -replace "!ORIG_SWF!" "!B!\release\EmpireManagement.swf" ngd.ColonyWindow "!S!\ctor.pcode" 712 ngd.ColonyWindow "!S!\fill.pcode" 718 ngd.ColonyWindow "!S!\adj.pcode" 1236 ngd.ColonyWindow "!S!\buy.pcode" 1237 ngd.ASRoot "!S!\scroll.pcode" 979 fl.controls.ScrollBar "!S!\sb_press.pcode" 818 fl.controls.ScrollBar "!S!\sb_release.pcode" 820 > "!FAILLOG!" 2>&1
if not exist "!B!\release\EmpireManagement.swf" (echo ERROR: FFDec did not write the patched SWF. & goto :fail)
set "NREP=0"
for /f "delims=" %%l in ('type "!B!\ffdec_release.log"') do echo %%l | "!SYS!\findstr.exe" /c:"Replace AS3 PCode" >nul && set /a NREP+=1
echo   method bodies replaced: !NREP! ^(expected 7^)
if not "!NREP!"=="7" (echo ERROR: expected 7 replaced method bodies. & goto :fail)
set "FAILLOG="

if "!DO_PERF!"=="1" (
  echo == profiling build ^(MooPerf wrappers on ~1760 methods^)
  copy /y "!B!\mooperf\MooPerf.dll" "!B!\perf\" >nul
  copy /y "!B!\release\MooFix.dll" "!B!\perf\" >nul
  dotnet "!PERFP!" "!B!\release\Assembly-CSharp.dll" "!B!\perf\MooPerf.dll" "!B!\perf\Assembly-CSharp.dll" "!GAME_MANAGED!" || goto :fail
  dotnet "!PERFP!" "!B!\release\Assembly-CSharp-firstpass.dll" "!B!\perf\MooPerf.dll" "!B!\perf\Assembly-CSharp-firstpass.dll" "!GAME_MANAGED!" || goto :fail
  if exist "!B!\perf\EmpireManagement.swf" del "!B!\perf\EmpireManagement.swf"
  set "FAILLOG=!B!\ffdec_perf.log"
  java -jar "!FFDEC_JAR!" -replace "!B!\release\EmpireManagement.swf" "!B!\perf\EmpireManagement.swf" ngd.ASRoot "!S!\enterframe.pcode" 973 > "!B!\ffdec_perf.log" 2>&1
  if not exist "!B!\perf\EmpireManagement.swf" (echo ERROR: FFDec did not write the profiling SWF. & goto :fail)
  set "FAILLOG="
)

if "!DO_VERIFY!"=="1" (
  echo == JIT verification ^(patched vs original; lists must be identical^)
  set "ALLM=1"
  for %%f in (Assembly-CSharp.dll Assembly-CSharp-firstpass.dll) do (
    dotnet "!VER!" "!ORIG_MANAGED!\%%f" "!B!\release" "!GAME_MANAGED!" > "!B!\jit_orig_%%f.log" 2>&1
    dotnet "!VER!" "!B!\release\%%f" "!B!\release" "!GAME_MANAGED!" > "!B!\jit_patched_%%f.log" 2>&1
    "!SYS!\findstr.exe" /c:"INVALID" /c:"jitted" "!B!\jit_orig_%%f.log" > "!B!\jit_orig_%%f.txt"
    "!SYS!\findstr.exe" /c:"INVALID" /c:"jitted" "!B!\jit_patched_%%f.log" > "!B!\jit_patched_%%f.txt"
    "!SYS!\fc.exe" "!B!\jit_orig_%%f.txt" "!B!\jit_patched_%%f.txt" >nul
    if errorlevel 1 (echo   %%f: DIFFERS - see build\jit_*_%%f.txt) else (echo   %%f: identical to original)
  )
)

echo == done: !B!\release
dir /b "!B!\release"
echo Next: install.cmd  ^(close the game first^)
exit /b 0

:fail
echo.
echo BUILD FAILED
if not defined FAILLOG exit /b 1
if not exist "!FAILLOG!" exit /b 1
set "NL=0"
for /f %%n in ('type "!FAILLOG!" ^| "!SYS!\find.exe" /c /v ""') do set "NL=%%n"
set /a SKIP=NL-30
if !SKIP! lss 0 set "SKIP=0"
echo ---- last lines of !FAILLOG! ----
for /f "skip=2 tokens=1* delims=[]" %%a in ('""!SYS!\find.exe" /n /v "" "!FAILLOG!""') do if %%a GTR !SKIP! echo(%%b
echo ----
echo If the log mentions NuGet or a network error, check the internet connection and run build.cmd again.
exit /b 1
