@echo off
pwsh -NoProfile -File "%~dp0scripts\Dev-Run.ps1" %*
if errorlevel 1 pause
