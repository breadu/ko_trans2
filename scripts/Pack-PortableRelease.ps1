param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,[switch]$LayoutPoc)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/PortableRelease.Common.ps1"
Assert-PortableVersion $Version
$source=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PublishDirectory));$output=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
if($output -eq $source -or $output.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'ZIP output must be outside the publish folder.'}
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $source -Portable -LayoutPoc:$LayoutPoc
$metadata=Test-PortableMetadata $source
if($metadata.version -ne $Version -or $metadata.sourceCommit -ne $SourceCommit){throw 'Version/source arguments differ from published binaries.'}
for($cursor=[IO.DirectoryInfo]::new($output);$null -ne $cursor;$cursor=$cursor.Parent){if($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Output cannot traverse a link/junction.'}}
[IO.Directory]::CreateDirectory($output) | Out-Null
$folder="KoTrans2_v$Version";$suffix=if($LayoutPoc){'-layout-poc'}else{''};$zipName="${folder}_win-x64${suffix}.zip";$destination=Join-Path $output $zipName
foreach($suffix in @('','.sha256','.release.json')){if(Test-Path -LiteralPath ($destination+$suffix)){throw 'Existing ZIP or sidecar must not be overwritten.'}}
Write-Output "Creating validated ZIP: $destination"
$partial=$destination+'.'+[Guid]::NewGuid().ToString('N')+'.partial'
$inventory=Get-Content -LiteralPath (Join-Path $source 'publish-files.json') -Raw | ConvertFrom-Json
$map=@{}; foreach($file in $inventory.files){$map[$file.path]=$file}
$inventoryFile=Join-Path $source 'publish-files.json'; $map['publish-files.json']=[ordered]@{path='publish-files.json';bytes=(Get-Item -LiteralPath $inventoryFile).Length;sha256=(Get-FileHash -LiteralPath $inventoryFile).Hash}
$names=[string[]]@($map.Keys);[Array]::Sort($names,[StringComparer]::Ordinal);$files=@(foreach($name in $names){$map[$name]})
$stream=[IO.FileStream]::new($partial,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true,[Text.UTF8Encoding]::new($false))
    try{foreach($file in $files){$entry=$archive.CreateEntry("$folder/$($file.path)",[IO.Compression.CompressionLevel]::Optimal);$entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero);$entry.ExternalAttributes=0;$readStream=[IO.File]::OpenRead((Join-Path $source $file.path));$target=$entry.Open();try{$readStream.CopyTo($target)}finally{$target.Dispose();$readStream.Dispose()}}}finally{$archive.Dispose()}
}finally{$stream.Dispose()}
Write-Output "Verifying every compressed entry..."
$archive=[IO.Compression.ZipFile]::OpenRead($partial)
try{
    if($archive.Entries.Count -ne $files.Count){throw 'ZIP entry count differs from validated input.'}
    $expected=@{};foreach($file in $files){$expected["$folder/$($file.path)"]=$file}
    foreach($entry in $archive.Entries){if(!$expected.ContainsKey($entry.FullName)){throw 'Unexpected/duplicate ZIP entry.'};$file=$expected[$entry.FullName];$readStream=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($readStream))}finally{$readStream.Dispose()};if($entry.Length -ne $file.bytes -or $hash -ne $file.sha256){throw "ZIP data differs: $($entry.FullName)"};$expected.Remove($entry.FullName)}
    if($expected.Count){throw 'ZIP is missing input files.'}
}finally{$archive.Dispose()}
$sha=(Get-FileHash -LiteralPath $partial).Hash.ToLowerInvariant()
# Same input bytes, fixed entry timestamps and ordinal order produce the same ZIP on this runtime.
[IO.File]::Move($partial,$destination,$false)
$encoding=[Text.UTF8Encoding]::new($false)
$shaStream=[IO.FileStream]::new($destination+'.sha256',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$bytes=$encoding.GetBytes("$sha  $zipName`n");$shaStream.Write($bytes,0,$bytes.Length)}finally{$shaStream.Dispose()}
$record=[ordered]@{product=$metadata.product;version=$Version;sourceCommit=$SourceCommit;buildUtc=$metadata.buildUtc;architecture='win-x64';package=$zipName;sha256=$sha;files=$files.Count;rootFolder=$folder}
$info=[IO.FileStream]::new($destination+'.release.json',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$bytes=$encoding.GetBytes(($record | ConvertTo-Json));$info.Write($bytes,0,$bytes.Length)}finally{$info.Dispose()}
$record | ConvertTo-Json
Write-Output "Prepared locally, not published: $destination"
