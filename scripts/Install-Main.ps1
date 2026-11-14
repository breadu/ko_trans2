# Explicit user-approved installation only. Never called by build, FAST or DEV-RUN.
param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$package = [IO.Path]::GetFullPath($PackageDirectory)
$release = Join-Path $root 'release'
if (Get-Process KoTrans2 -ErrorAction SilentlyContinue) { throw 'Close KO Trans 2 normally before installation. No process was stopped.' }
if ($package -eq $release -or $package.StartsWith($release + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a separate validated package directory.' }
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $package
foreach ($path in @($root, $release, (Join-Path $root 'work'))) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Install paths must not be links/junctions.' }
}
if ((Test-Path -LiteralPath $release) -and !(Test-Path -LiteralPath "$release/install.json" -PathType Leaf)) { throw 'Existing release has no KO Trans 2 install marker. Refusing to replace unknown files.' }
$stage = Join-Path $root ("work/install-stage-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $package -Force | Copy-Item -Destination $stage -Recurse
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $stage
@{ Product='KO Trans 2'; SourceCommit=$SourceCommit; InstalledUtc=[DateTimeOffset]::UtcNow.ToString('o'); ExecutableSha256=(Get-FileHash -LiteralPath "$stage/KoTrans2.exe" -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content -LiteralPath "$stage/install.json" -Encoding utf8
$backup = $null
try {
    if (Test-Path -LiteralPath $release) { $backup = Join-Path $root ("work/install-backup-" + [Guid]::NewGuid().ToString('N')) }
    foreach ($path in @($release, $stage, $backup)) {
        if ($path -and !([IO.Path]::GetFullPath($path)).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Install move paths must stay inside this checkout.' }
    }
    if (Test-Path -LiteralPath $release) {
        Move-Item -LiteralPath $release -Destination $backup
    }
    Move-Item -LiteralPath $stage -Destination $release
} catch {
    if ($backup -and !(Test-Path -LiteralPath $release)) { Move-Item -LiteralPath $backup -Destination $release }
    throw
}
Write-Output "Installed: $release/KoTrans2.exe; commit=$SourceCommit; previous=$backup. Run.cmd launches main; Dev-Run.cmd remains separate. No settings, old KO Trans, shortcuts or Git were changed."
