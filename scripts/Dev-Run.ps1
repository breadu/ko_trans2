param([switch]$BuildOnly, [string]$TestData, [switch]$MockAi, [switch]$DebugOverlay, [switch]$CaptureBuildLog,
    [string]$Branch = '', [switch]$Choose, [switch]$ResolveOnly)
$ErrorActionPreference = 'Stop'
if ($Branch -and $Choose) { throw 'Specify -Branch or -Choose, not both.' }
function Read-DevGit([string]$Directory, [string[]]$Arguments) {
    $result = @(& git -C $Directory @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the local development repository.' }
    return $result
}
function Resolve-DevTarget([string]$Directory) {
    $current = (Read-DevGit $Directory @('branch','--show-current') | Select-Object -First 1).Trim()
    $common = (Read-DevGit $Directory @('rev-parse','--path-format=absolute','--git-common-dir') | Select-Object -First 1).Trim()
    $preference = Join-Path $common 'kotrans2-devrun.json'
    if (!$Branch -and !$Choose -and $current -like 'codex/*') {
        return [pscustomobject]@{ Root = $Directory; Branch = $current; Preference = $preference }
    }
    $candidates = @(
        $entry = $null
        # NUL records preserve Unicode, spaces and Git-quoted path characters.
        $records = (Read-DevGit $Directory @('worktree','list','--porcelain','-z')) -join "`n"
        foreach ($line in ($records.Split([char]0) + @(''))) {
            if ($line.Length -eq 0) {
                if ($entry -and $entry.Branch -like 'codex/*' -and
                    (Test-Path -LiteralPath (Join-Path $entry.Root 'scripts/Dev-Run.ps1')) -and
                    (Test-Path -LiteralPath (Join-Path $entry.Root 'src/KoTrans.App/KoTrans.App.csproj'))) { [pscustomobject]$entry }
                $entry = $null
            } elseif ($line.StartsWith('worktree ')) { $entry = @{ Root = $line.Substring(9); Branch = ''; Preference = $preference } }
            elseif ($entry -and $line.StartsWith('branch refs/heads/')) { $entry.Branch = $line.Substring(18) }
        }
    )
    $selected = $null
    if ($Branch) {
        $selected = $candidates | Where-Object Branch -CEQ $Branch | Select-Object -First 1
        if (!$selected) { throw 'The requested codex/* branch has no existing local application worktree. No checkout or sync was attempted.' }
    } elseif (!$Choose) {
        if (Test-Path -LiteralPath $preference) {
            try {
                if ((Get-Item -LiteralPath $preference).Length -gt 16384) { throw 'Oversized preference' }
                $saved = Get-Content -LiteralPath $preference -Raw | ConvertFrom-Json
                $selected = $candidates | Where-Object { $_.Branch -ceq $saved.branch -and
                    [IO.Path]::GetFullPath($_.Root).TrimEnd('\','/') -ieq [IO.Path]::GetFullPath($saved.worktree).TrimEnd('\','/') } | Select-Object -First 1
                if ($selected) {
                    & git -C $Directory merge-base --is-ancestor $selected.Branch refs/heads/main 2>$null
                    if ($LASTEXITCODE -eq 0) { $selected = $null }
                    elseif ($LASTEXITCODE -ne 1) { throw 'Cannot inspect saved branch ancestry' }
                }
            } catch { $selected = $null; Write-Warning 'Saved DEV-RUN worktree is unavailable. Resolving local candidates.' }
        }
        if (!$selected) {
            $active = @($candidates | Where-Object {
                @(Read-DevGit $_.Root @('diff','--name-only','refs/heads/main...HEAD','--','src')).Count -gt 0
            })
            # Reports/benchmark-only branches must not silently replace the app under development.
            if ($active.Count -eq 1) { $selected = $active[0] }
        }
    }
    if (!$selected) {
        if ($candidates.Count -eq 0 -and !$Choose -and $current -eq 'main') {
            return [pscustomobject]@{ Root = $Directory; Branch = $current; Preference = $preference }
        }
        if ($candidates.Count -eq 0) { throw 'No local codex/* application worktree exists. No branch was created or changed.' }
        if ($ResolveOnly) { throw 'DEV-RUN target is ambiguous. Use -Branch codex/name or run Dev-Run.cmd -Choose.' }
        Write-Host 'Select a local development worktree (no pull or checkout):'
        $candidates = @($candidates | Sort-Object Branch)
        for ($i = 0; $i -lt $candidates.Count; $i++) { Write-Host ('  {0}. {1} ({2})' -f ($i + 1), $candidates[$i].Branch, $candidates[$i].Root) }
        $answer = Read-Host 'Branch number'; $index = 0
        if (![int]::TryParse($answer, [ref]$index) -or $index -lt 1 -or $index -gt $candidates.Count) { throw 'Invalid selection. Nothing was built or launched.' }
        $selected = $candidates[$index - 1]
    }
    $actual = (Read-DevGit $selected.Root @('branch','--show-current') | Select-Object -First 1).Trim()
    $actualCommon = (Read-DevGit $selected.Root @('rev-parse','--path-format=absolute','--git-common-dir') | Select-Object -First 1).Trim()
    if ($actual -cne $selected.Branch -or $actual -notlike 'codex/*' -or $actualCommon -ine $common) { throw 'The selected development worktree changed or belongs to another repository.' }
    return $selected
}
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (!$ResolveOnly -and (Get-Process KoTrans2 -ErrorAction SilentlyContinue)) { throw 'KO Trans 2 is running. Use its notification-area icon > Exit (X only hides). No process was stopped.' }
    $target = Resolve-DevTarget $root
    if ($ResolveOnly) { $target; return }
    Write-Host ('DEV-RUN target: {0} ({1})' -f $target.Branch, $target.Root)
    if (!$BuildOnly) {
        $tempPreference = $target.Preference + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            @{ branch = $target.Branch; worktree = $target.Root } | ConvertTo-Json | Set-Content -LiteralPath $tempPreference -Encoding utf8
            [IO.File]::Move($tempPreference, $target.Preference, $true)
        } finally { if (Test-Path -LiteralPath $tempPreference) { Remove-Item -LiteralPath $tempPreference } }
    }
    if ([IO.Path]::GetFullPath($target.Root).TrimEnd('\','/') -ine [IO.Path]::GetFullPath($root).TrimEnd('\','/')) {
        $forward = @{ BuildOnly = $BuildOnly; MockAi = $MockAi; DebugOverlay = $DebugOverlay; CaptureBuildLog = $CaptureBuildLog }
        if ($TestData) { $forward.TestData = [IO.Path]::GetFullPath($TestData, $root) }
        & (Join-Path $target.Root 'scripts/Dev-Run.ps1') @forward
        return
    }
    $branch = git branch --show-current
    if ($LASTEXITCODE -ne 0 -or $branch -cne $target.Branch) { throw 'DEV-RUN branch changed during target selection.' }
    $exe = Join-Path $root 'src/KoTrans.App/bin/Release/net10.0-windows/KoTrans2.exe'
    if (Get-Process KoTrans2 -ErrorAction SilentlyContinue) { throw 'KO Trans 2 is running. Use its notification-area icon > Exit (X only hides). No process was stopped.' }
    if ($CaptureBuildLog) {
        $logDir = Join-Path $root 'work/sync-devrun'
        [void][IO.Directory]::CreateDirectory($logDir)
        $log = Join-Path $logDir 'build.log'
        dotnet build src/KoTrans.App/KoTrans.App.csproj -c Release --nologo *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath $log -Tail 80
            throw ('Incremental build failed. Log: {0}' -f $log)
        }
        Write-Output ('Incremental build succeeded. Log: {0}' -f $log)
    } else {
        dotnet build src/KoTrans.App/KoTrans.App.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Incremental build failed.' }
    }
    if (!$BuildOnly) {
        if ($MockAi -and !$TestData) { throw 'MockAi requires isolated TestData.' }
        $launchArgs = @('--dev-run')
        if ($MockAi) { $launchArgs += '--mock-ai' }
        if ($DebugOverlay) { $launchArgs += '--debug-overlay' }
        if ($TestData) { $launchArgs += @('--test-data', ('"' + [IO.Path]::GetFullPath($TestData) + '"')) }
        $process = Start-Process -FilePath $exe -ArgumentList $launchArgs -WorkingDirectory $root -PassThru
        Write-Output "DEV-RUN started: PID=$($process.Id), branch=$branch"
    }
} finally { Pop-Location }
