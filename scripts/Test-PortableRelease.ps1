param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$LayoutPoc)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/PortableRelease.Common.ps1"
$source=[IO.Path]::GetFullPath($PublishDirectory);$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new test output directory.'}
if($output.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Test output cannot be inside the input package.'}
$metadata=Test-PortableMetadata $source
$packageArguments=@{Version=$metadata.version;SourceCommit=$metadata.sourceCommit;LayoutPoc=$LayoutPoc}
$checks=0
function Check([bool]$value,[string]$message){if(!$value){throw $message};$script:checks++}
function Reject([scriptblock]$action){$rejected=$false;try{& $action | Out-Null}catch{$rejected=$true};Check $rejected 'Unsafe package/overwrite accepted.'}
& "$PSScriptRoot/Pack-PortableRelease.ps1" -PublishDirectory $source -OutputDirectory "$output/first" @packageArguments
& "$PSScriptRoot/Pack-PortableRelease.ps1" -PublishDirectory $source -OutputDirectory "$output/repeat" @packageArguments
$suffix=if($LayoutPoc){'-layout-poc'}else{''};$name="KoTrans2_v$($metadata.version)_win-x64${suffix}.zip";$zip=Join-Path "$output/first" $name
$hash=(Get-FileHash -LiteralPath $zip).Hash
Check ($hash -eq (Get-FileHash -LiteralPath (Join-Path "$output/repeat" $name)).Hash) 'Same input produced different ZIP bytes.'
Check ((Get-Content -LiteralPath ($zip+'.sha256') -Raw).StartsWith($hash,[StringComparison]::OrdinalIgnoreCase)) 'SHA sidecar mismatch.'
$sidecar=Get-Content -LiteralPath ($zip+'.release.json') -Raw | ConvertFrom-Json
Check ($sidecar.sha256 -eq $hash -and $sidecar.version -eq $metadata.version -and $sidecar.sourceCommit -eq $metadata.sourceCommit) 'Public sidecar version/source/hash mismatch.'
Reject { & "$PSScriptRoot/Pack-PortableRelease.ps1" -PublishDirectory $source -OutputDirectory "$output/first" @packageArguments }
Check ((Get-FileHash -LiteralPath $zip).Hash -eq $hash) 'Overwrite attempt changed the original ZIP.'
Reject { & "$PSScriptRoot/Pack-PortableRelease.ps1" -PublishDirectory ($source+'\') -OutputDirectory (Join-Path $source 'must-not-create') @packageArguments }
Reject { Assert-PortableVersion '../../escape' }
[IO.Compression.ZipFile]::ExtractToDirectory($zip,"$output/extracted")
$extracted=Join-Path "$output/extracted" "KoTrans2_v$($metadata.version)"
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable -LayoutPoc:$LayoutPoc
if ($LayoutPoc) {
    Reject { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable }
    $loose = Join-Path $extracted 'LibNMeCab.dll'
    [IO.File]::Move($loose, $loose+'.held')
    try { Reject { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable -LayoutPoc } }
    finally { [IO.File]::Move($loose+'.held', $loose) }
}
Check ((Test-Path -LiteralPath "$extracted/KoTrans2.exe") -and (Test-Path -LiteralPath "$extracted/local-worker/KoTrans.LocalWorker.exe") -and (Test-Path -LiteralPath "$extracted/local-worker/runtimes/win-x64/native/vulkan/ggml-vulkan.dll")) 'Extracted folder lacks native/worker files.'
# Modify only our extracted fixture. Never modify the validated input folder.
foreach($name in @('settings.json','synthetic.key','private.log','fixture.cs','model.gguf')){
    $file=Join-Path $extracted $name;[IO.File]::WriteAllText($file,'SYNTHETIC DATA ONLY')
    Reject { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable -LayoutPoc:$LayoutPoc }
    [IO.File]::Delete($file)
}
$model=Join-Path $extracted 'local-worker/runtimes/win-x64/native/avx2/llama.dll'
$bytes=[IO.File]::ReadAllBytes($model);[IO.File]::WriteAllText($model,'damaged')
Reject { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable -LayoutPoc:$LayoutPoc };[IO.File]::WriteAllBytes($model,$bytes)
$worker=Join-Path $extracted 'local-worker/KoTrans.LocalWorker.exe';[IO.File]::Move($worker,$worker+'.held')
Reject { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable -LayoutPoc:$LayoutPoc };[IO.File]::Move($worker+'.held',$worker)
$record=Join-Path $extracted 'release.json';$before=[IO.File]::ReadAllBytes($record);$bad=Get-Content -LiteralPath $record -Raw | ConvertFrom-Json;$bad.version='999.0.0';$bad | ConvertTo-Json | Set-Content -LiteralPath $record
Reject { & "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable -LayoutPoc:$LayoutPoc };[IO.File]::WriteAllBytes($record,$before)
& "$PSScriptRoot/Test-ReleasePackage.ps1" -Path $extracted -Portable -LayoutPoc:$LayoutPoc
Write-Output "PASS: $checks portable ZIP/reproducibility/extraction/tamper/overwrite assertions. No GUI, user settings, keys or downloaded GGUF accessed."
