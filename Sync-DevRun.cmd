@echo off
setlocal
where pwsh.exe >nul 2>&1
if errorlevel 1 (
    echo [ERROR] PowerShell 7 is required.
    pause
    exit /b 1
)
pwsh.exe -NoLogo -NoProfile -File "%~dp0scripts\Sync-DevRun.ps1" %*
set "result=%errorlevel%"
if not "%result%"=="0" (
    echo [ERROR] Sync-DevRun failed. See the message above.
    pause
)
exit /b %result%
