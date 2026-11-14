# Isolated local Git repositories; no GitHub, GUI, clipboard, .NET or user settings.
$ErrorActionPreference='Stop'
$caseRoot=Join-Path ([IO.Path]::GetTempPath()) ('KoTrans2.SyncRegression.'+[guid]::NewGuid().ToString('N'))
$realGit=(Get-Command git.exe).Source
New-Item -ItemType Directory -Path $caseRoot|Out-Null
function G([string[]]$a){ & $realGit @a | Out-Null;if($LASTEXITCODE){throw 'Fixture git failed'} }
function git { if($args -contains 'get-url'){$global:LASTEXITCODE=0;return 'https://github.com/breadu/ko_trans2.git'};& $realGit @args }
function Set-Clipboard {param($Value)}
$processFixture=[pscustomobject]@{Running=$false;DotnetCalled=$false}
function Get-Process { param($Name,$ErrorAction); if($Name -ne 'KoTrans2'){throw 'Unexpected process query'}; if($processFixture.Running){[pscustomobject]@{Id=1234;ProcessName='KoTrans2'}} }
function dotnet { $processFixture.DotnetCalled=$true; throw 'Unexpected .NET invocation in script-only fixture' }
try{
 $remote=Join-Path $caseRoot 'remote.git';$seed=Join-Path $caseRoot 'seed';$local=Join-Path $caseRoot 'local'
 G @('init','--bare',$remote);G @('init','-b','codex/test-sync',$seed);G @('-C',$seed,'config','user.email','test@example.invalid');G @('-C',$seed,'config','user.name','Test')
 New-Item -ItemType Directory -Path (Join-Path $seed 'scripts') -Force|Out-Null
 'param([switch]$BuildOnly,[switch]$CaptureBuildLog,[switch]$DebugOverlay); Set-Content (Join-Path $PSScriptRoot "../dev.marker") ($DebugOverlay.IsPresent.ToString())'|Set-Content (Join-Path $seed 'scripts/Dev-Run.ps1')
 'param($Suite); Add-Content (Join-Path $PSScriptRoot "../calls.txt") $Suite; if(Test-Path (Join-Path $PSScriptRoot "../fail.marker")){throw "synthetic FAST failure"}'|Set-Content (Join-Path $seed 'scripts/Test-Fast.ps1')
 G @('-C',$seed,'add','scripts');G @('-C',$seed,'commit','-m','A');G @('-C',$seed,'remote','add','origin',$remote);G @('-C',$seed,'push','origin','HEAD')
 G @('clone','--branch','codex/test-sync',$remote,$local)
 New-Item -ItemType Directory -Path (Join-Path $seed 'src/KoTrans.Infrastructure') -Force|Out-Null
 'synthetic'|Set-Content (Join-Path $seed 'src/KoTrans.Infrastructure/TranslationPipeline.cs');G @('-C',$seed,'add','src');G @('-C',$seed,'commit','-m','B');G @('-C',$seed,'push','origin','HEAD')
 # gitignored test markers do not make the fixture checkout dirty.
 '/fail.marker','/calls.txt','/dev.marker','/work/'|Set-Content (Join-Path $local '.git/info/exclude')
 # A synthetic running app must stop sync before pull/FAST/build, preserving retry state.
 $guardJournal=Join-Path $local '.git/kotrans2-fast-pending.json'
 $guardState='{"Version":1,"Pending":["Wpf"],"Unmapped":[]}'
 $guardState | Set-Content -LiteralPath $guardJournal
 $beforeGuard=(& $realGit -C $local rev-parse HEAD | Select-Object -First 1)
 $processFixture.Running=$true
 $message='';try{& "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -FastTest -BuildOnly *> $null}catch{$message=$_.Exception.Message}
 if($message -notmatch '^ENVIRONMENT UNAVAILABLE:'){throw ('Running-app environment check not reported: '+$message)}
 if((& $realGit -C $local rev-parse HEAD | Select-Object -First 1) -ne $beforeGuard -or (Test-Path (Join-Path $local 'calls.txt')) -or (Test-Path (Join-Path $local 'dev.marker')) -or (Get-Content -LiteralPath $guardJournal -Raw).Trim() -ne $guardState){throw 'Running app reached pull/FAST/DEV-RUN or altered retry state'}
 # Direct WPF FAST must also stop before invoking dotnet, independently of Sync-DevRun.
 $message='';try{& "$PSScriptRoot/Test-Fast.ps1" -Suite Wpf *> $null}catch{$message=$_.Exception.Message}
 if($message -notmatch '^ENVIRONMENT UNAVAILABLE:' -or $processFixture.DotnetCalled){throw 'Direct WPF FAST did not guard the running app before build'}
 $processFixture.Running=$false
 Remove-Item -LiteralPath $guardJournal
 'fail'|Set-Content (Join-Path $local 'fail.marker')
 $failed=$false;try{& "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -FastTest -BuildOnly *> $null}catch{$failed=$true}
 if(!$failed -or (Test-Path (Join-Path $local 'dev.marker'))){throw 'Failed FAST reached DEV-RUN'}
 $journal=Join-Path $local '.git/kotrans2-fast-pending.json';$pending=Get-Content $journal -Raw|ConvertFrom-Json
 if(!$pending.Pending.Count){throw 'Failure was not persisted'}
 Remove-Item -LiteralPath (Join-Path $local 'fail.marker')
 & "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -FastTest -BuildOnly *> $null
 if((Get-Content $journal -Raw|ConvertFrom-Json).Pending.Count -ne 0 -or !(Test-Path (Join-Path $local 'dev.marker'))){throw 'Same commit retry failed'}
 if((Get-Content -LiteralPath (Join-Path $local 'dev.marker') -Raw).Trim() -ne 'False'){throw 'Default sync unexpectedly enabled debug overlay'}
 & "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -BuildOnly -DebugOverlay *> $null
 if((Get-Content -LiteralPath (Join-Path $local 'dev.marker') -Raw).Trim() -ne 'True'){throw 'Sync lost the debug overlay switch before DEV-RUN'}
 # Partial selection cannot clear other unresolved suites or start GUI.
 @{Version=1;Commit='synthetic';Pending=@('Ocr','Translation')}|ConvertTo-Json|Set-Content $journal
 $failed=$false;try{& "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -FastTest -FastSuite Ocr -BuildOnly *> $null}catch{$failed=$true}
 if(!$failed -or @((Get-Content $journal -Raw|ConvertFrom-Json).Pending) -notcontains 'Translation'){throw 'Partial retry lost unresolved suite'}
 '{bad'|Set-Content $journal
 $failed=$false;try{& "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -FastTest -BuildOnly *> $null}catch{$failed=$true}
 if(!$failed){throw 'Corrupt state accepted'}
 # Unmapped pull failures must also survive a same-commit rerun.
 @{Version=1;Pending=@();Unmapped=@()}|ConvertTo-Json|Set-Content $journal
 'synthetic'|Set-Content (Join-Path $seed 'scripts/unknown.ps1');G @('-C',$seed,'add','scripts');G @('-C',$seed,'commit','-m','C');G @('-C',$seed,'push','origin','HEAD')
 foreach($attempt in 1..2){$failed=$false;try{& "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -FastTest -BuildOnly *> $null}catch{$failed=$true};if(!$failed -or !(Get-Content $journal -Raw|ConvertFrom-Json).Unmapped.Count){throw 'Unmapped validation failure was forgotten'}}
 # An owned child test process terminates during FAST; its prewritten pending journal survives.
 $stub = Join-Path $local 'scripts/Test-Fast.ps1'
 @'
param($Suite)
if(Test-Path (Join-Path $PSScriptRoot '../interrupt.marker')) { [Environment]::Exit(93) }
Add-Content (Join-Path $PSScriptRoot '../calls.txt') $Suite
'@ | Set-Content -LiteralPath $stub
 Add-Content (Join-Path $local '.git/info/exclude') '/interrupt.marker'
 G @('-C',$local,'config','user.email','test@example.invalid'); G @('-C',$local,'config','user.name','Test')
 G @('-C',$local,'add','scripts/Test-Fast.ps1'); G @('-C',$local,'commit','-m','Owned interrupt stub'); G @('-C',$local,'push','origin','HEAD')
 @{Version=1;Pending=@();Unmapped=@()}|ConvertTo-Json|Set-Content $journal
 Remove-Item -LiteralPath (Join-Path $local 'dev.marker') -ErrorAction SilentlyContinue
 'interrupt' | Set-Content (Join-Path $local 'interrupt.marker')
 $runner = Join-Path $caseRoot 'interrupt.ps1'
 @'
param($SyncScript,$Root,$GitBinary)
function git { if($args -contains 'get-url'){$global:LASTEXITCODE=0;return 'https://github.com/breadu/ko_trans2.git'}; & $GitBinary @args }
function Set-Clipboard {param($Value)}
& $SyncScript -Root $Root -Branch codex/test-sync -FastTest -FastSuite Ocr -BuildOnly
'@ | Set-Content -LiteralPath $runner
 & pwsh -NoProfile -File $runner (Join-Path $PSScriptRoot 'Sync-DevRun.ps1') $local $realGit *> $null
 if($LASTEXITCODE -ne 93 -or @((Get-Content $journal -Raw|ConvertFrom-Json).Pending) -notcontains 'Ocr' -or (Test-Path (Join-Path $local 'dev.marker'))) { throw 'Process interruption lost pending or reached GUI' }
 Remove-Item -LiteralPath (Join-Path $local 'interrupt.marker')
 & "$PSScriptRoot/Sync-DevRun.ps1" -Root $local -Branch codex/test-sync -FastTest -FastSuite Ocr -BuildOnly *> $null
 if((Get-Content $journal -Raw|ConvertFrom-Json).Pending.Count){throw 'Interrupted suite did not recover'}
 # Linked worktrees must have independent Git journal locations.
 $linked=Join-Path $caseRoot 'linked'
 G @('-C',$local,'push','origin','HEAD:refs/heads/codex/test-linked')
 G @('-C',$local,'worktree','add','-b','codex/test-linked',$linked)
 @{Version=1;Pending=@('Translation');Unmapped=@()}|ConvertTo-Json|Set-Content $journal
 & "$PSScriptRoot/Sync-DevRun.ps1" -Root $linked -Branch codex/test-linked -FastTest -FastSuite Ocr -BuildOnly *> $null
 $linkedJournal = (& $realGit -C $linked rev-parse --path-format=absolute --git-path kotrans2-fast-pending.json | Select-Object -First 1)
 if($linkedJournal -eq $journal -or !(Test-Path -LiteralPath $linkedJournal) -or @((Get-Content $journal -Raw|ConvertFrom-Json).Pending) -notcontains 'Translation'){throw 'Linked worktree erased another pending journal'}
 # Inspect the actual production mapping function without running its top-level sync logic.
 $tree=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Sync-DevRun.ps1'),[ref]$null,[ref]$null)
 $functionAst=$tree.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-FastSelection'},$true)
 . ([scriptblock]::Create($functionAst.Extent.Text))
 foreach($map in @(@('src/KoTrans.Infrastructure/ApiKeyStore.cs','Translation'),@('src/KoTrans.Ocr/OcrRegionTracker.cs','Stability'),@('src/KoTrans.Infrastructure/TranslationPipeline.cs','GameQa'),@('tests/KoTrans.Tests/AuditRegressionTests.cs','Settings'))){if($map[1] -notin (Get-FastSelection @($map[0])).Suites){throw ('Wrong focused suite mapping: '+$map[0])}}
 Write-Output 'PASS: running-app preflight (sync/direct WPF), local bare A->B failure, same-commit retry, partial retry, hard process interruption, linked-worktree isolation, corrupt/unmapped-state guards and suite mappings. No GUI or remote network.'
}finally{
 $resolved=[IO.Path]::GetFullPath($caseRoot)
 if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'KoTrans2.SyncRegression.*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
