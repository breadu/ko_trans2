# Explicit, model/GUI/network-free script regression. Never runs real release preparation.
$ErrorActionPreference = 'Stop'
$sourceWork = Join-Path $PSScriptRoot 'Work.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('KoTrans2.ReleaseGuard.' + [guid]::NewGuid().ToString('N'))
$fixtureScripts = Join-Path $temp 'scripts'
New-Item -ItemType Directory -Path $fixtureScripts -Force | Out-Null
try {
    Copy-Item -LiteralPath $sourceWork -Destination (Join-Path $fixtureScripts 'Work.ps1')
    Set-Content -LiteralPath (Join-Path $fixtureScripts 'Test-Fast.ps1') -Value 'param($Suite); exit 0'
    Set-Content -LiteralPath (Join-Path $fixtureScripts 'Publish-Release.ps1') -Value 'param($OutputDirectory, $NMeCabSourceArchive, $IpaDicSourceArchive); Set-Content -LiteralPath (Join-Path $PSScriptRoot "published.marker") -Value "stub only"'
    # Functions shadow these commands only in this test script's scope.
    function dotnet { $global:LASTEXITCODE = 0 }
    function Get-Process { param($Name, $ErrorAction) }
    foreach ($code in @(0, 1, 3)) {
        $marker = Join-Path $fixtureScripts 'published.marker'
        Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
        Set-Content -LiteralPath (Join-Path $fixtureScripts 'Test-Desktop.ps1') -Value ('param($Suite); exit ' + $code)
        $failed = $false
        try {
            & (Join-Path $fixtureScripts 'Work.ps1') -Action MainMerge -Desktop -ReleaseDirectory (Join-Path $temp 'unused') -NMeCabSourceArchive 'synthetic-unused.zip' -IpaDicSourceArchive 'synthetic-ipa-unused.zip'
        } catch { $failed = $true }
        if ($code -eq 0) {
            if ($failed -or !(Test-Path -LiteralPath $marker)) { throw 'Successful desktop stub did not reach publish stub.' }
        } elseif (!$failed -or (Test-Path -LiteralPath $marker)) {
            throw "Desktop exit $code failed to block publish."
        }
    }
    Write-Output 'PASS: Desktop exit 0/1/3 guard (stub scripts only; no build, GUI, Publish or Git).'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
