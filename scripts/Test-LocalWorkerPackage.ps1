param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/PortableRelease.Common.ps1"
$root=[IO.Path]::GetFullPath($Path)
$worker=Join-Path $root 'local-worker'
function Need([string]$relative) { $p=Join-Path $worker $relative; if(!(Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0){throw "Missing local worker file: $relative"}; return $p }
foreach($file in @('KoTrans.LocalWorker.exe','KoTrans.LocalWorker.dll','KoTrans.LocalWorker.deps.json','KoTrans.LocalWorker.runtimeconfig.json','LLamaSharp.dll','KoTrans.Core.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','local-worker-runtime.json')){ $null=Need $file }
foreach($file in @('KoTrans.LocalWorker.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll')){ Assert-PackageX64 (Need $file) }
$runtime=Get-Content -LiteralPath (Need 'KoTrans.LocalWorker.runtimeconfig.json') -Raw | ConvertFrom-Json
if($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or !$runtime.runtimeOptions.includedFrameworks -or @($runtime.runtimeOptions.includedFrameworks | Where-Object version -NE '10.0.14').Count){throw 'Local worker is not the reviewed self-contained runtime.'}
$deps=Get-Content -LiteralPath (Need 'KoTrans.LocalWorker.deps.json') -Raw | ConvertFrom-Json
if($deps.libraries.PSObject.Properties.Name -notcontains 'LLamaSharp/0.30.0'){throw 'Unexpected LLamaSharp version.'}
$native=Get-Content -LiteralPath "$PSScriptRoot/../docs/local-worker-native-manifest.json" -Raw | ConvertFrom-Json
foreach($file in $native.files){if($file.file -notmatch '^runtimes/win-x64/native/(noavx|avx|avx2|avx512|vulkan)/[a-z0-9-]+\.dll$'){throw 'Unsafe native manifest path.'};$p=Need $file.file;if((Get-Item -LiteralPath $p).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $p).Hash -ne $file.sha256){throw "Local backend differs from pinned 0.30.0: $($file.file)"}}
$crt=Get-Content -LiteralPath (Need 'local-worker-runtime.json') -Raw | ConvertFrom-Json
foreach($file in $crt){if($file.name -notmatch '^[a-zA-Z0-9_]+\.dll$'){throw 'Unsafe runtime manifest path.'};$p=Need $file.name;if((Get-FileHash -LiteralPath $p).Hash -ne $file.sha256){throw 'App-local runtime hash mismatch.'};$signature=Get-AuthenticodeSignature -LiteralPath $p;if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw 'App-local Microsoft signature verification failed.'}}
foreach($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')){if((Get-FileHash -LiteralPath (Need $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root $name)).Hash){throw 'Host and worker C++ runtime differ.'}}
foreach($file in @('LLamaSharp-MIT.txt','llama.cpp-MIT.txt','Hy-MT2-LICENSE.txt','CommunityToolkit.HighPerformance-8.4.2-License.md','CommunityToolkit.HighPerformance-8.4.2-ThirdPartyNotices.txt','Reactive-MIT.txt','packages.json','sources.json')){if(!(Test-Path -LiteralPath (Join-Path $root "licenses/local-ai/$file") -PathType Leaf)){throw "Missing local AI notice: $file"}}
Write-Output "PASS: local worker self-contained 10.0.14, pinned native $($native.files.Count) DLLs, signed app-local C++ runtime and notices."
