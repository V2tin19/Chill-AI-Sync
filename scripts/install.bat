@echo off
rem Chill AI one-click installer launcher.
rem Keep this file ASCII-only: all Chinese output comes from install.ps1 (UTF-8 with BOM).
chcp 65001 >nul
title Chill AI - installer
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Pause
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
)
exit /b %ERRORLEVEL%
