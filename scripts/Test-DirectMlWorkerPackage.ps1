param([Parameter(Mandatory)][string]$Path, [object]$MainDeps)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Path)
$worker = Join-Path $root 'ocr-worker'
function Require([string]$name) {
    $file = Join-Path $worker $name
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing DirectML worker file: $name" }
    return $file
}
foreach ($file in @('KoTrans.OcrWorker.exe','KoTrans.OcrWorker.dll','KoTrans.Core.dll','KoTrans.OcrWorker.deps.json','KoTrans.OcrWorker.runtimeconfig.json',
    'Microsoft.ML.OnnxRuntime.dll','onnxruntime.dll','onnxruntime_providers_shared.dll','DirectML.dll','OpenCvSharp.dll','OpenCvSharpExtern.dll',
    'coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')) { $null = Require $file }
$runtime = Get-Content -LiteralPath (Require 'KoTrans.OcrWorker.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or !$runtime.runtimeOptions.includedFrameworks -or
    @($runtime.runtimeOptions.includedFrameworks | Where-Object version -NE '10.0.14').Count) { throw 'GPU worker must include the reviewed self-contained runtime, without a user SDK/runtime installation.' }
$deps = Get-Content -LiteralPath (Require 'KoTrans.OcrWorker.deps.json') -Raw | ConvertFrom-Json
if (!$deps.libraries.'Microsoft.ML.OnnxRuntime.DirectML/1.24.4' -or !$deps.libraries.'Microsoft.ML.OnnxRuntime.Managed/1.24.4' -or
    $deps.libraries.'Microsoft.ML.OnnxRuntime/1.31.0') { throw 'GPU worker native/managed runtime versions are not isolated.' }
# Native-only Microsoft.AI.DirectML is a build asset, absent from deps.libraries.
# ORT 1.24.4's Windows PE version is date/commit based, not the NuGet version.
$native = Get-Content -LiteralPath "$PSScriptRoot/../docs/licenses/DirectML-source.json" -Raw | ConvertFrom-Json
foreach ($name in @('onnxruntime.dll','DirectML.dll')) {
    if ((Get-FileHash -LiteralPath (Require $name) -Algorithm SHA256).Hash -ne $native.nativeFiles.$name.sha256) { throw "Unexpected DirectML native binary: $name" }
}
. "$PSScriptRoot/PortableRelease.Common.ps1"
foreach ($file in @('KoTrans.OcrWorker.exe','onnxruntime.dll','DirectML.dll','OpenCvSharpExtern.dll','coreclr.dll')) { Assert-PackageX64 (Require $file) }
if (Test-Path -LiteralPath (Join-Path $worker 'KoTrans.Ocr.dll')) { throw 'CPU OCR assembly must never be copied into the GPU worker.' }
if (Test-Path -LiteralPath (Join-Path $root 'DirectML.dll')) { throw 'GPU runtime must not enter the main process native directory.' }
if (!(Get-Item -LiteralPath (Join-Path $root 'onnxruntime.dll')).VersionInfo.FileVersion.StartsWith('1.31.0')) { throw 'CPU runtime must remain at the existing 1.31.0 version.' }
if (!$MainDeps) { $MainDeps = Get-Content -LiteralPath (Join-Path $root 'KoTrans2.deps.json') -Raw | ConvertFrom-Json }
if (!$mainDeps.libraries.'Microsoft.ML.OnnxRuntime/1.31.0' -or $mainDeps.libraries.'Microsoft.ML.OnnxRuntime.DirectML/1.24.4') { throw 'DirectML runtime leaked into the main application dependency graph.' }
$items = @(Get-ChildItem -LiteralPath $worker -Recurse -File)
if ($items | Where-Object { $_.Name -match 'opencv_videoio_ffmpeg|DirectML\.Debug|^python' -or $_.Extension -in @('.pdb','.lib','.onnx','.gguf','.py') }) { throw 'GPU worker contains unused development/model files.' }
foreach ($name in @('DirectML-1.15.4-LICENSE.txt','DirectML-1.15.4-LICENSE-CODE.txt','DirectML-1.15.4-ThirdPartyNotices.txt','ONNX-Runtime-DirectML-1.24.4-LICENSE.txt','ONNX-Runtime-DirectML-1.24.4-ThirdPartyNotices.txt','DirectML-source.json')) {
    $file = Join-Path $root "licenses/$name"
    if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne (Get-FileHash -LiteralPath "$PSScriptRoot/../docs/licenses/$name").Hash) { throw "Missing or changed DirectML notice: $name" }
}
Write-Output 'PASS: self-contained x64 DirectML worker, pinned independent runtime, app-local CRT and notices. Execution/clean-PC validation is separate.'
