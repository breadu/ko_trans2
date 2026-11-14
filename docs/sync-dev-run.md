# ChatGPT → GitHub → Sync-DevRun (KO Trans 2)

This workflow lives on the unmerged **`codex/csharp-migration`** branch of the private **`breadu/ko_trans2`** repository. **Do not copy it into `main`** before the separately approved merge. It is a **manual** launcher for continuing development when Codex usage runs out. It does **not** change the existing Codex default (`scripts/Work.ps1 -BuildOnly` + selected Background FAST).

## Typical workflow

### Local DEV-RUN without synchronization

`Dev-Run.cmd` uses only existing local worktrees; it never fetches, pulls, checks out, merges or creates a branch. From a `codex/*` worktree it builds that worktree. From the main checkout it reuses the last valid, unmerged selection in `.git/kotrans2-devrun.json`, or automatically selects the sole worktree with application source changes relative to main. Benchmark/report-only worktrees do not automatically replace the application under development. Ambiguous choices show a numbered menu. The chosen branch and path are printed before the incremental build.

`Dev-Run.cmd -Choose` selects another existing worktree, and `Dev-Run.cmd -Branch codex/name` targets one explicitly. The preference is saved only for a manual GUI run. `-BuildOnly` preserves the no-GUI policy; `-ResolveOnly` inspects the destination without a build, GUI, prompt or preference write. No model/configuration files are moved. The target keeps the existing running-app guard and normal build/launch behavior. Regression: `pwsh -NoProfile -File scripts/Test-DevRunRouting.ps1` uses temporary local Git fixtures and a stub build only.

1. Ask ChatGPT to modify a `codex/*` branch based on `main`, commit, and report its commit SHA. Do not push to or merge `main`.
2. Run **`Sync-DevRun.cmd -FastTest`** to pull that branch with fast-forward only, run the **related Background FAST suites** for files in the pulled commits, then incrementally build and launch KoTrans2.
3. Try the GUI yourself. If a build or selected FAST test fails, the launcher saves a log and copies a bounded diagnostic excerpt to the clipboard. Paste it into ChatGPT for another fix.
4. For a normal quick build and launch without regression, double-click **`Sync-DevRun.cmd`**.
5. If a complex runtime or game-input issue cannot be reproduced by ChatGPT, save a sanitized GitHub Issue for later Codex/Windows Desktop investigation. Do not upload Game QA screenshots, original text, personal data, keys, or model binaries automatically.

## Launcher options

| Command | Effect |
| --- | --- |
| `Sync-DevRun.cmd` | Detect GitHub branches → sync selected worktree → incremental build → run app |
| `Sync-DevRun.cmd -FastTest` | Same, but run mapped **Background FAST** suites first |
| `Sync-DevRun.cmd -BuildOnly -FastTest` | Sync + focused regression + incremental build, **no GUI** |
| `Sync-DevRun.cmd -DebugOverlay` | Sync + incremental build + launch with the debug overlay |
| `Sync-DevRun.cmd -FastTest -FastSuite Wpf` | Run an explicit suite even when no commits were pulled |
| `Sync-DevRun.cmd -FastTest -FastSuite Wpf,Translation` | Run selected explicit suites, not all tests |
| `Sync-DevRun.cmd -Choose` | Select a local worktree or a remote-only branch |
| `Sync-DevRun.cmd -Branch codex/example -FastTest` | Target a named branch, creating a worktree with confirmation if needed |

**Branch detection:** On normal launch, one newly detected remote-only `codex/*` branch prompts Yes/No; several show a numbered list. The first scan establishes a snapshot, since older remote branches cannot reliably be called new. Once selected, the worktree choice is saved in the shared Git `.git/kotrans2-sync-devrun.json`; ignored branches do not repeatedly prompt after the next successful sync. When another remote branch is selected, its worktree path is confirmed before creation. The launcher can run from the current unmerged `codex/csharp-migration` checkout — it does not require a copy in `main`.

**Git safety:** Only the approved `breadu/ko_trans2` origin is accepted. Uncommitted changes in the target worktree stop the pull. Diverged history stops at `git pull --ff-only`; no reset, force, rebase, automatic merge, push, or modification of `main`. The ordinary Codex commit/non-force push policy remains separate.

**FAST behavior:** On a normal pull, the launcher diffs the prior HEAD against the new HEAD and selects the relevant suites from the existing `scripts/Test-Fast.ps1`: `Foundation`, `Image`, `Settings`, `Ocr`, `Translation`, `Study`, `Wpf`, `Stability`, `GameQa`. For the first checkout of a branch, the comparison starts at its merge base with `main`. Unmapped/shared changes stop with an explanation; specify `-FastSuite` to make an informed manual choice. Documentation-only changes and subsequent runs with no new commits skip FAST rather than reporting false regression success. This is **focused diagnostic coverage**, not the full regression required before an explicit main merge. No Desktop test, real game capture, external AI call, model conversion, or release verification runs automatically.

**Running app:** Close KO Trans 2 normally before starting this workflow. The launcher now checks before pull/FAST/build and reports `ENVIRONMENT UNAVAILABLE` while the app is running, without changing the pending-test journal. Direct `Test-Fast.ps1 -Suite Wpf` has the same check before building the referenced app project. This is an execution-environment block, not a UI assertion failure; no process is stopped automatically. After closing the app, rerun the same command to perform the actual tests.

**Errors:** Application build output is written to `work/sync-devrun/build.log`; individual FAST logs to `work/sync-devrun/fast-<suite>.log` in the selected worktree. Failed builds/tests copy a limited excerpt (up to 50 matching error lines plus 80 final lines; capped at 30,000 characters) to the Windows clipboard. Copy failures do not hide the original test/build error.

**OCR models:** Actual model files remain excluded from Git. When launching a new worktree, the helper tries to reuse the model folder in an existing checkout via the already-supported `KO_TRANS2_MODELS` environment variable, without copying or downloading any weights. An explicitly configured model path always takes precedence. Missing models remain an application error, not a reason to generate fake results.

Requirements: Windows 10/11 x64, .NET 10 SDK, PowerShell 7, Git, access to the approved private origin, and an interactive desktop for GUI runs. Close an already running KO Trans 2 window first; no process is force-terminated.
