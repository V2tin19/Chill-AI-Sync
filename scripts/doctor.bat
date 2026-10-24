@echo off
rem Chill AI diagnostics launcher (troubleshooting).
rem Keep this file ASCII-only: all Chinese output comes from doctor.ps1 (UTF-8 with BOM).
chcp 65001 >nul
title Chill AI - doctor
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0doctor.ps1" -Pause
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0doctor.ps1" %*
)
exit /b %ERRORLEVEL%
