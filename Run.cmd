@echo off
if not exist "%~dp0release\KoTrans2.exe" (
  echo KO Trans 2 main installation is missing. See docs/main-install.md.
  pause
  exit /b 1
)
start "" "%~dp0release\KoTrans2.exe" %*
