# Explicit local model preparation only; never called by Build/FAST/DEV-RUN. No downloads or Git.
param([Parameter(Mandatory)][string]$DetectorSource,[Parameter(Mandatory)][string]$RecognizerSource,[string]$ModelsDirectory=(Join-Path $PSScriptRoot '../models'))
$ErrorActionPreference='Stop'
$destination=Join-Path ([IO.Path]::GetFullPath($ModelsDirectory)) 'fast'
if(Test-Path -LiteralPath $destination){throw 'Fast model set already exists; inspect it instead of overwriting.'}
foreach($item in @(@{Path=$DetectorSource;Name='PP-OCRv5_mobile_det'},@{Path=$RecognizerSource;Name='PP-OCRv5_mobile_rec'})){
 $manifest=Get-Content -LiteralPath (Join-Path $item.Path 'manifest.json') -Raw|ConvertFrom-Json
 if($manifest.Name -ne $item.Name -or $manifest.License -ne 'Apache-2.0' -or $manifest.Source -notlike 'https://huggingface.co/PaddlePaddle/*'){throw 'Unreviewed model identity/source/license'}
 foreach($entry in @(@{File=$manifest.File;Sha=$manifest.Sha256},@{File=$manifest.Dictionary;Sha=$manifest.DictionarySha256})){
  if(!$entry.Sha){continue}
  if([IO.Path]::GetFileName($entry.File) -ne $entry.File){throw 'Model paths must be simple filenames'}
  if((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $item.Path $entry.File)).Hash -ne $entry.Sha){throw 'Model SHA mismatch'}
 }
}
New-Item -ItemType Directory -Path (Join-Path $destination 'det'),(Join-Path $destination 'ja')|Out-Null
foreach($item in @(@{Source=$DetectorSource;Folder='det';Files=@('manifest.json','model.onnx')},@{Source=$RecognizerSource;Folder='ja';Files=@('manifest.json','model.onnx','dictionary.txt')})){
 foreach($file in $item.Files){Copy-Item -LiteralPath (Join-Path $item.Source $file) -Destination (Join-Path $destination $item.Folder)}
}
Write-Output 'Prepared optional models/fast; precision models unchanged. No model is loaded at UI startup.'
