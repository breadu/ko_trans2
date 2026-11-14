# Manual ChatGPT/GitHub fallback workflow: sync, optional focused tests, incremental DEV-RUN.
# Does not alter the ordinary Codex Background-only Work.ps1 policy.
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '..'),
    [switch]$Choose,
    [string]$Branch = '',
    [switch]$FastTest,
    [string]$FastSuite = '',
    [switch]$BuildOnly,
    [switch]$DebugOverlay
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Sync-DevRun requires Windows and PowerShell 7.' }
if ($Choose -and $Branch) { throw 'Specify -Choose or -Branch, not both.' }
if ($FastSuite -and -not $FastTest) { throw '-FastSuite requires -FastTest.' }

function Git-Read {
    param([string]$Directory, [string[]]$Arguments)
    $output = @(& git -C $Directory @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw ('Git failed (exit {0}): git {1}' -f $LASTEXITCODE, ($Arguments -join ' '))
    }
    return $output
}
function Git-Run {
    param([string]$Directory, [string[]]$Arguments)
    & git -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw ('Git failed (exit {0}): git {1}' -f $LASTEXITCODE, ($Arguments -join ' '))
    }
}
function Full-Dir {
    param([string]$Directory)
    return [IO.Path]::GetFullPath($Directory).TrimEnd([char[]]@('\', '/'))
}
function Get-RemoteBranches {
    $rows = @(Git-Read $root @('ls-remote', '--heads', 'origin', 'refs/heads/codex/*'))
    return @(
        foreach ($line in $rows) {
            if ($line -match '^[a-fA-F0-9]+\s+refs/heads/(codex/[a-z0-9][a-z0-9-]*)$') {
                $Matches[1]
            }
        }
    ) | Sort-Object -Unique
}
function Pick-FromList {
    param([string[]]$Names, [string]$Question, [switch]$AllowBlank)
    for ($i = 0; $i -lt $Names.Count; $i++) {
        Write-Host ('  {0}. {1}' -f ($i + 1), $Names[$i])
    }
    $answer = Read-Host $Question
    if (-not $answer -and $AllowBlank) { return $null }
    $index = 0
    if (-not [int]::TryParse($answer, [ref]$index) -or
        $index -lt 1 -or $index -gt $Names.Count) {
        throw 'Invalid selection. No branch was selected.'
    }
    return $Names[$index - 1]
}
function New-RemoteWorktree {
    param([string]$Name)
    if ($Name -notmatch '^codex/[a-z0-9][a-z0-9-]*$') {
        throw 'Only codex/* development branches are supported.'
    }
    if ($Name -notin @(Get-RemoteBranches)) {
        throw ('Remote branch not found: {0}' -f $Name)
    }
    $mainOrCurrent = @($worktrees | Where-Object { $_.Branch -eq 'main' } | Select-Object -First 1)
    $parentRoot = if ($mainOrCurrent.Count) { $mainOrCurrent[0].Path } else { $root }
    $defaultPath = Join-Path (Split-Path $parentRoot -Parent) ('ko_trans2-' + $Name.Substring(6))
    Write-Host ('New remote branch: {0}' -f $Name)
    $entered = Read-Host ('Create local worktree at [Enter = {0}]' -f $defaultPath)
    if (-not $entered) { $entered = $defaultPath }
    if (-not [IO.Path]::IsPathFullyQualified($entered)) {
        $entered = Join-Path (Split-Path $parentRoot -Parent) $entered
    }
    $destination = Full-Dir $entered
    if (Test-Path -LiteralPath $destination) {
        throw 'Worktree directory already exists. No files were changed.'
    }
    foreach ($wt in $worktrees) {
        $existing = Full-Dir $wt.Path
        if ($destination.Equals($existing, [StringComparison]::OrdinalIgnoreCase) -or
            $destination.StartsWith($existing + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Do not create a worktree inside an existing checkout.'
        }
    }
    $answer = Read-Host ('Create {0} at this path? [y/N]' -f $Name)
    if ($answer -notmatch '^(y|yes)$') { throw 'Cancelled. No worktree was created.' }
    $refspec = 'refs/heads/{0}:refs/remotes/origin/{0}' -f $Name
    Git-Run $root @('fetch', '--no-tags', 'origin', $refspec)
    $localBranch = @(Git-Read $root @('branch', '--list', '--format=%(refname:short)', $Name))
    if ($Name -in $localBranch) {
        Git-Run $root @('worktree', 'add', $destination, $Name)
    } else {
        Git-Run $root @('worktree', 'add', '--track', '-b', $Name, $destination, ('origin/' + $Name))
    }
    Write-Host 'Worktree created. Local OCR models are never downloaded, copied, or committed.'
    return [pscustomobject]@{ Path = $destination; Branch = $Name; Created = $true }
}
function Get-FastSelection {
    param([string[]]$ChangedFiles)
    $suites = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $unmapped = [Collections.Generic.List[string]]::new()
    foreach ($raw in $ChangedFiles) {
        $file = $raw.Replace('\', '/')
        $pick = @()
        if ($file -match '^(docs/|README\.md$|AGENTS\.md$|THIRD-PARTY-NOTICES\.md$|\.gitignore$|\.gitattributes$)' -or
            $file -in @('Sync-DevRun.cmd', 'scripts/Dev-Run.ps1')) {
            continue
        } elseif ($file -match '^src/KoTrans.Ocr/') {
            $pick = @('Ocr', 'Stability')
        } elseif ($file -match 'WindowCaptureBounds|GameFixTests|StreamingTests|OpenAiStream') {
            $pick = @('Translation')
        } elseif ($file -match '^src/KoTrans.Core/Models[.]cs$') {
            $pick = @('Settings','Wpf')
        } elseif ($file -match '^src/KoTrans.Core/OcrContracts\.cs$') {
            $pick = @('Ocr', 'Stability')
        } elseif ($file -match '^src/KoTrans.Core/TranslationContracts\.cs$') {
            $pick = @('Translation')
        } elseif ($file -match '^src/KoTrans.Core/ExplanationContracts\.cs$') {
            $pick = @('Study')
        } elseif ($file -match '^src/KoTrans.Core/GameQaContracts\.cs$') {
            $pick = @('GameQa')
        } elseif ($file -match '^src/KoTrans.App/') {
            $pick = @('Wpf')
            if ($file -match 'GameQa') { $pick += 'GameQa' }
            if ($file -match 'Study|Pronunciation') { $pick += 'Study' }
            if ($file -match 'Translation') { $pick += 'Translation' }
            if ($file -match 'DesktopInput|DesktopWindows|PassThroughHotkey') { $pick += 'Image' }
        } elseif ($file -match '^src/KoTrans.Infrastructure/(ApiKeyStore|SettingsStore)\.cs$') {
            $pick = if ($file -match 'ApiKeyStore') { @('Translation') } else { @('Settings') }
        } elseif ($file -match '^src/KoTrans.Infrastructure/(GameQa[^/]*|GameQaRecorder|GameQaReplay)\.cs$') {
            $pick = @('GameQa')
        } elseif ($file -match '^src/KoTrans.Infrastructure/(ExplanationService|JapaneseReadingService|StudyFrame|StudyJson|VoiceIndex|VoiceIndexCache|AudioTemporaryFiles)\.cs$') {
            $pick = @('Study')
            if ($file -match 'VoiceIndex|AudioTemporaryFiles') { $pick += 'Stability' }
        } elseif ($file -match '^src/KoTrans.Infrastructure/(TranslationContext|TranslationPipeline|TranslationServices)\.cs$') {
            $pick = if ($file -match 'TranslationPipeline') { @('Translation','Stability','GameQa') } else { @('Translation') }
        } elseif ($file -match '^src/KoTrans.Infrastructure/(ScreenCapture|WindowTargets)\.cs$') {
            $pick = @('Image', 'Ocr')
        } elseif ($file -match '^tests/KoTrans.WpfTests/.*\.cs$') {
            $pick = @('Wpf')
        } elseif ($file -match 'AuditRegressionTests|AppLaunchOptions') {
            $pick = @('Settings','Translation','Wpf')
        } elseif ($file -match '^tests/KoTrans.Tests/GameQaTests\.cs$') {
            $pick = @('GameQa')
        } elseif ($file -match '^tests/KoTrans.Tests/OcrAlgorithmTests\.cs$') {
            $pick = @('Ocr', 'Stability')
        } elseif ($file -match '^tests/KoTrans.Tests/StabilityTests\.cs$') {
            $pick = @('Stability')
        } elseif ($file -match '^tests/KoTrans.Tests/Phase4Tests\.cs$') {
            $pick = @('Study')
        } elseif ($file -match '^tests/KoTrans.Tests/Phase3Tests\.cs$') {
            $pick = @('Translation')
        } else {
            $unmapped.Add($file)
        }
        foreach ($suite in $pick) { [void]$suites.Add($suite) }
    }
    return [pscustomobject]@{
        Suites = @($suites | Sort-Object)
        Unmapped = @($unmapped)
    }
}
function Copy-FailureExcerpt {
    param([string]$Log, [string]$Label, [string]$BranchName, [string]$Commit)
    if (-not (Test-Path -LiteralPath $Log -PathType Leaf)) {
        Write-Warning ('Failure log not available: {0}' -f $Log)
        return
    }
    try {
        $interesting = @(Select-String -LiteralPath $Log -Pattern '(?i)\berror\b|\bfailed\b|오류' |
            Select-Object -First 50 | ForEach-Object { $_.Line })
        $tail = @(Get-Content -LiteralPath $Log -Tail 80)
        $lines = @(
            ('KO Trans 2: {0}' -f $Label)
            ('Branch: {0}' -f $BranchName)
            ('Commit: {0}' -f $Commit)
            ('Full log: {0}' -f $Log)
            ''
            'Error lines (first 50):'
        ) + $interesting + @('', 'Last 80 lines:') + $tail
        $summary = $lines -join [Environment]::NewLine
        if ($summary.Length -gt 30000) {
            $summary = $summary.Substring(0, 30000) + [Environment]::NewLine +
                '[Excerpt truncated. Consult the original log.]'
        }
        Set-Clipboard -Value $summary -ErrorAction Stop
        Write-Host ('[DIAGNOSTICS] Copied to clipboard for ChatGPT: {0}' -f $Log)
    } catch {
        Write-Warning ('Could not access the clipboard. Log preserved: {0}' -f $Log)
    }
}

$root = Full-Dir (Resolve-Path -LiteralPath $Root).Path
$root = (Git-Read $root @('rev-parse', '--show-toplevel') | Select-Object -First 1).Trim()
$origin = (Git-Read $root @('remote', 'get-url', 'origin') | Select-Object -First 1).Trim()
if ($origin -notmatch '^(https://github[.]com/|git@github[.]com:|ssh://git@github[.]com/)(breadu/ko_trans2)([.]git)?/?$') {
    throw 'This tool only syncs the approved breadu/ko_trans2 origin; never breadu/ko_trans.'
}
$commonDir = (Git-Read $root @('rev-parse', '--path-format=absolute', '--git-common-dir') | Select-Object -First 1).Trim()
$stateFile = Join-Path $commonDir 'kotrans2-sync-devrun.json'
$saved = $null
if (Test-Path -LiteralPath $stateFile -PathType Leaf) {
    try {
        $saved = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    } catch {
        Write-Warning 'Saved worktree preference is invalid. Choose a branch.'
    }
}
$worktrees = @(
    $entry = $null
    foreach ($line in (@(Git-Read $root @('worktree', 'list', '--porcelain')) + @(''))) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($null -ne $entry) { [pscustomobject]$entry }
            $entry = $null
            continue
        }
        if ($line.StartsWith('worktree ')) {
            $entry = [ordered]@{ Path = $line.Substring(9); Branch = ''; Created = $false }
        } elseif ($null -ne $entry -and $line.StartsWith('branch refs/heads/')) {
            $entry.Branch = $line.Substring(18)
        }
    }
)
$local = @($worktrees | Where-Object {
    $_.Branch -match '^codex/[a-z0-9][a-z0-9-]*$' -and
    (Test-Path -LiteralPath $_.Path -PathType Container)
} | Sort-Object Branch)
$selected = $null
$remoteSnapshot = $null
if ($Branch -and $Branch -notmatch '^codex/[a-z0-9][a-z0-9-]*$') {
    throw 'Only codex/* development branches are supported.'
}
if ($Branch) {
    $selected = $local | Where-Object { $_.Branch -eq $Branch } | Select-Object -First 1
    if ($null -eq $selected) { $selected = New-RemoteWorktree $Branch }
} elseif ($Choose) {
    Write-Host 'Local development worktrees:'
    for ($i = 0; $i -lt $local.Count; $i++) {
        Write-Host ('  {0}. {1} ({2})' -f ($i + 1), $local[$i].Branch, $local[$i].Path)
    }
    Write-Host '  R. Select a GitHub-only branch and create a worktree'
    $answer = Read-Host 'Enter local branch number or R'
    if ($answer -ieq 'R') {
        $names = @(Get-RemoteBranches | Where-Object { $_ -notin @($local | ForEach-Object { $_.Branch }) })
        if (-not $names.Count) { throw 'No remote-only branches found.' }
        $name = Pick-FromList $names 'Select a remote branch number'
        $selected = New-RemoteWorktree $name
    } else {
        $index = 0
        if (-not [int]::TryParse($answer, [ref]$index) -or $index -lt 1 -or $index -gt $local.Count) {
            throw 'Invalid local worktree selection.'
        }
        $selected = $local[$index - 1]
    }
} else {
    # Unlike the MovieMalhae main-only launcher, this file lives in an
    # unmerged codex worktree. Scan for new branches even when launched there.
    $remoteSnapshot = @(Get-RemoteBranches)
    $localNames = @($local | ForEach-Object { $_.Branch })
    $remoteOnly = @($remoteSnapshot | Where-Object { $_ -notin $localNames })
    $hasSnapshot = ($null -ne $saved -and $saved.PSObject.Properties.Name -contains 'remoteSeen')
    if ($hasSnapshot) {
        $seen = @($saved.remoteSeen | Where-Object { $_ })
        $new = @($remoteOnly | Where-Object { $_ -notin $seen })
        if ($new.Count -eq 1) {
            Write-Host ('New GitHub branch found: {0}' -f $new[0])
            $answer = Read-Host 'Use the new branch? [Y/n]'
            if ($answer -match '^(|y|yes)$') {
                $selected = New-RemoteWorktree $new[0]
            } elseif ($answer -notmatch '^(n|no)$') {
                throw 'Invalid answer. No branch was selected.'
            }
        } elseif ($new.Count -gt 1) {
            Write-Host 'Several new development branches were found:'
            $name = Pick-FromList $new 'Choose number (Enter = previous branch)' -AllowBlank
            if ($name) { $selected = New-RemoteWorktree $name }
        }
    } elseif ($remoteOnly.Count -gt 0) {
        Write-Host 'First scan: existing remote-only branches may not be new.'
        $answer = Read-Host 'Select a remote-only branch now? [y/N]'
        if ($answer -match '^(y|yes)$') {
            $name = Pick-FromList $remoteOnly 'Choose a remote-only branch number'
            $selected = New-RemoteWorktree $name
        } elseif ($answer -notmatch '^(|n|no)$') {
            throw 'Invalid answer.'
        }
    }
    if ($null -eq $selected -and $null -ne $saved) {
        $selected = $local | Where-Object {
            $_.Branch -eq $saved.branch -and
            (Full-Dir $_.Path) -ieq (Full-Dir $saved.worktree)
        } | Select-Object -First 1
    }
    if ($null -eq $selected) {
        $selected = $local | Where-Object { (Full-Dir $_.Path) -ieq (Full-Dir $root) } |
            Select-Object -First 1
    }
    if ($null -eq $selected -and $local.Count -eq 1) { $selected = $local[0] }
    if ($null -eq $selected -and $local.Count -gt 1) {
        Write-Host 'No remembered worktree. Choose one:'
        $names = @($local | ForEach-Object { $_.Branch })
        $name = Pick-FromList $names 'Select a local branch number'
        $selected = $local | Where-Object { $_.Branch -eq $name } | Select-Object -First 1
    }
    if ($null -eq $selected) {
        if ($remoteOnly.Count -eq 0) { throw 'No codex/* development worktree exists.' }
        $name = Pick-FromList $remoteOnly 'Select an available remote branch'
        $selected = New-RemoteWorktree $name
    }
}
$workDir = Full-Dir $selected.Path
$branchNow = (Git-Read $workDir @('branch', '--show-current') | Select-Object -First 1).Trim()
if ($branchNow -ne $selected.Branch -or $branchNow -notlike 'codex/*') {
    throw 'Selected worktree branch changed; never sync main.'
}
# Check before pull and FAST, not only at the later DEV-RUN build step.
if (Get-Process KoTrans2 -ErrorAction SilentlyContinue) {
    throw 'ENVIRONMENT UNAVAILABLE: KO Trans 2 is running. Close its window normally and run Sync-DevRun again. No pull, FAST, build or process termination was attempted.'
}
$dirty = @(Git-Read $workDir @('status', '--porcelain', '--untracked-files=normal'))
if ($dirty.Count -gt 0) {
    Write-Host 'Uncommitted files in the target worktree:'
    $dirty | Select-Object -First 12 | ForEach-Object { Write-Host $_ }
    throw 'Commit/stash or inspect these local changes first. No pull was attempted.'
}
$before = (Git-Read $workDir @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
Write-Host ('Syncing {0} at {1}' -f $branchNow, $workDir)
Git-Run $workDir @('pull', '--ff-only', '--no-rebase', 'origin', $branchNow)
$after = (Git-Read $workDir @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
Write-Host ('Fast-forward sync: {0} -> {1}' -f $before.Substring(0, 8), $after.Substring(0, 8))

# Keep only local, non-tracked metadata; do not write user settings or game data.
$newState = @{ branch = $branchNow; worktree = $workDir }
if ($null -ne $remoteSnapshot) {
    $newState.remoteSeen = @($remoteSnapshot)
} elseif ($null -ne $saved -and $saved.PSObject.Properties.Name -contains 'remoteSeen') {
    $newState.remoteSeen = @($saved.remoteSeen)
}
$newState | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $stateFile -Encoding utf8

$logDir = Join-Path $workDir 'work/sync-devrun'
[void][IO.Directory]::CreateDirectory($logDir)

if ($FastTest) {
    # Git's per-worktree directory isolates retry state, including linked worktrees.
    $journalPath = [IO.Path]::GetFullPath((Git-Read $workDir @('rev-parse','--git-path','kotrans2-fast-pending.json') | Select-Object -First 1), $workDir)
    $pending = @(); $unmappedPending = @()
    if (Test-Path -LiteralPath $journalPath) {
        try {
            $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json -ErrorAction Stop
            if ($journal.Version -ne 1 -or $null -eq $journal.Pending -or @($journal.Pending | Where-Object { $_ -notin @('Foundation','Settings','Image','Ocr','Translation','Study','Wpf','Stability','GameQa') }).Count) { throw 'Invalid retry state' }
            $pending = @($journal.Pending)
            if ($journal.PSObject.Properties.Name -contains "Unmapped") { $unmappedPending = @($journal.Unmapped) }
        } catch { throw 'FAST retry state is invalid. Inspect the per-worktree kotrans2-fast-pending.json; no GUI run is allowed until resolved.' }
    }
    function Save-FastPending {
        $tempJournal = $journalPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        @{ Version=1; Commit=$after; Pending=@($pending); Unmapped=@($unmappedPending) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $tempJournal -Encoding utf8
        [IO.File]::Move($tempJournal, $journalPath, $true)
    }
    $allowed = @('Foundation', 'Settings', 'Image', 'Ocr', 'Translation', 'Study', 'Wpf', 'Stability', 'GameQa')
    $suites = @()
    if ($FastSuite) {
        $suites = @($FastSuite.Split(',') | ForEach-Object { $_.Trim() } |
            Where-Object { $_ } | Sort-Object -Unique)
        if (@($suites | Where-Object { $_ -notin $allowed }).Count -gt 0 -or $suites.Count -eq 0) {
            throw ('Unknown FAST suite. Available: {0}' -f ($allowed -join ', '))
        }
        $unmappedPending = @() # Explicit operator selection supplies coverage for unmapped changes.
    } else {
        $changed = @()
        if ($before -ne $after) {
            $changed = @(Git-Read $workDir @('diff', '--name-only', '--no-ext-diff', $before, $after, '--'))
        } elseif ($selected.Created) {
            # First checkout already points to remote HEAD. Compare against
            # the prior C# migration branch rather than the mostly-empty main.
            foreach ($baseline in @('codex/csharp-migration', 'main')) {
                if ($baseline -eq $branchNow) { continue }
                $common = @(& git -C $workDir merge-base $baseline HEAD 2>$null)
                if ($LASTEXITCODE -eq 0 -and $common.Count -gt 0) {
                    $changed = @(Git-Read $workDir @('diff', '--name-only', '--no-ext-diff', $common[0], $after, '--'))
                    break
                }
            }
        }
        $plan = Get-FastSelection @(@($changed) + @($unmappedPending) | Sort-Object -Unique)
        if ($plan.Unmapped.Count -gt 0) {
            $unmappedPending = @($plan.Unmapped); Save-FastPending
            Write-Host ('Cannot safely auto-map these {0} file(s) to small FAST suites:' -f $plan.Unmapped.Count)
            $plan.Unmapped | ForEach-Object { Write-Host ('  {0}' -f $_) }
            throw 'Choose explicit FAST suites using -FastSuite, or run without -FastTest. No broad/full tests were started.'
        }
        $suites = @(@($plan.Suites) + @($pending) | Sort-Object -Unique)
    }
    if ($suites.Count -eq 0) {
        Write-Host 'FAST: no applicable committed code changes; skipped (not a regression PASS).'
    } else {
        $testScript = Join-Path $workDir 'scripts/Test-Fast.ps1'
        if (-not (Test-Path -LiteralPath $testScript -PathType Leaf)) {
            throw ('FAST runner is missing in selected branch: {0}' -f $testScript)
        }
        $pending = @(@($pending) + @($suites) | Sort-Object -Unique)
        Save-FastPending # A crash/interruption before completion remains pending.
        foreach ($suite in $suites) {
            $log = Join-Path $logDir ('fast-' + $suite.ToLowerInvariant() + '.log')
            Write-Host ('FAST: running {0} (Background only)' -f $suite)
            try {
                & $testScript -Suite $suite *> $log
                if (-not $?) { throw ('FAST {0} did not complete successfully.' -f $suite) }
                Get-Content -LiteralPath $log -Tail 4
                $pending = @($pending | Where-Object { $_ -ne $suite })
                Save-FastPending
            } catch {
                Write-Host ('FAST {0} failed. Log: {1}' -f $suite, $log)
                if (Test-Path -LiteralPath $log) {
                    Get-Content -LiteralPath $log -Tail 35
                }
                Copy-FailureExcerpt -Log $log -Label ('FAST failure: ' + $suite) -BranchName $branchNow -Commit $after
                throw
            }
        }
    }
}

if ($FastTest -and $pending.Count -gt 0) { throw ('Unresolved FAST suites remain: ' + ($pending -join ', ') + '. No DEV-RUN started.') }
$devRun = Join-Path $workDir 'scripts/Dev-Run.ps1'
if (-not (Test-Path -LiteralPath $devRun -PathType Leaf)) {
    throw ('DEV-RUN script is missing in selected branch: {0}' -f $devRun)
}
$previousModelPath = [Environment]::GetEnvironmentVariable('KO_TRANS2_MODELS', 'Process')
$setModelPath = $false
if (-not $previousModelPath -and
    -not (Test-Path -LiteralPath (Join-Path $workDir 'models/det/manifest.json') -PathType Leaf)) {
    # The OCR model files are intentionally excluded from Git. Use the existing
    # checkout's models, if present, rather than duplicating huge binaries.
    foreach ($wt in (@($root) + @($worktrees | ForEach-Object { $_.Path }))) {
        $candidate = Join-Path $wt 'models'
        if (Test-Path -LiteralPath (Join-Path $candidate 'det/manifest.json') -PathType Leaf) {
            $env:KO_TRANS2_MODELS = $candidate
            $setModelPath = $true
            Write-Host ('Using existing read-only OCR model directory: {0}' -f $candidate)
            break
        }
    }
}
$buildLog = Join-Path $logDir 'build.log'
Write-Host 'Running existing incremental DEV-RUN. Full rebuild/publish/desktop tests are not started.'
try {
    try {
        & $devRun -BuildOnly:$BuildOnly -DebugOverlay:$DebugOverlay -CaptureBuildLog
        if (-not $?) { throw 'DEV-RUN returned a failure.' }
    } catch {
        $reason = $_.Exception.Message
        if ($reason -match 'Incremental build failed') {
            Copy-FailureExcerpt -Log $buildLog -Label 'DEV-RUN build failed' -BranchName $branchNow -Commit $after
        }
        throw
    }
} finally {
    if ($setModelPath) {
        Remove-Item Env:KO_TRANS2_MODELS -ErrorAction SilentlyContinue
    }
}
Write-Host ('Sync-DevRun completed for {0} at {1}.' -f $branchNow, $after.Substring(0, 8))
