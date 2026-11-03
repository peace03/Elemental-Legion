@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Elemental Legion Server
pushd "%~dp0"
set "menuArgument="
if "%~1"=="" set "menuArgument=-SelectScenario"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0client\Tools\Start-Server.ps1" -ReportNetwork -ReportGameplay %menuArgument% %*
set "serverExitCode=%errorlevel%"
popd
echo.
echo Server exited with code %serverExitCode%.
pause
exit /b %serverExitCode%
