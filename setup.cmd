@echo off
rem ===========================================================================
rem  setup.cmd - check everything the build needs and install missing tools after asking.
rem
rem  Usage:  setup.cmd         ask before each installation
rem          setup.cmd yes     install missing tools without asking
rem          setup.cmd no      only report, install nothing
rem
rem  Installs (only after confirmation):
rem    JPEXS FFDec 26.5.0   downloaded from GitHub into tools\ffdec\ (SHA-256 checked; nothing installed system-wide)
rem    .NET SDK 9           winget install Microsoft.DotNet.SDK.9
rem    Java (Temurin 21)    winget install EclipseAdoptium.Temurin.21.JRE
rem  Cannot install (reported with instructions): Master of Orion, UCP 6.4b.
rem ===========================================================================
setlocal EnableExtensions EnableDelayedExpansion
set "SYS=%SystemRoot%\System32"
set "ROOT=%~dp0"
if "!ROOT:~-1!"=="\" set "ROOT=!ROOT:~0,-1!"
set "MODE=%~1"
set "FFDEC_VER=26.5.0"
set "FFDEC_URL=https://github.com/jindrapetrik/jpexs-decompiler/releases/download/version26.5.0/ffdec_26.5.0.zip"
set "FFDEC_SHA=8d00c2713a724bb3f7e6c9a64994836ce3a953b5ac264f405d98f92e7c266d33"

echo == Build tools
call "!ROOT!\tools\check_tools.cmd"
if "!TOOLS_MISSING!"=="0" goto :game

echo.
if not defined FFDEC_JAR call :get_ffdec
if not defined DOTNET_OK call :winget_install Microsoft.DotNet.SDK.9 ".NET SDK 9" "https://dotnet.microsoft.com/download"
if not defined JAVA_OK call :winget_install EclipseAdoptium.Temurin.21.JRE "Java runtime (Eclipse Temurin 21 JRE)" "https://adoptium.net/"
echo.
echo == Build tools (after setup)
call "!ROOT!\tools\check_tools.cmd"

:game
echo.
echo == Game and UCP
set "GAME_OK="
set "UCP_OK="
call "!ROOT!\tools\find_moo\find_moo.cmd"
if errorlevel 1 goto :summary
set "GAME_OK=1"
echo   [ok]      Master of Orion  !MOO_DIR!   [found via !MOO_HOW!]
set "D=!MOO_DIR!\MasterOfOrion_Data"
set "UCP_BAD=0"
call :ucp "!D!\Managed\Assembly-CSharp.dll" 915c5eacf2314a64a7f39ac243c45333f07e96b70e5c1ec26b90888d64b5895a
call :ucp "!D!\Managed\Assembly-CSharp-firstpass.dll" ab936ff9ea0bd8157e5a5d503dc4def9ea13c731aa737f82c006bd5d38d039e6
call :ucp "!D!\StreamingAssets\EmpireManagement.swf" 55d6a8489a816ae9c794affacedf6dfeb8603cfd92b782842d19f4cfb5932e51
if "!UCP_BAD!"=="0" (set "UCP_OK=1" & echo   [ok]      UCP 6.4b original files) else (
  echo   [missing] UCP 6.4b original files
  echo             Install UCP 6.4b: run the game once to the main menu and quit, subscribe to
  echo             https://steamcommunity.com/sharedfiles/filedetails/?id=1109736522
  echo             then run javaInstaller.bat in ...\steamapps\workshop\content\298050\1109736522
  echo             and keep the "Unofficial Code Patch" mod active in the game's mod menu.
)

:summary
echo.
set "READY=1"
if not "!TOOLS_MISSING!"=="0" set "READY="
if not defined GAME_OK set "READY="
if not defined UCP_OK set "READY="
if defined READY (
  echo Everything is ready. Next:  build.cmd   then   install.cmd
  exit /b 0
)
echo Not ready yet - see the [missing] lines above.
if defined INSTALLED_SOMETHING echo If a tool was just installed but is still reported missing, open a NEW Command Prompt and run setup.cmd again.
exit /b 1

rem ---------------------------------------------------------------------------
:ask
rem %1 = question; sets ANSWER=Y or N
set "ANSWER=N"
if /i "!MODE!"=="yes" (set "ANSWER=Y" & exit /b 0)
if /i "!MODE!"=="no" exit /b 0
"!SYS!\choice.exe" /c YN /m "%~1"
if not errorlevel 2 set "ANSWER=Y"
exit /b 0

:get_ffdec
echo JPEXS FFDec !FFDEC_VER! is needed to patch the Flash UI. It is downloaded into tools\ffdec\ only.
echo   source: !FFDEC_URL!
call :ask "Download and extract JPEXS FFDec !FFDEC_VER! now"
if not "!ANSWER!"=="Y" (call :ffdec_manual & exit /b 0)
if not exist "!SYS!\curl.exe" (echo   curl.exe not found ^(Windows 10 1803 or newer has it^). & call :ffdec_manual & exit /b 0)
if not exist "!SYS!\tar.exe" (echo   tar.exe not found ^(Windows 10 1803 or newer has it^). & call :ffdec_manual & exit /b 0)
if not exist "!ROOT!\build" mkdir "!ROOT!\build"
set "ZIP=!ROOT!\build\ffdec_!FFDEC_VER!.zip"
"!SYS!\curl.exe" -fL --retry 2 -o "!ZIP!" "!FFDEC_URL!" || (echo   download failed. & call :ffdec_manual & exit /b 0)
set "GOT="
for /f "skip=1 delims=" %%h in ('""!SYS!\certutil.exe" -hashfile "!ZIP!" SHA256"') do if not defined GOT set "GOT=%%h"
if defined GOT set "GOT=!GOT: =!"
if /i not "!GOT!"=="!FFDEC_SHA!" (echo   SHA-256 mismatch ^(!GOT!^) - file deleted. & del "!ZIP!" & call :ffdec_manual & exit /b 0)
if not exist "!ROOT!\tools\ffdec" mkdir "!ROOT!\tools\ffdec"
"!SYS!\tar.exe" -xf "!ZIP!" -C "!ROOT!\tools\ffdec" || (echo   extraction failed. & call :ffdec_manual & exit /b 0)
del "!ZIP!"
echo   JPEXS FFDec extracted to tools\ffdec\
set "INSTALLED_SOMETHING=1"
exit /b 0

:ffdec_manual
echo   Manual: download ffdec_!FFDEC_VER!.zip from https://github.com/jindrapetrik/jpexs-decompiler/releases
echo           and extract it to !ROOT!\tools\ffdec\  ^(tools\ffdec\ffdec.jar must exist^), or set FFDEC_JAR.
exit /b 0

:winget_install
rem %1 = winget id, %2 = display name, %3 = manual download page
echo %~2 is needed.
if not defined HAVE_WINGET (
  echo   winget is not available. Install it manually from %~3
  echo   ^(or install "App Installer" from the Microsoft Store to get winget^).
  exit /b 0
)
call :ask "Install %~2 now with winget (may show a Windows administrator prompt)"
if not "!ANSWER!"=="Y" (echo   Skipped. Install it manually from %~3 & exit /b 0)
winget install --id %~1 --exact --accept-source-agreements --accept-package-agreements
if errorlevel 1 (echo   winget reported an error. Install it manually from %~3) else set "INSTALLED_SOMETHING=1"
exit /b 0

:ucp
rem %1 = game file, %2 = expected UCP 6.4b sha256; the file itself or its .orig backup must match
set "F=%~1"
set "OKF="
for %%c in ("!F!" "!F!.orig") do if not defined OKF if exist "%%~c" (
  set "GOT="
  for /f "skip=1 delims=" %%h in ('""!SYS!\certutil.exe" -hashfile "%%~c" SHA256"') do if not defined GOT set "GOT=%%h"
  if defined GOT set "GOT=!GOT: =!"
  if /i "!GOT!"=="%~2" set "OKF=%%~nxc"
)
if defined OKF (echo             !OKF!) else (echo             %~nx1: not UCP 6.4b & set /a UCP_BAD+=1)
exit /b 0
