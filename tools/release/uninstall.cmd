@echo off
rem MOO2016_FCP binary release - uninstall (restores the *.orig backups).  Usage: uninstall.cmd ["<Master of Orion folder>"]
setlocal EnableExtensions EnableDelayedExpansion
set "HERE=%~dp0"
if not "%~1"=="" set "MOO_DIR=%~1"
call "%HERE%files\find_moo.cmd"
if errorlevel 1 goto :fail
echo Game folder: !MOO_DIR!   [found via !MOO_HOW!]
echo.
"%HERE%files\FcpPatch.exe" uninstall "!MOO_DIR!"
if errorlevel 1 goto :fail
pause
exit /b 0

:fail
echo.
echo Uninstall was not completed (see the messages above).
pause
exit /b 1
