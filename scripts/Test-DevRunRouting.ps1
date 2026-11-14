# Temporary local repositories and stub build only; no GUI/network/models/user settings.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('KoTrans2.DevRouting.' + [Guid]::NewGuid().ToString('N'))
$seed = Join-Path $fixture 'main repo'
$first = Join-Path $fixture '개발 first'
$second = Join-Path $fixture 'second'
$docs = Join-Path $fixture 'benchmark'
$source = Join-Path $PSScriptRoot 'Dev-Run.ps1'
$checks = 0; $buildState = [pscustomobject]@{ Calls = 0; Running = $false; AllowLaunch = $false; LaunchArgs = @() }
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action | Out-Null } catch { $failed = $true }; Check $failed 'Unsafe/ambiguous routing accepted' }
function G([string[]]$Arguments) { & git @Arguments | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Fixture Git failed' } }
function Read-Host { throw 'Unexpected interactive input in routing regression' }
function Get-Process { param($Name,$ErrorAction); if ($Name -ne 'KoTrans2') { throw 'Unexpected process lookup' }; if ($buildState.Running) { [pscustomobject]@{ Id = 999 } } }
function Start-Process { param($FilePath,$ArgumentList,$WorkingDirectory,[switch]$PassThru); if (!$buildState.AllowLaunch) { throw 'GUI launch is forbidden in this test' }; $buildState.LaunchArgs = $ArgumentList; [pscustomobject]@{Id=123} }
function dotnet { $buildState.Calls++; Set-Content -LiteralPath (Join-Path (Get-Location) 'build.marker') 'synthetic'; $global:LASTEXITCODE = 0 }
try {
    New-Item -ItemType Directory -Path "$seed/scripts","$seed/src/KoTrans.App" -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination "$seed/scripts/Dev-Run.ps1"
    'synthetic project' | Set-Content -LiteralPath "$seed/src/KoTrans.App/KoTrans.App.csproj"
    G @('init','-b','main',$seed); G @('-C',$seed,'config','user.name','Routing Test'); G @('-C',$seed,'config','user.email','routing@example.invalid')
    G @('-C',$seed,'add','scripts/Dev-Run.ps1','src/KoTrans.App/KoTrans.App.csproj'); G @('-C',$seed,'commit','-m','fixture baseline')
    $standalone = & "$seed/scripts/Dev-Run.ps1" -ResolveOnly
    Check ($standalone.Branch -eq 'main' -and [IO.Path]::GetFullPath($standalone.Root) -eq $seed) 'Fresh clone could not resolve main'
    $mainBefore = (& git -C $seed rev-parse HEAD).Trim()
    G @('-C',$seed,'worktree','add','-b','codex/benchmark',$docs)
    'report only' | Set-Content -LiteralPath "$docs/report.txt"; G @('-C',$docs,'add','report.txt'); G @('-C',$docs,'commit','-m','fixture report')
    G @('-C',$seed,'worktree','add','-b','codex/first',$first)
    'first source' | Set-Content -LiteralPath "$first/src/KoTrans.App/Example.cs"; G @('-C',$first,'add','src/KoTrans.App/Example.cs'); G @('-C',$first,'commit','-m','fixture first')
    $selected = & "$seed/scripts/Dev-Run.ps1" -ResolveOnly
    Check ($selected.Branch -eq 'codex/first' -and [IO.Path]::GetFullPath($selected.Root) -eq [IO.Path]::GetFullPath($first)) 'Single application worktree not found; Unicode/space path broken'
    Check ($buildState.Calls -eq 0) 'Resolve-only built the application'
    $preference = $selected.Preference
    Check (!(Test-Path -LiteralPath $preference)) 'Resolve-only changed the preference'
    G @('-C',$seed,'worktree','add','-b','codex/second',$second)
    'second source' | Set-Content -LiteralPath "$second/src/KoTrans.App/Example.cs"; G @('-C',$second,'add','src/KoTrans.App/Example.cs'); G @('-C',$second,'commit','-m','fixture second')
    Reject { & "$seed/scripts/Dev-Run.ps1" -ResolveOnly }
    $selected = & "$seed/scripts/Dev-Run.ps1" -ResolveOnly -Branch codex/second
    Check ([IO.Path]::GetFullPath($selected.Root) -eq [IO.Path]::GetFullPath($second)) 'Explicit branch not selected'
    @{ branch = 'codex/first'; worktree = $first } | ConvertTo-Json | Set-Content -LiteralPath $preference
    $selected = & "$seed/scripts/Dev-Run.ps1" -ResolveOnly
    Check ([IO.Path]::GetFullPath($selected.Root) -eq [IO.Path]::GetFullPath($first)) 'Remembered valid worktree not selected'
    & "$seed/scripts/Dev-Run.ps1" -BuildOnly -CaptureBuildLog -TestData 'relative test path' -MockAi -DebugOverlay
    Check ($buildState.Calls -eq 1 -and (Test-Path -LiteralPath "$first/build.marker") -and !(Test-Path -LiteralPath "$seed/build.marker")) 'Main was built instead of forwarding BuildOnly to the branch'
    $buildState.Running = $true; Reject { & "$seed/scripts/Dev-Run.ps1" -BuildOnly }
    Check ($buildState.Calls -eq 1) 'Running-app guard allowed a build'; $buildState.Running = $false
    $buildState.AllowLaunch = $true
    & "$seed/scripts/Dev-Run.ps1" -TestData 'relative test path' -MockAi -DebugOverlay
    Check ($buildState.LaunchArgs -contains '--debug-overlay' -and $buildState.LaunchArgs -contains '--dev-run' -and $buildState.LaunchArgs -contains '--mock-ai' -and $buildState.LaunchArgs -contains '--test-data') 'Delegation lost debug/mock/isolation launch arguments'
    $buildState.AllowLaunch = $false
    $own = & "$second/scripts/Dev-Run.ps1" -ResolveOnly
    Check ($own.Root -eq $second) 'Explicit codex worktree was redirected by another remembered choice'
    Reject { & "$seed/scripts/Dev-Run.ps1" -ResolveOnly -Branch main }
    Reject { & "$seed/scripts/Dev-Run.ps1" -ResolveOnly -Branch codex/missing }
    Reject { & "$seed/scripts/Dev-Run.ps1" -ResolveOnly -Choose -Branch codex/first }
    @{ branch = 'codex/first'; worktree = $second } | ConvertTo-Json | Set-Content -LiteralPath $preference
    Reject { & "$seed/scripts/Dev-Run.ps1" -ResolveOnly }
    'invalid json' | Set-Content -LiteralPath $preference
    Reject { & "$seed/scripts/Dev-Run.ps1" -ResolveOnly }
    Check ((& git -C $seed branch --show-current).Trim() -eq 'main' -and (& git -C $seed rev-parse HEAD).Trim() -eq $mainBefore) 'Main branch/ref changed'
    Check (@(& git -C $seed status --porcelain).Count -eq 0) 'Launcher modified main tracked/source files'
    Write-Output "PASS: $checks DEV-RUN local worktree routing/ambiguity/preference/Unicode/BuildOnly/running-app assertions. No GUI/network/models/user settings."
} finally {
    $absolute = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (!$absolute.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'KoTrans2.DevRouting.*') { throw 'Unsafe routing fixture cleanup path' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
