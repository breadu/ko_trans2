param([ValidateSet('WindowLifecycle','OverlayDrag','DictionaryHoverCapture','StudyResize','WindowResize','MagpieNotifications')][string]$Suite = 'WindowLifecycle', [switch]$List)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$clock = [Diagnostics.Stopwatch]::StartNew()
# This entry point is ONLY for an explicit user request. FAST/Work never invoke it.
if ($List) { $testArgs = @('--list') } else { $testArgs = @('--desktop', $Suite) }
try {
    if (!$List) {
        if ($Suite -eq 'MagpieNotifications') { Write-Output 'Desktop: hidden native windows and test-only broadcasts; no displayed windows or input.' }
        else { Write-Output 'Desktop: real WPF windows will be shown. Requires an unlocked interactive desktop.' }
    }
    dotnet run --project "$root/tests/KoTrans.WpfTests/KoTrans.WpfTests.csproj" -c Release -- @testArgs
    $result = $LASTEXITCODE
    if ($result -eq 3) { Write-Warning 'Desktop environment unavailable; this is not a product test pass.'; exit 3 }
    if ($result -ne 0) { throw "Desktop tests failed (exit $result)." }
} finally {
    Write-Output ('Desktop: build + run {0:F2}s' -f $clock.Elapsed.TotalSeconds)
}
