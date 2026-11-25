@echo off
rem ===========================================================================
rem  check_tools.cmd - checks the build tools and prints one status line per tool.
rem  CALL it from a batch file that already ran "setlocal EnableDelayedExpansion"; it sets, in the caller:
rem    DOTNET_OK / DOTNET_VER   .NET SDK 9 or newer
rem    JAVA_OK   / JAVA_VER     Java 8 or newer
rem    FFDEC_JAR                path of ffdec.jar (empty if not found)
rem    HAVE_WINGET              winget available
rem    TOOLS_MISSING            number of missing tools
rem  Tools installed a moment ago (PATH of this window not refreshed yet) are found in their default folders
rem  and added to PATH for this window.
rem ===========================================================================
set "SYS=%SystemRoot%\System32"
set "CT_ROOT=%~dp0.."
set "TOOLS_MISSING=0"

rem ---- .NET SDK 9+ ----
set "DOTNET_OK="
set "DOTNET_VER="
"!SYS!\where.exe" dotnet >nul 2>&1 || if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;!PATH!"
for /f "tokens=1" %%v in ('dotnet --list-sdks 2^>nul') do (
  for /f "tokens=1 delims=." %%m in ("%%v") do if %%m GEQ 9 (set "DOTNET_OK=1" & set "DOTNET_VER=%%v")
)
if defined DOTNET_OK (echo   [ok]      .NET SDK !DOTNET_VER!) else (
  echo   [missing] .NET SDK 9 or newer
  "!SYS!\where.exe" dotnet >nul 2>&1 && (
    echo             installed SDKs ^(a .NET runtime alone is not enough^):
    dotnet --list-sdks
  )
  set /a TOOLS_MISSING+=1
)

rem ---- Java 8+ ----
set "JAVA_OK="
set "JAVA_VER="
set "JAVA_DIR="
"!SYS!\where.exe" java >nul 2>&1 || (
  for /d %%d in ("%ProgramFiles%\Eclipse Adoptium\*" "%ProgramFiles%\Microsoft\jdk-*" "%ProgramFiles%\Java\*") do if exist "%%~d\bin\java.exe" set "JAVA_DIR=%%~d\bin"
)
if defined JAVA_DIR set "PATH=!JAVA_DIR!;!PATH!"
for /f "tokens=3" %%v in ('java -version 2^>^&1 ^| "!SYS!\findstr.exe" /i /c:"version"') do if not defined JAVA_VER set "JAVA_VER=%%~v"
set "JM="
if defined JAVA_VER for /f "tokens=1,2 delims=._-" %%a in ("!JAVA_VER!") do (set "JM=%%a" & if "%%a"=="1" set "JM=%%b")
if defined JM if !JM! GEQ 8 set "JAVA_OK=1"
if defined JAVA_OK (echo   [ok]      Java !JAVA_VER!) else (
  if defined JAVA_VER (echo   [missing] Java 8 or newer  ^(found !JAVA_VER!^)) else (echo   [missing] Java 8 or newer)
  set /a TOOLS_MISSING+=1
)

rem ---- JPEXS FFDec ----
if defined FFDEC_JAR if not exist "!FFDEC_JAR!" (echo   FFDEC_JAR=!FFDEC_JAR! does not exist - ignored & set "FFDEC_JAR=")
if not defined FFDEC_JAR for %%j in ("!CT_ROOT!\tools\ffdec\ffdec.jar" "!CT_ROOT!\_local_only\external\ffdec\ffdec.jar" "%ProgramFiles%\FFDec\ffdec.jar" "%ProgramFiles(x86)%\FFDec\ffdec.jar") do if not defined FFDEC_JAR if exist "%%~j" set "FFDEC_JAR=%%~fj"
if defined FFDEC_JAR (echo   [ok]      JPEXS FFDec  !FFDEC_JAR!) else (
  echo   [missing] JPEXS FFDec  ^(tools\ffdec\ffdec.jar^)
  set /a TOOLS_MISSING+=1
)

rem ---- winget (only needed by setup.cmd to install .NET / Java) ----
set "HAVE_WINGET="
"!SYS!\where.exe" winget >nul 2>&1 && set "HAVE_WINGET=1"
exit /b 0
