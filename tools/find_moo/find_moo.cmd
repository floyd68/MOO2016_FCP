@echo off
rem ===========================================================================
rem  find_moo.cmd - locate the Master of Orion (2016) installation folder.
rem  Usage (from another batch file):   call "%~dp0tools\find_moo\find_moo.cmd"
rem  On success sets MOO_DIR (game folder that contains MasterOfOrion_Data) and
rem  MOO_HOW (how it was found) in the caller's environment; errorlevel 0.
rem  On failure prints instructions and returns errorlevel 1.
rem
rem  Search order:
rem    1. MOO_DIR environment variable (explicit override)
rem    2. Steam (MOO_STEAM_ROOT env var, else registry) -> steamapps\libraryfolders.vdf -> appmanifest_298050.acf -> installdir
rem    3. GOG Galaxy: registry HKLM\SOFTWARE\(WOW6432Node\)GOG.com\Games\* with gameName "Master of Orion*"
rem    4. default path C:\Program Files (x86)\Steam\steamapps\common\Master of Orion
rem ===========================================================================
setlocal EnableExtensions EnableDelayedExpansion
set "SYS=%SystemRoot%\System32"
set "APPID=298050"
set "FOUND="
set "HOW="

rem ---- 1. explicit ----
if defined MOO_DIR (
  call :check "!MOO_DIR!" && (set "FOUND=!MOO_DIR!" & set "HOW=MOO_DIR environment variable")
  if not defined FOUND (echo MOO_DIR="!MOO_DIR!" does not contain MasterOfOrion_Data\Managed\Assembly-CSharp.dll & endlocal & exit /b 1)
)

rem ---- 2. Steam ----
if not defined FOUND (
  set "STEAM=!MOO_STEAM_ROOT!"
  if not defined STEAM for /f "tokens=2,*" %%a in ('""!SYS!\reg.exe" query "HKCU\Software\Valve\Steam" /v SteamPath 2>nul"') do set "STEAM=%%b"
  if not defined STEAM for /f "tokens=2,*" %%a in ('""!SYS!\reg.exe" query "HKLM\SOFTWARE\WOW6432Node\Valve\Steam" /v InstallPath 2>nul"') do set "STEAM=%%b"
  if not defined STEAM for /f "tokens=2,*" %%a in ('""!SYS!\reg.exe" query "HKLM\SOFTWARE\Valve\Steam" /v InstallPath 2>nul"') do set "STEAM=%%b"
  if defined STEAM (
    set "STEAM=!STEAM:/=\!"
    call :steamlib "!STEAM!"
    set "VDF=!STEAM!\steamapps\libraryfolders.vdf"
    if not defined FOUND if exist "!VDF!" (
      for /f "usebackq tokens=1,*" %%a in ("!VDF!") do (
        if not defined FOUND if /i "%%~a"=="path" (
          set "LIB=%%~b"
          set "LIB=!LIB:\\=\!"
          call :steamlib "!LIB!"
        )
      )
    )
  )
)

rem ---- 3. GOG ----
if not defined FOUND (
  for %%r in ("HKLM\SOFTWARE\WOW6432Node\GOG.com\Games" "HKLM\SOFTWARE\GOG.com\Games") do (
    if not defined FOUND for /f "delims=" %%k in ('""!SYS!\reg.exe" query %%r 2>nul"') do (
      if not defined FOUND (
        set "GN="
        set "GP="
        for /f "tokens=2,*" %%a in ('""!SYS!\reg.exe" query "%%k" /v gameName 2>nul"') do set "GN=%%b"
        if defined GN if /i "!GN:~0,15!"=="Master of Orion" (
          for /f "tokens=2,*" %%a in ('""!SYS!\reg.exe" query "%%k" /v path 2>nul"') do set "GP=%%b"
          if defined GP call :check "!GP!" && (set "FOUND=!GP!" & set "HOW=GOG registry")
        )
      )
    )
  )
)

rem ---- 4. default ----
if not defined FOUND (
  set "DEF=C:\Program Files (x86)\Steam\steamapps\common\Master of Orion"
  call :check "!DEF!" && (set "FOUND=!DEF!" & set "HOW=default path")
)

if not defined FOUND (
  echo Could not find the Master of Orion installation.
  echo Set it explicitly, e.g.:  set "MOO_DIR=D:\Games\Steam\steamapps\common\Master of Orion"
  echo ^(the folder that contains MasterOfOrion_Data^), then run the script again.
  endlocal & exit /b 1
)
endlocal & set "MOO_DIR=%FOUND%" & set "MOO_HOW=%HOW%"
exit /b 0

rem ---- helpers ----
:steamlib
rem %1 = Steam library root; looks for appmanifest_298050.acf and reads installdir
if defined FOUND exit /b 0
set "ACF=%~1\steamapps\appmanifest_%APPID%.acf"
if not exist "%ACF%" exit /b 0
set "IDIR="
for /f "usebackq tokens=1,*" %%a in ("%ACF%") do if /i "%%~a"=="installdir" set "IDIR=%%~b"
if not defined IDIR exit /b 0
rem use delayed variables inside ( ): paths like "C:\Program Files (x86)" would close the block early
set "LIBROOT=%~1"
set "CAND=%~1\steamapps\common\!IDIR!"
call :check "!CAND!" && (set "FOUND=!CAND!" & set "HOW=Steam library !LIBROOT!")
exit /b 0

:check
rem %1 = candidate game folder
if exist "%~1\MasterOfOrion_Data\Managed\Assembly-CSharp.dll" exit /b 0
exit /b 1
