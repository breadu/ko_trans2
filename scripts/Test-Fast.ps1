param([ValidateSet('Foundation','Settings','Image','Ocr','Translation','Study','Wpf','Stability','GameQa')][string]$Suite = 'Foundation')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$clock = [Diagnostics.Stopwatch]::StartNew()
$project = if ($Suite -eq 'Wpf') { 'KoTrans.WpfTests' } else { 'KoTrans.Tests' }
try {
    # WPF references the app project, whose DLLs are locked by a running DEV-RUN.
    if ($Suite -eq 'Wpf' -and (Get-Process KoTrans2 -ErrorAction SilentlyContinue)) {
        throw 'ENVIRONMENT UNAVAILABLE: KO Trans 2 is running. Use its notification-area icon > Exit (X only hides) before WPF FAST; no build/test was started and no process was stopped.'
    }
    dotnet run --project "$root/tests/$project/$project.csproj" -c Release -- $Suite
    if ($LASTEXITCODE -ne 0) { throw "Background $Suite tests failed (exit $LASTEXITCODE)." }
} finally {
    Write-Output ('Background {0}: build + run {1:F2}s' -f $Suite, $clock.Elapsed.TotalSeconds)
}
