param([Parameter(Mandatory)][string]$Path, [switch]$ModelsOnly, [switch]$AllowUnbundledSourceModels, [switch]$Portable)
$ErrorActionPreference = 'Stop'
if ($AllowUnbundledSourceModels -and !$ModelsOnly) { throw 'Unbundled local models are allowed only in a development model source, never a release package.' }
$root = [IO.Path]::GetFullPath($Path)
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Package directory does not exist.' }
for ($cursor = [IO.DirectoryInfo]::new($root); $null -ne $cursor; $cursor = $cursor.Parent) {
    if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package path must not traverse a junction/link.' }
}
function Require-File([string]$relative) {
    $file = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Missing/empty package file: $relative" }
    return $file
}
function Assert-Hash([string]$relative, [string]$expected) {
    if ($expected -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid SHA-256: $relative" }
    $file = Require-File $relative
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "SHA-256 mismatch: $relative" }
}
$items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Package must not contain links/junctions.' }
$expected = @{ det = 'PP-OCRv5_server_det'; en = 'en_PP-OCRv5_mobile_rec'; ja = 'PP-OCRv5_server_rec' }
foreach ($folder in @('det','en','ja')) {
    $prefix = if ($ModelsOnly) { $folder } else { "models/$folder" }
    $manifest = Get-Content -LiteralPath (Require-File "$prefix/manifest.json") -Raw | ConvertFrom-Json
    if ($manifest.Name -ne $expected[$folder] -or $manifest.License -ne 'Apache-2.0' -or $manifest.Source -ne "https://huggingface.co/PaddlePaddle/$($expected[$folder])") { throw "Unapproved model/source/license: $folder" }
    if ($manifest.File -ne 'model.onnx' -or $manifest.ChannelOrder -ne 'BGR' -or $manifest.Layout -ne 'NCHW') { throw "Unsupported model contract: $folder" }
    if (($folder -eq 'det' -and ($manifest.Kind -ne 'detection' -or $manifest.Decoder -ne 'DB')) -or ($folder -ne 'det' -and ($manifest.Kind -ne 'recognition' -or $manifest.Decoder -ne 'CTC'))) { throw "Unsupported decoder: $folder" }
    # Pin to reviewed build provenance; changing a manifest alone cannot bless a different binary.
    $reviewed = Get-Content -LiteralPath "$PSScriptRoot/../docs/model-manifests/$folder/manifest.json" -Raw | ConvertFrom-Json
    if (($manifest | ConvertTo-Json -Depth 10 -Compress) -ne ($reviewed | ConvertTo-Json -Depth 10 -Compress)) { throw "Preprocessing manifest differs from reviewed configuration: $folder" }
    if ($manifest.Sha256 -ne $reviewed.Sha256 -or $manifest.DictionarySha256 -ne $reviewed.DictionarySha256) { throw "Model differs from reviewed provenance: $folder" }
    Assert-Hash "$prefix/model.onnx" $manifest.Sha256
    if ($folder -ne 'det') {
        if ($manifest.Dictionary -ne 'dictionary.txt' -or $folder -notin $manifest.Languages) { throw "Wrong dictionary/language: $folder" }
        Assert-Hash "$prefix/dictionary.txt" $manifest.DictionarySha256
    }
    $null = Require-File "$prefix/conversion.json"
}
$modelRoot = if ($ModelsOnly) { $root } else { Join-Path $root 'models' }
$onnx = @(Get-ChildItem -LiteralPath $modelRoot -Filter '*.onnx' -Recurse)
$fastRoot = Join-Path $modelRoot 'fast'
$expectedCount = 3
if (Test-Path -LiteralPath $fastRoot) {
    foreach ($folder in @('det','ja')) {
        $prefix = if ($ModelsOnly) { "fast/$folder" } else { "models/fast/$folder" }
        $manifest = Get-Content -LiteralPath (Require-File "$prefix/manifest.json") -Raw | ConvertFrom-Json
        $reviewed = Get-Content -LiteralPath "$PSScriptRoot/../docs/model-manifests/fast/$folder/manifest.json" -Raw | ConvertFrom-Json
        if (($manifest | ConvertTo-Json -Depth 10 -Compress) -ne ($reviewed | ConvertTo-Json -Depth 10 -Compress)) { throw "Unreviewed fast model contract: $folder" }
        Assert-Hash "$prefix/model.onnx" $reviewed.Sha256
        if ($folder -eq 'ja') { Assert-Hash "$prefix/dictionary.txt" $reviewed.DictionarySha256 }
        # Upstream ONNX recognition model uses source.json, not a fictitious conversion record.
        if (!$ModelsOnly) {
            $record = if ($folder -eq 'det') { 'conversion.json' } else { 'source.json' }
            Assert-Hash "$prefix/$record" (Get-FileHash -LiteralPath "$PSScriptRoot/../docs/model-manifests/fast/$folder/$record" -Algorithm SHA256).Hash
        }
    }
    $expectedCount = 5
}
$v6Root = Join-Path $modelRoot 'paddle/v6-medium'
if (Test-Path -LiteralPath $v6Root) {
    foreach ($folder in @('det','rec')) {
        $prefix = if ($ModelsOnly) { "paddle/v6-medium/$folder" } else { "models/paddle/v6-medium/$folder" }
        $manifest = Get-Content -LiteralPath (Require-File "$prefix/manifest.json") -Raw | ConvertFrom-Json
        $reviewed = Get-Content -LiteralPath "$PSScriptRoot/../docs/model-manifests/paddle/v6-medium/$folder/manifest.json" -Raw | ConvertFrom-Json
        if (($manifest | ConvertTo-Json -Depth 20 -Compress) -ne ($reviewed | ConvertTo-Json -Depth 20 -Compress)) { throw "Unreviewed v6 model contract: $folder" }
        Assert-Hash "$prefix/model.onnx" $reviewed.Sha256
        if ($folder -eq 'rec') { Assert-Hash "$prefix/dictionary.txt" $reviewed.DictionarySha256 }
    }
    if (!$ModelsOnly) { Assert-Hash 'models/paddle/v6-medium/source.json' (Get-FileHash -LiteralPath "$PSScriptRoot/../docs/model-manifests/paddle/v6-medium/source.json" -Algorithm SHA256).Hash }
    $expectedCount += 2
}
if ($expectedCount -ne 7) { throw 'The three supported PaddleOCR configurations require all seven reviewed models.' }
if (!$AllowUnbundledSourceModels -and $onnx.Count -ne 7) { throw 'Only the seven reviewed PaddleOCR models belong in the package.' }
if (!$AllowUnbundledSourceModels -and ($items | Where-Object {
    $_.Name -match 'meiki|^hybrid-manifest\.json$' -or
        (!$ModelsOnly -and $_.Extension -ieq '.onnx' -and !$_.FullName.StartsWith($modelRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
})) { throw 'The package contains retired OCR assets or ONNX files outside the reviewed models directory.' }
if ($ModelsOnly) { Write-Output 'PASS: reviewed model hashes, sources, dictionaries and conversion records (no inference).'; return }
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md')) {
    Assert-Hash $name (Get-FileHash -LiteralPath "$repo/$name" -Algorithm SHA256).Hash
}
. "$PSScriptRoot/PortableRelease.Common.ps1"
$licensePaths = @(Get-ReleaseLicensePaths "$repo/docs/licenses")
foreach ($relative in $licensePaths) {
    Assert-Hash "licenses/$relative" (Get-FileHash -LiteralPath "$repo/docs/licenses/$relative" -Algorithm SHA256).Hash
}
foreach ($file in @('KoTrans2.exe','KoTrans2.dll','KoTrans2.deps.json','KoTrans2.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll','PresentationCore.dll','wpfgfx_cor3.dll','Microsoft.ML.OnnxRuntime.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','OpenCvSharp.dll','OpenCvSharpExtern.dll','LibNMeCab.dll','LibNMeCab.IpaDicBin.dll','IpaDic/sys.dic','IpaDic/unk.dic','IpaDic/char.bin','IpaDic/matrix.bin','IpaDic/COPYING','IpaDic/AUTHORS','USER-GUIDE.md')) { $null = Require-File $file }
$runtime = Get-Content -LiteralPath (Require-File 'KoTrans2.runtimeconfig.json') -Raw | ConvertFrom-Json
$source = Get-Content -LiteralPath "$PSScriptRoot/../docs/licenses/NMeCab-source.json" -Raw | ConvertFrom-Json
Assert-Hash "licenses/$($source.File)" $source.Sha256
$ipaSource = Get-Content -LiteralPath "$repo/docs/licenses/IpaDicBin-source.json" -Raw | ConvertFrom-Json
Assert-Hash "licenses/$($ipaSource.File)" $ipaSource.Sha256
$allowedLicenses = $licensePaths + @($source.File, $ipaSource.File)
$licenseDirectory = Join-Path $root 'licenses'
foreach ($file in Get-ChildItem -LiteralPath $licenseDirectory -Recurse -Force -File) {
    $relative = [IO.Path]::GetRelativePath($licenseDirectory, $file.FullName).Replace('\', '/')
    if ($relative -cnotin $allowedLicenses) { throw "Unexpected release license file: $relative" }
}
foreach ($file in $items | Where-Object { !$_.PSIsContainer }) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    if ($relative -match '(^|/)runtimes/linux[^/]*/' -or $file.Name -match '\.so(?:\.[0-9]+)*$') {
        throw "Linux native asset in Windows package: $($file.Name)"
    }
    $stream = $file.OpenRead()
    try {
        $magic = [byte[]]::new(4)
        if ($stream.Read($magic, 0, 4) -eq 4 -and [BitConverter]::ToUInt32($magic) -eq 0x464C457F) {
            throw "ELF native asset in Windows package: $($file.Name)"
        }
    } finally { $stream.Dispose() }
}

$dictionary = Get-Content -LiteralPath "$PSScriptRoot/../docs/ipa-manifest.json" -Raw | ConvertFrom-Json
foreach ($file in $dictionary.Files) { Assert-Hash "IpaDic/$($file.File)" $file.Sha256 }
if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or !$runtime.runtimeOptions.includedFrameworks) { throw 'Package is not self-contained.' }
if (@($runtime.runtimeOptions.includedFrameworks | Where-Object { $_.version -ne '10.0.14' }).Count -gt 0) { throw 'Runtime version differs from reviewed 10.0.14 license/source notices.' }
$deps = Get-Content -LiteralPath (Require-File 'KoTrans2.deps.json') -Raw | ConvertFrom-Json
if ($deps.runtimeTarget.name -notmatch '/win-x64$') { throw 'Package must target win-x64.' }
Assert-ReleaseHelp "$PSScriptRoot/../docs/help" (Join-Path $root 'help')
foreach ($file in @('KoTrans2.exe','coreclr.dll','onnxruntime.dll','OpenCvSharpExtern.dll')) { Assert-PackageX64 (Require-File $file) }
$forbidden = $items | Where-Object { !$_.PSIsContainer -and ($_.Name -match '^(settings.*\.json|.*\.secret|.*\.key|word\.idx|craft\.onnx|python.*|opencv_videoio_ffmpeg.*)$' -or $_.Extension -in @('.mp3','.py','.pdiparams')) }
if ($forbidden) { throw 'Package contains user data, unknown models, Python or unused FFmpeg runtime.' }
Write-Output 'PASS: self-contained x64 folder structure, native PE architecture, reviewed models, Japanese dictionary and notices. This is not an execution/desktop/clean-PC test or license certification.'

if (Test-Path -LiteralPath (Join-Path $root 'local-worker')) { & "$PSScriptRoot/Test-LocalWorkerPackage.ps1" -Path $root }
elseif ($Portable) { throw 'Portable package must contain its local worker.' }
if (Test-Path -LiteralPath (Join-Path $root 'ocr-worker')) { & "$PSScriptRoot/Test-DirectMlWorkerPackage.ps1" -Path $root }
elseif ($Portable) { throw 'Portable package must contain its isolated DirectML OCR worker.' }
if ($Portable) {
    $opencvHash = (Get-FileHash -LiteralPath (Require-File 'OpenCvSharpExtern.dll')).Hash
    Assert-Hash 'ocr-worker/OpenCvSharpExtern.dll' $opencvHash
    foreach ($relative in @('KoTrans2.deps.json', 'ocr-worker/KoTrans.OcrWorker.deps.json')) {
        if (Select-String -LiteralPath (Require-File $relative) -Pattern '"OpenCvSharp4.runtime.win/' -SimpleMatch -Quiet) {
            throw "Stock OpenCvSharp native package in $relative."
        }
    }
    . "$PSScriptRoot/PortableRelease.Common.ps1"
    $null = Test-PortableMetadata $root
    $null = Require-File 'PORTABLE-README.txt'
    $inventory = Get-Content -LiteralPath (Require-File 'publish-files.json') -Raw | ConvertFrom-Json
    if ($inventory.format -ne 1 -or !$inventory.files) { throw 'Invalid validated publish inventory.' }
    $files = @(Get-PackageFiles $root | Where-Object path -NE 'publish-files.json')
    $expected = @{}; foreach ($file in $inventory.files) {
        if ($file.path -match '(^/|\\|(^|/)\.\.(/|$)|:)' -or $expected.ContainsKey($file.path)) { throw 'Unsafe/duplicate inventory path.' }
        $expected[$file.path] = $file
    }
    if ($files.Count -ne $expected.Count) { throw 'Publish file list has changed since validation.' }
    foreach ($file in $files) {
        if (!$expected.ContainsKey($file.path) -or $expected[$file.path].sha256 -ne $file.sha256 -or $expected[$file.path].bytes -ne $file.bytes) { throw "Unvalidated publish content: $($file.path)" }
        $name = [IO.Path]::GetFileName($file.path); $extension = [IO.Path]::GetExtension($name)
        if ($extension -in @('.gguf','.part','.key','.secret','.log','.tmp','.cs','.csproj','.sln','.slnx','.pdb','.ps1','.cmd','.py') -or $name -match '^(install\.json|settings.*|update-state\.json|startup-metrics\.json|.*fixture.*)$' -or $file.path -match '(^|/)(work|tests|captures|Backups|Diagnostics|voice)(/|$)') { throw "Private/developer data forbidden in portable ZIP: $($file.path)" }
    }
    Write-Output "PASS: public metadata matches executable, exact validated inventory ($($files.Count) files), no user/development/GGUF data."
}
