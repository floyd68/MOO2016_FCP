@echo off
rem Restore the original (UCP) files from the *.orig backups (Windows Command Prompt version of uninstall.sh).
rem Game folder is auto-detected (tools\find_moo\find_moo.cmd).
rem Optional: set MOO_DIR=<game folder>  or  set GAME=<...\MasterOfOrion_Data>
setlocal EnableExtensions EnableDelayedExpansion
set "SYS=%SystemRoot%\System32"
if defined GAME goto :have_game
call "%~dp0tools\find_moo\find_moo.cmd" || exit /b 1
set "GAME=!MOO_DIR!\MasterOfOrion_Data"
echo Game folder: !MOO_DIR!   [found via !MOO_HOW!]
:have_game
"!SYS!\tasklist.exe" | "!SYS!\findstr.exe" /i "MasterOfOrion" >nul && (echo Close the game first. & exit /b 1)
for %%f in (Managed\Assembly-CSharp.dll Managed\Assembly-CSharp-firstpass.dll StreamingAssets\EmpireManagement.swf) do (
  if exist "!GAME!\%%f.orig" (
    copy /y "!GAME!\%%f.orig" "!GAME!\%%f" >nul
    echo restored %%f
  ) else (
    echo no backup for %%f - skipped
  )
)
if exist "!GAME!\Managed\MooFix.dll" del "!GAME!\Managed\MooFix.dll"
if exist "!GAME!\Managed\MooPerf.dll" del "!GAME!\Managed\MooPerf.dll"
echo Done. ^(Steam "Verify integrity of game files" also restores the files, but then UCP must be reinstalled.^)
exit /b 0
