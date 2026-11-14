@echo off
rem Copyright (c) 2026 Legion Builds
rem Starts the portable hardware check. Installs nothing and changes no setting: PowerShell runs with a process-scoped
rem execution-policy bypass for this one script, and everything the check writes goes into the work\ folder beside this file.
setlocal
title Viewer hardware check
set "KIT=%~dp0"
echo Viewer hardware check - this takes roughly 30 minutes. Keep the laptop plugged in and leave it alone while it runs.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%KIT%scripts\run-laptop.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo The check stopped with exit code %RC%. Zip the work folder and bring it back with any message above.
echo Press any key to close this window.
pause >nul
exit /b %RC%
