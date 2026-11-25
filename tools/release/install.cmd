@echo off
rem MOO2016_FCP binary release - install.  Usage: install.cmd ["<Master of Orion folder>"]
setlocal EnableExtensions EnableDelayedExpansion
set "HERE=%~dp0"
if not "%~1"=="" set "MOO_DIR=%~1"
call "%HERE%files\find_moo.cmd"
if errorlevel 1 goto :fail
echo Game folder: !MOO_DIR!   [found via !MOO_HOW!]
echo.
"%HERE%files\FcpPatch.exe" install "!MOO_DIR!" "%HERE%files"
if errorlevel 1 goto :fail
echo.
echo Installed. Start the game as usual. To remove the patch, run uninstall.cmd.
pause
exit /b 0

:fail
echo.
echo Installation was not completed (see the messages above). Nothing was changed if a check failed.
pause
exit /b 1
