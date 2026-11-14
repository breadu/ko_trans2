$ErrorActionPreference = 'Stop'
function Get-ReleaseLicensePaths([string]$Source) {
    $list = @(Get-Content -LiteralPath (Join-Path (Split-Path $Source -Parent) 'release-license-files.txt'))
    if (!$list.Count -or @($list | Sort-Object -Unique).Count -ne $list.Count) { throw 'Invalid release license inventory.' }
    foreach ($name in $list) {
        if ($name -cnotmatch '^(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-][A-Za-z0-9_.-]*$') { throw "Invalid release license path: $name" }
        $file = Get-Item -LiteralPath (Join-Path $Source $name)
        if ($file.PSIsContainer -or !$file.Length -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Invalid release license source: $name"
        }
        $name
    }
}

function Copy-ReleaseLicenses([string]$Source, [string]$Destination) {
    foreach ($name in @(Get-ReleaseLicensePaths $Source)) {
        $target = Join-Path $Destination $name
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination $target
    }
}

function Get-ReleaseHelpPaths([string]$Source) {
    $manifest = Get-Content -LiteralPath (Join-Path $Source 'screenshots-manifest.json') -Raw | ConvertFrom-Json
    $images = @($manifest.captures | ForEach-Object { $_.file })
    if (!$images.Count -or @($images | Sort-Object -Unique).Count -ne $images.Count -or
        @($images | Where-Object { $_ -cnotmatch '^images/[a-z0-9-]+\.png$' }).Count) {
        throw 'Invalid release help image inventory.'
    }
    foreach ($name in (@('index.html','quick-start.html','features.html','troubleshooting.html',
                         'css/style.css','js/help.js','js/search-index.js') + $images)) {
        $file = Get-Item -LiteralPath (Join-Path $Source $name) -ErrorAction Stop
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Invalid release help source: $name"
        }
        $name
    }
}

function Copy-ReleaseHelp([string]$Source, [string]$Destination) {
    foreach ($name in @(Get-ReleaseHelpPaths $Source)) {
        $target = Join-Path $Destination $name
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination $target
    }
}

function Assert-ReleaseHelp([string]$Source, [string]$Destination) {
    $paths = @(Get-ReleaseHelpPaths $Source)
    $files = @(Get-ChildItem -LiteralPath $Destination -File -Recurse -Force)
    if ($files.Count -ne $paths.Count) { throw 'Release help file list differs from the public help inventory.' }
    foreach ($name in $paths) {
        $target = Join-Path $Destination $name
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "Missing release help file: $name" }
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Source $name)).Hash) {
            throw "Release help checksum differs: $name"
        }
    }
}

function Assert-PortableVersion([string]$Version) {
    if ($Version.Length -gt 80 -or $Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Supply an explicit safe SemVer, without build metadata. No public version is chosen automatically.' }
}
function Get-PackageFiles([string]$Root) {
    $names = [string[]]@(Get-ChildItem -LiteralPath $Root -Recurse -Force -File | ForEach-Object { [IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/') })
    [Array]::Sort($names,[StringComparer]::Ordinal)
    foreach ($name in $names) { $path=Join-Path $Root $name; [ordered]@{ path=$name; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } }
}
function Test-PortableMetadata([string]$Root) {
    $metadata = Get-Content -LiteralPath (Join-Path $Root 'release.json') -Raw | ConvertFrom-Json
    Assert-PortableVersion $metadata.version
    if ($metadata.product -ne 'KO Trans 2' -or $metadata.architecture -ne 'win-x64' -or $metadata.sourceCommit -notmatch '^[0-9a-f]{40}$' -or !([DateTimeOffset]::Parse($metadata.buildUtc))) { throw 'Invalid public release metadata.' }
    $version = (Get-Item -LiteralPath (Join-Path $Root 'KoTrans2.exe')).VersionInfo.ProductVersion
    if ($version -ne ($metadata.version+'+'+$metadata.sourceCommit)) { throw 'Public metadata does not match the actual executable version/source.' }
    return $metadata
}

function Assert-PackageX64([string]$Path) {
    $stream=[IO.File]::OpenRead($Path);$reader=[IO.BinaryReader]::new($stream)
    try {
        if($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D){throw "Not a Windows PE: $Path"}
        $stream.Position=0x3c;$offset=$reader.ReadInt32()
        if($offset -lt 64 -or $offset -gt $stream.Length-6){throw 'Invalid PE offset.'}
        $stream.Position=$offset
        if($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664){throw "Not x64: $Path"}
    } finally {$reader.Dispose()}
}
