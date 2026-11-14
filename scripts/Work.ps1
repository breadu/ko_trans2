param([ValidateSet('DevRun','MainMerge')][string]$Action = 'DevRun', [switch]$Desktop, [switch]$Approve, [switch]$BuildOnly, [string]$TestData, [switch]$MockAi, [switch]$DebugOverlay, [string]$ReleaseDirectory, [string]$NMeCabSourceArchive, [string]$IpaDicSourceArchive)
$ErrorActionPreference = 'Stop'
if ($Action -eq 'MainMerge') {
    # Explicit release preparation only. Approval/merge/install remain human-directed Git operations.
    if ($Approve) { throw 'Review docs/release.md and request the exact main merge separately. This script never merges, pushes main, or installs.' }
    if ($BuildOnly -or $TestData -or $MockAi -or $DebugOverlay) { throw 'MainMerge preparation cannot be combined with DEV-RUN options.' }
    if ($Desktop -and (!$ReleaseDirectory -or !$NMeCabSourceArchive -or !$IpaDicSourceArchive)) { throw 'Desktop release preparation requires a new ReleaseDirectory and the reviewed NMeCabSourceArchive and IpaDicSourceArchive. See docs/release.md.' }
    $root = Split-Path $PSScriptRoot -Parent
    Push-Location $root
    try {
        if (Get-Process KoTrans2 -ErrorAction SilentlyContinue) { throw 'Close KO Trans 2 normally first. No process was stopped.' }
        dotnet build KoTrans.slnx -c Release -t:Rebuild --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Explicit release rebuild failed.' }
        foreach ($suite in @('Foundation','Ocr','Translation','Study','Stability','GameQa','Wpf')) { & "$PSScriptRoot/Test-Fast.ps1" -Suite $suite }
        if ($Desktop) {
            & "$PSScriptRoot/Test-Desktop.ps1" -Suite WindowLifecycle
            $desktopExit = $LASTEXITCODE
            if ($desktopExit -eq 3) { throw 'Desktop environment unavailable (exit 3). Publish was NOT RUN; this is not a product test pass.' }
            if ($desktopExit -ne 0) { throw "Desktop verification failed (exit $desktopExit). Publish was NOT RUN." }
            & "$PSScriptRoot/Publish-Release.ps1" -OutputDirectory $ReleaseDirectory -NMeCabSourceArchive $NMeCabSourceArchive -IpaDicSourceArchive $IpaDicSourceArchive
        }
        Write-Output 'Release preparation checks finished. Review the remaining manual Desktop/clean-PC/license checks and request main merge approval separately. No Git changes or installation.'
    } finally { Pop-Location }
    return
}
if ($Desktop -or $Approve -or $ReleaseDirectory -or $NMeCabSourceArchive -or $IpaDicSourceArchive) { throw 'Release options require an explicit MainMerge preparation workflow.' }
& "$PSScriptRoot/Dev-Run.ps1" -BuildOnly:$BuildOnly -TestData $TestData -MockAi:$MockAi -DebugOverlay:$DebugOverlay
