using System.Diagnostics;
using System.Collections.Generic;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal enum GameLaunchOutcome { Requested, Activated }

internal static class GameLauncher
{
    internal static ProcessStartInfo PrepareStart(GameWindowIdentity game)
    {
        game.Validate();
        var executable = game.ExecutablePath;
        if (!Path.IsPathFullyQualified(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new TranslationException("저장된 게임 실행 파일 경로가 올바르지 않습니다. EXE 파일 위치를 확인해 주세요.");
        if (!File.Exists(executable)) throw new TranslationException("게임 실행 파일을 찾지 못했습니다. 파일이 이동·삭제되었는지 확인해 주세요.");
        return new ProcessStartInfo(Path.GetFullPath(executable)) {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!, UseShellExecute = true
        };
    }
    internal static IReadOnlyList<WindowTarget> FindExisting(GameWindowIdentity game, IReadOnlyList<WindowTarget> windows)
    {
        return WindowTargets.ReconnectionCandidates(game, windows);
    }
    internal static Task<GameLaunchOutcome> LaunchAsync(GameWindowIdentity game, CancellationToken token) => Task.Run(() => {
        token.ThrowIfCancellationRequested();
        var start = PrepareStart(game);
        var existing = FindExisting(game, WindowTargets.List());
        token.ThrowIfCancellationRequested();
        if (existing.Count > 1) throw new TranslationException("같은 실행 파일의 게임 창이 여러 개 열려 있습니다. 사용할 게임 창을 직접 선택해 주세요.");
        if (existing.Count == 1) {
            bool activated;
            try { activated = WindowTargets.ActivateForManual(existing[0]); }
            catch (CaptureUnavailableException) { activated = false; }
            if (!activated) throw new TranslationException("게임은 이미 실행 중이지만 창을 앞으로 가져오지 못했습니다. 작업 표시줄에서 게임 창을 복원·선택해 주세요.");
            return GameLaunchOutcome.Activated;
        }
        using var process = Process.Start(start);
        return GameLaunchOutcome.Requested;
    }, token);
}
