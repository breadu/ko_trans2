param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$NMeCabSourceArchive, [Parameter(Mandatory)][string]$IpaDicSourceArchive, [string]$ModelsDirectory = "$PSScriptRoot/../models", [string]$Version = "", [switch]$LayoutPoc)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$output = [IO.Path]::GetFullPath($OutputDirectory)
# Explicit release preparation only. Never called by FAST, BuildOnly or DEV-RUN.
if (Test-Path -LiteralPath $output) { throw 'Use a new isolated output directory. Existing folders are never overwritten or removed.' }
if ($output -eq $root -or $output.StartsWith("$root\src\", [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith("$root\models\", [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be separate from development binaries and model sources.' }
if (Get-Process KoTrans2 -ErrorAction SilentlyContinue) { throw 'Close KO Trans 2 normally before explicit release preparation.' }
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $ModelsDirectory -ModelsOnly -AllowUnbundledSourceModels
$source = Get-Content -LiteralPath "$root/docs/licenses/NMeCab-source.json" -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $NMeCabSourceArchive -Algorithm SHA256).Hash -ne $source.Sha256) { throw 'NMeCab corresponding source archive differs from the reviewed upstream tag.' }
$ipaSource = Get-Content -LiteralPath "$root/docs/licenses/IpaDicBin-source.json" -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $IpaDicSourceArchive -Algorithm SHA256).Hash -ne $ipaSource.Sha256) { throw 'IPA corresponding source archive differs from the reviewed upstream tag.' }
. "$PSScriptRoot/PortableRelease.Common.ps1"
# The app project is the sole public version source; -Version must not silently override it.
[xml]$appProject = Get-Content -LiteralPath "$root/src/KoTrans.App/KoTrans.App.csproj" -Raw
$declaredVersion = [string]$appProject.Project.PropertyGroup.Version
Assert-PortableVersion $declaredVersion
if ($Version -and $Version -cne $declaredVersion) {
    throw "Requested version differs from KoTrans.App.csproj. Change <Version> there first."
}
$sourceCommit = (git -C $root rev-parse HEAD).Trim()
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot determine source commit.' }
git -C $root diff --quiet; if ($LASTEXITCODE -ne 0) { throw 'Commit source changes before preparing a public package.' }
git -C $root diff --cached --quiet; if ($LASTEXITCODE -ne 0) { throw 'Commit staged changes before preparing a public package.' }
$profile = if ($LayoutPoc) { 'FolderWinX64LayoutPoc' } else { 'FolderWinX64' }
$publish = @('publish', "$root/src/KoTrans.App/KoTrans.App.csproj", '-c', 'Release', "-p:PublishProfile=$profile", '-p:DebugType=none', '-p:DebugSymbols=false', "-p:SourceRevisionId=$sourceCommit", '-o', $output, '--nologo')
if ($Version) { Assert-PortableVersion $Version; $publish += "-p:Version=$Version" }
dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Partial output was retained for inspection.' }

# Verify both consumers use the DLL produced by the documented source build.
$expectedNative = (Get-FileHash -LiteralPath "$root/artifacts/opencv/win-x64/OpenCvSharpExtern.dll").Hash
foreach ($relative in @('OpenCvSharpExtern.dll', 'ocr-worker/OpenCvSharpExtern.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $output $relative)).Hash -ne $expectedNative) {
        throw "Unexpected OpenCV native library: $relative"
    }
}

# LLamaSharp.Backend.Vulkan.Linux 0.30.0 copies these files even for win-x64.
# Remove only its reviewed Linux Vulkan assets from the new Windows publish output.
foreach ($prefix in @('runtimes','local-worker/runtimes')) {
    foreach ($name in @('libggml-base.so','libggml-vulkan.so','libggml.so','libllama.so','libmtmd.so')) {
        $asset = Join-Path $output "$prefix/linux-x64/native/vulkan/$name"
        if (Test-Path -LiteralPath $asset -PathType Leaf) { Remove-Item -LiteralPath $asset }
    }
}

foreach ($folder in @('det','en','ja')) {
    $dest = Join-Path $output "models/$folder"; New-Item -ItemType Directory -Path $dest -Force | Out-Null
    $files = @('model.onnx','manifest.json','conversion.json'); if ($folder -ne 'det') { $files += 'dictionary.txt' }
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $ModelsDirectory "$folder/$file") -Destination $dest }
}
if (Test-Path -LiteralPath (Join-Path $ModelsDirectory 'fast')) {
    foreach ($folder in @('det','ja')) {
        $dest = Join-Path $output "models/fast/$folder"; New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($file in @('model.onnx','manifest.json')) { Copy-Item -LiteralPath (Join-Path $ModelsDirectory "fast/$folder/$file") -Destination $dest }
        if ($folder -eq 'ja') { Copy-Item -LiteralPath (Join-Path $ModelsDirectory 'fast/ja/dictionary.txt') -Destination $dest }
        $record = if ($folder -eq 'det') { 'conversion.json' } else { 'source.json' }
        Copy-Item -LiteralPath "$root/docs/model-manifests/fast/$folder/$record" -Destination $dest
    }
}
if (Test-Path -LiteralPath (Join-Path $ModelsDirectory 'paddle/v6-medium')) {
    foreach ($folder in @('det','rec')) {
        $dest = Join-Path $output "models/paddle/v6-medium/$folder"; New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($file in @('model.onnx','manifest.json')) { Copy-Item -LiteralPath (Join-Path $ModelsDirectory "paddle/v6-medium/$folder/$file") -Destination $dest }
        if ($folder -eq 'rec') { Copy-Item -LiteralPath (Join-Path $ModelsDirectory 'paddle/v6-medium/rec/dictionary.txt') -Destination $dest }
    }
    Copy-Item -LiteralPath "$root/docs/model-manifests/paddle/v6-medium/source.json" -Destination (Join-Path $output 'models/paddle/v6-medium/source.json')
}
Copy-Item -LiteralPath "$root/LICENSE", "$root/THIRD-PARTY-NOTICES.md" -Destination $output
Copy-Item -LiteralPath "$root/docs/user-guide.md" -Destination (Join-Path $output 'USER-GUIDE.md')
Copy-ReleaseHelp "$root/docs/help" (Join-Path $output 'help')
Copy-ReleaseLicenses "$root/docs/licenses" (Join-Path $output 'licenses')
Copy-Item -LiteralPath $NMeCabSourceArchive -Destination (Join-Path $output "licenses/$($source.File)")
Copy-Item -LiteralPath $IpaDicSourceArchive -Destination (Join-Path $output "licenses/$($ipaSource.File)")
Copy-Item -LiteralPath "$root/docs/PORTABLE-README.txt" -Destination (Join-Path $output 'PORTABLE-README.txt')
if ((git -C $root rev-parse HEAD).Trim() -ne $sourceCommit) { throw 'Source commit changed while publishing.' }
git -C $root diff --quiet; if ($LASTEXITCODE -ne 0) { throw 'Source files changed while publishing.' }
git -C $root diff --cached --quiet; if ($LASTEXITCODE -ne 0) { throw 'Staged source changed while publishing.' }
$actual = (Get-Item -LiteralPath (Join-Path $output 'KoTrans2.exe')).VersionInfo.ProductVersion
$publicVersion = $actual.Split('+')[0]; Assert-PortableVersion $publicVersion
if ($actual -ne ($publicVersion+'+'+$sourceCommit) -or $publicVersion -cne $declaredVersion) { throw 'Published executable version/source differs from the app project.' }
[ordered]@{product='KO Trans 2';version=$publicVersion;sourceCommit=$sourceCommit;buildUtc=[DateTimeOffset]::UtcNow.ToString('o');architecture='win-x64'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
if ($LayoutPoc) {
    $recordPath = Join-Path $output 'release.json'
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -AsHashtable
    $record.layout = 'managed-bundle-poc'
    $record | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding utf8
}
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $output -LayoutPoc:$LayoutPoc
[ordered]@{format=1;files=@(Get-PackageFiles $output)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'publish-files.json') -Encoding utf8
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $output -Portable -LayoutPoc:$LayoutPoc
Write-Output "Prepared for manual release checks: $output. No GUI, installation, Git operation or main merge was performed."
