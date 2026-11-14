param([Parameter(Mandatory)][string]$NMeCabSourceArchive, [Parameter(Mandatory)][string]$IpaDicSourceArchive)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$parent = Join-Path ([IO.Path]::GetTempPath()) 'KoTrans2.PackageTests'
$fixture = Join-Path $parent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$checks = 0
function Accept { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $fixture; $script:checks++ }
function Reject([string]$message) {
    $rejected = $false
    try { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $fixture | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "Package validator accepted: $message" }; $script:checks++
}
try {
    # Actual reviewed models/dictionary/notices, synthetic executable headers. Never executed/published.
    foreach ($folder in @('det','en','ja')) {
        $dest = Join-Path $fixture "models/$folder"; New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($name in @('model.onnx','manifest.json','conversion.json','dictionary.txt')) {
            $source = Join-Path $repo "models/$folder/$name"; if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $dest }
        }
    }
    Copy-Item -LiteralPath "$repo/src/KoTrans.App/bin/Release/net10.0-windows/IpaDic" -Destination "$fixture/IpaDic" -Recurse
    . "$PSScriptRoot/PortableRelease.Common.ps1"
    Copy-ReleaseLicenses "$repo/docs/licenses" "$fixture/licenses"
    Copy-ReleaseHelp "$repo/docs/help" "$fixture/help"
    $helpFiles = @(Get-ReleaseHelpPaths "$repo/docs/help")
    if (@(Get-ChildItem -LiteralPath "$fixture/help" -Recurse -File).Count -ne $helpFiles.Count) {
        throw 'The fixture did not contain exactly the public help files.'
    }
    Copy-Item -LiteralPath $NMeCabSourceArchive -Destination "$fixture/licenses/NMeCab-v0.10.2-source.zip"
    Copy-Item -LiteralPath "$repo/LICENSE", "$repo/THIRD-PARTY-NOTICES.md" -Destination $fixture
    Copy-Item -LiteralPath $IpaDicSourceArchive -Destination "$fixture/licenses/NMeCab-v0.10.0-source.zip"
    foreach ($file in @('KoTrans2.exe','KoTrans2.dll','KoTrans2.deps.json','KoTrans2.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll','PresentationCore.dll','wpfgfx_cor3.dll','Microsoft.ML.OnnxRuntime.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','OpenCvSharp.dll','OpenCvSharpExtern.dll','LibNMeCab.dll','LibNMeCab.IpaDicBin.dll','USER-GUIDE.md')) { [IO.File]::WriteAllText((Join-Path $fixture $file), 'SYNTHETIC TEST FILE - NOT EXECUTABLE') }
    foreach ($name in @('KoTrans2.exe','coreclr.dll','onnxruntime.dll','OpenCvSharpExtern.dll')) {
        $file = [IO.File]::Create((Join-Path $fixture $name)); $writer = [IO.BinaryWriter]::new($file)
        try { $writer.Write([uint16]0x5A4D); $file.Position = 0x3c; $writer.Write([int]128); $file.Position = 128; $writer.Write([uint32]0x4550); $writer.Write([uint16]0x8664) } finally { $writer.Dispose() }
    }
    $runtime = '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.14"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.14"}]}}'
    [IO.File]::WriteAllText("$fixture/KoTrans2.runtimeconfig.json", $runtime)
    [IO.File]::WriteAllText("$fixture/KoTrans2.deps.json", '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"}}')
    foreach ($folder in @('det','ja')) {
        $dest = Join-Path $fixture "models/fast/$folder"; New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($name in @('model.onnx','manifest.json','dictionary.txt')) {
            $source = Join-Path $repo "models/fast/$folder/$name"; if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $dest }
        }
        $record = if ($folder -eq 'det') { 'conversion.json' } else { 'source.json' }
        Copy-Item -LiteralPath "$repo/docs/model-manifests/fast/$folder/$record" -Destination $dest
    }
    foreach ($folder in @('det','rec')) {
        $dest = Join-Path $fixture "models/paddle/v6-medium/$folder"; New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($name in @('model.onnx','manifest.json')) { Copy-Item -LiteralPath "$repo/models/paddle/v6-medium/$folder/$name" -Destination $dest }
        if ($folder -eq 'rec') { Copy-Item -LiteralPath "$repo/models/paddle/v6-medium/rec/dictionary.txt" -Destination $dest }
    }
    Copy-Item -LiteralPath "$repo/docs/model-manifests/paddle/v6-medium/source.json" -Destination "$fixture/models/paddle/v6-medium/source.json"
    Accept
    foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md','licenses/Original-KO-Trans-MIT.txt','licenses/Apache-2.0.txt','licenses/opencv/OpenCV-LICENSE.txt','licenses/opencv/OpenCvSharp-LICENSE.txt','licenses/NMeCab-v0.10.2-source.zip','licenses/NMeCab-v0.10.0-source.zip')) {
        $file = Join-Path $fixture $name
        $bytes = [IO.File]::ReadAllBytes($file)
        [IO.File]::Delete($file)
        Reject "missing $name"
        [IO.File]::WriteAllText($file, 'altered legal material')
        Reject "altered $name"
        [IO.File]::WriteAllBytes($file, $bytes)
    }
    [IO.File]::WriteAllText("$fixture/private.mp3", 'synthetic')
    Reject 'private audio'
    [IO.File]::Delete("$fixture/private.mp3")
    $reference = "$fixture/licenses/opencv/unlisted-notice.txt"
    [IO.File]::WriteAllText($reference, 'unapproved documentation')
    Reject 'unlisted license payload'
    [IO.File]::Delete($reference)
    $elf = "$fixture/renamed-native.dll"
    [IO.File]::WriteAllBytes($elf, [byte[]]@(0x7f, 0x45, 0x4c, 0x46))
    Reject 'ELF payload with a Windows file extension'
    [IO.File]::Delete($elf)
    $helpEntry = "$fixture/help/index.html"
    $helpBytes = [IO.File]::ReadAllBytes($helpEntry)
    [IO.File]::WriteAllText($helpEntry, 'altered help')
    Reject 'modified help page'
    [IO.File]::WriteAllBytes($helpEntry, $helpBytes)
    $missingImage = Join-Path "$fixture/help" ($helpFiles | Where-Object { $_ -like 'images/*' } | Select-Object -First 1)
    $imageBytes = [IO.File]::ReadAllBytes($missingImage)
    [IO.File]::Delete($missingImage)
    Reject 'missing help image'
    [IO.File]::WriteAllBytes($missingImage, $imageBytes)
    $internalReport = "$fixture/help/review-report.md"
    [IO.File]::WriteAllText($internalReport, 'internal help file')
    Reject 'unexpected help report'
    [IO.File]::Delete($internalReport)
    [IO.File]::WriteAllText("$fixture/KoTrans2.runtimeconfig.json", '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.14"}}}')
    Reject 'framework dependent runtime'; [IO.File]::WriteAllText("$fixture/KoTrans2.runtimeconfig.json", $runtime)
    [IO.File]::WriteAllText("$fixture/models/craft.onnx", 'unknown'); Reject 'unreviewed model'; Remove-Item -LiteralPath "$fixture/models/craft.onnx"
    [IO.File]::WriteAllText("$fixture/settings.json", 'synthetic'); Reject 'user settings'; Remove-Item -LiteralPath "$fixture/settings.json"
    $native = [IO.File]::ReadAllBytes("$fixture/onnxruntime.dll"); $bad = [byte[]]$native.Clone(); $bad[132] = 0x4c; $bad[133] = 1; [IO.File]::WriteAllBytes("$fixture/onnxruntime.dll", $bad)
    Reject 'x86 native DLL'; [IO.File]::WriteAllBytes("$fixture/onnxruntime.dll", $native)
    $manifest = [IO.File]::ReadAllText("$fixture/models/det/manifest.json")
    [IO.File]::WriteAllText("$fixture/models/det/manifest.json", $manifest.Replace('Apache-2.0','Unknown')); Reject 'unknown license'; [IO.File]::WriteAllText("$fixture/models/det/manifest.json", $manifest)
    $dict = [IO.File]::ReadAllBytes("$fixture/IpaDic/char.bin"); [IO.File]::WriteAllBytes("$fixture/IpaDic/char.bin", [byte[]]@(1,2,3)); Reject 'damaged IPA dictionary'; [IO.File]::WriteAllBytes("$fixture/IpaDic/char.bin", $dict)
    Remove-Item -LiteralPath "$fixture/hostfxr.dll"; Reject 'missing runtime'; [IO.File]::WriteAllText("$fixture/hostfxr.dll", 'SYNTHETIC')
    Accept
    $savedFast = [IO.File]::ReadAllText("$fixture/models/fast/ja/manifest.json")
    [IO.File]::WriteAllText("$fixture/models/fast/ja/manifest.json", $savedFast.Replace('Apache-2.0','Unknown')); Reject 'unknown fast license'
    [IO.File]::WriteAllText("$fixture/models/fast/ja/manifest.json", $savedFast)
    Remove-Item -LiteralPath "$fixture/models/fast/ja/model.onnx"; Reject 'incomplete fast set'
    Copy-Item -LiteralPath "$repo/models/fast/ja/model.onnx" -Destination "$fixture/models/fast/ja/model.onnx"
    $v6Manifest = [IO.File]::ReadAllText("$fixture/models/paddle/v6-medium/rec/manifest.json")
    [IO.File]::WriteAllText("$fixture/models/paddle/v6-medium/rec/manifest.json", $v6Manifest.Replace('Apache-2.0','Unknown')); Reject 'unreviewed v6 license'
    [IO.File]::WriteAllText("$fixture/models/paddle/v6-medium/rec/manifest.json", $v6Manifest)
    $v6Dictionary = [IO.File]::ReadAllBytes("$fixture/models/paddle/v6-medium/rec/dictionary.txt")
    [IO.File]::WriteAllText("$fixture/models/paddle/v6-medium/rec/dictionary.txt", 'damaged'); Reject 'damaged v6 dictionary'
    [IO.File]::WriteAllBytes("$fixture/models/paddle/v6-medium/rec/dictionary.txt", $v6Dictionary)
    [IO.File]::WriteAllText("$fixture/models/unbundled-meiki.onnx", 'not release-approved'); Reject 'unbundled local model'
    & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path "$fixture/models" -ModelsOnly -AllowUnbundledSourceModels; $checks++
    $sourceFlagRejected = $false
    try { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $fixture -AllowUnbundledSourceModels } catch { $sourceFlagRejected = $true }
    if (!$sourceFlagRejected) { throw 'Source-only extra-model allowance bypassed package validation.' }; $checks++
    Remove-Item -LiteralPath "$fixture/models/unbundled-meiki.onnx"
    $retired = "$fixture/models/meiki/0.3.5"
    New-Item -ItemType Directory -Path $retired -Force | Out-Null
    foreach ($name in @('meiki.text.detect.v0.1.960x544.onnx','meiki.text.detect.v0.1.320x192.onnx','meiki.text.rec.v0.960x32.onnx','meiki.text.rec.v0.vertical.32x480.onnx','manifest.json')) {
        $path = Join-Path $retired $name
        [IO.File]::WriteAllText($path, 'retired file')
        Reject "retired OCR file $name"
        [IO.File]::Delete($path)
    }
    [IO.Directory]::Delete($retired)
    [IO.Directory]::Delete("$fixture/models/meiki")
    [IO.File]::WriteAllText("$fixture/hybrid-manifest.json", '{}')
    Reject 'retired hybrid manifest outside models'
    [IO.File]::Delete("$fixture/hybrid-manifest.json")
    [IO.File]::WriteAllText("$fixture/extra.onnx", 'unexpected model')
    Reject 'unknown ONNX outside models'
    [IO.File]::Delete("$fixture/extra.onnx")
    Accept
    Remove-Item -LiteralPath "$fixture/models/paddle/v6-medium/rec/model.onnx"; Reject 'incomplete v6 set'
    Write-Output "PASS: $checks package validator assertions. Synthetic headers only, no Publish/GUI/runtime execution; not a real package smoke test."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath($parent) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
