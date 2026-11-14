using System.ComponentModel;

namespace KoTrans.App;

public partial class MainWindow
{
    private readonly Func<GameWindowIdentity, CancellationToken, Task<GameLaunchOutcome>> launchGame;
    private readonly CancellationTokenSource gameLaunchLifetime = new();
    private bool gameLaunchPending;
    private void RefreshGameLaunchButton()
    {
        LaunchGameButton.Visibility = string.IsNullOrWhiteSpace(Current.GameWindow?.ExecutablePath) ? Visibility.Collapsed : Visibility.Visible;
        LaunchGameButton.IsEnabled = !gameLaunchPending && !closing && !closeApproved;
        LaunchGameButton.Content = gameLaunchPending ? "실행 중…" : "게임 실행";
    }
    private async void LaunchGameClick(object sender, RoutedEventArgs e) => await LaunchCurrentGameAsync();
    internal async Task LaunchCurrentGameAsync()
    {
        if (!ready || closing || closeApproved || gameLaunchPending || Current.GameWindow is not { } saved || string.IsNullOrWhiteSpace(saved.ExecutablePath)) return;
        var profile = Current.Id;
        gameLaunchPending = true; RefreshGameLaunchButton();
        try {
            var outcome = await launchGame(saved, gameLaunchLifetime.Token);
            if (closing || closeApproved || profile != Current.Id || Current.GameWindow != saved) return;
            var message = outcome == GameLaunchOutcome.Activated ? "실행 중인 게임 창을 열었어요" : "게임 실행을 요청했어요";
            ShowCompletionStatus(message);
            ShowFeedback(FeedbackKind.Success, message, $"{TranslationButtonCaption(false)}을 눌러 인식을 시작하세요.", replaceError: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is TranslationException or IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) {
            if (closing || closeApproved || profile != Current.Id || Current.GameWindow != saved) return;
            var reason = ex is TranslationException ? ex.Message
                : ex is Win32Exception { NativeErrorCode: 1223 } ? "게임 실행이 취소되었습니다."
                : "게임 실행 파일의 접근 권한과 실행 환경을 확인해 주세요. 런처나 실행 인자가 필요한 게임은 직접 실행해 주세요.";
            model.Status = reason; ShowFeedback(FeedbackKind.Error, "게임을 실행하지 못했어요", reason, replaceError: true);
        }
        finally { gameLaunchPending = false; if (!closing && !closeApproved) RefreshGameLaunchButton(); }
    }
}
