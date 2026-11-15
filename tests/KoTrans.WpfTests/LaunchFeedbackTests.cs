using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class LaunchFeedbackTests
{
    internal static void RejectedVisibilityExpires()
    {
        string dir = FeedbackTests.DirectoryPath(); var now = TimeSpan.Zero;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [], feedbackClock: () => now));
        var settings = new AppSettings(); main.ApplyLoadedSettings(settings); main.ShowProfileSettingsPage(3);
        try {
            var ko = Named<CheckBox>(main, "ShowKorean"); var source = Named<CheckBox>(main, "ShowOriginal");
            ko.IsChecked = false; source.IsChecked = false;
            Check(source.IsChecked == true && settings.DefaultProfile.ShowOriginal && !settings.DefaultProfile.ShowTranslation,
                "Rejected uncheck did not restore the original valid profile state");
            now += TimeSpan.FromMilliseconds(1400); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Visible, "Validation warning expired before it could be read");
            now += TimeSpan.FromMilliseconds(300); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && !main.IsFeedbackTimerRunning,
                "Restored valid settings left a persistent validation warning");
            source.IsChecked = false; now += TimeSpan.FromSeconds(1); main.AdvanceFeedback(); source.IsChecked = false;
            now += TimeSpan.FromMilliseconds(700); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Visible, "An earlier warning deadline dismissed a repeated warning");
            main.ShowFeedback(FeedbackKind.Error, "저장 실패는 유지", replaceError: true);
            now += TimeSpan.FromMilliseconds(1700); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Visible && main.FeedbackToast.Message == "저장 실패는 유지",
                "A short validation deadline dismissed a newer three-second error");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void StartFailureExplainsSource()
    {
        AuditSafetyTests.Run();
        string dir = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        var target = new WindowTarget(new IntPtr(1), 1, 1, "Synthetic Game", "synthetic", "", "class");
        profile.CaptureMode = CaptureMode.Window; profile.GameWindow = target.Identity;
        profile.Translation = new() { Provider = TranslationProvider.OpenAI, Model = "synthetic-model" };
        // If startup tries credentials before checking the missing game, this is not a usable key.
        File.WriteAllBytes(Path.Combine(dir, "OpenAI.key"), [1, 2, 3]);
        var candidates = new List<WindowTarget>();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => candidates,
            _ => throw new CaptureUnavailableException("대상 게임 창이 최소화되어 있습니다. 창을 복원하세요.")));
        main.ApplyLoadedSettings(settings);
        try {
            Click(Named<Button>(main, "WatchButton"));
            Until(() => main.FeedbackToast.Visibility == Visibility.Visible, "Missing game start failure was only shown in status bar");
            Check(main.FeedbackToast.Kind == FeedbackKind.Error && main.FeedbackToast.Message.Contains("게임 창을 확인")
                && main.FeedbackToast.Detail.Contains("게임을 실행") && main.FeedbackToast.Detail.Contains("다시 선택"), "Missing game start failure lacks a clear reason/action");
            Check(!main.IsInTray && Equals(Named<Button>(main, "WatchButton").Content, "번역 시작"), "Failed start hid the main window or left Stop enabled");
            string reason = ((MainViewModel)main.DataContext).Status;
            Complete(main.SaveCurrentProfileAsync());
            Check(((MainViewModel)main.DataContext).Status == reason && main.FeedbackToast.Visibility == Visibility.Visible,
                "Internal save overwrote source failure reason/toast");
            profile.GameWindow = null; main.ApplyLoadedSettings(settings);
            var missing = main.StartTranslationAsync(true); Complete(missing);
            Check(!missing.Result && main.FeedbackToast.Message.Contains("대상 게임 창을 선택") && main.FeedbackToast.Detail.Contains("캡처 · OCR"),
                "Unselected window does not explain how to choose a target");
            profile.GameWindow = target.Identity; candidates.Add(target); candidates.Add(target with { Handle = new IntPtr(2) }); main.ApplyLoadedSettings(settings);
            var ambiguous = main.StartTranslationAsync(true); Complete(ambiguous);
            Check(!ambiguous.Result && main.FeedbackToast.Detail.Contains("여러 개") && !main.FeedbackToast.Detail.Contains("게임을 실행"),
                "Ambiguous game window was misreported as a missing game");
            candidates.RemoveAt(1); main.ApplyLoadedSettings(settings);
            var minimized = main.StartTranslationAsync(true); Complete(minimized);
            Check(!minimized.Result && main.FeedbackToast.Detail.Contains("최소화") && main.FeedbackToast.Detail.Contains("복원"),
                "Minimized target did not explain restore action before creating an overlay/API service");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void SavePreservesUsefulStatus()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new SaveFeedbackTests.Writer(store);
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], persistSettings: writer.Save)); var settings = new AppSettings(); main.ApplyLoadedSettings(settings);
        var model = (MainViewModel)main.DataContext;
        try {
            Check(model.Status.Contains("대상 게임 창을 선택") && !model.Status.Contains("저장했습니다"), "Startup lacks a useful capture setup hint");
            string before = model.Status; Complete(main.SaveCurrentProfileAsync());
            Check(model.Status == before && main.FeedbackCount == 0, "Internal save replaced readiness hint with completion noise");
            writer.Gate = new(); var pending = main.SaveCurrentProfileAsync(); model.Status = "다음 문장을 기다리는 중";
            writer.Gate.SetResult(); Complete(pending);
            Check(model.Status == "다음 문장을 기다리는 중", "Late save completion overwrote current processing status");
            Complete(main.SaveWithFeedbackAsync());
            Check(main.FeedbackToast.Kind == FeedbackKind.Success && model.Status == "다음 문장을 기다리는 중", "Explicit save lost confirmation or replaced processing state");
            writer.Fail = true; var failed = main.SaveCurrentProfileAsync(); Complete(failed);
            Check(!failed.Result && model.Status == "설정 저장 실패 · 저장 위치와 권한을 확인하세요.", "Silent save hid a genuine persistence error");
            writer.Fail = false; Complete(main.SaveCurrentProfileAsync());
            Check(!model.Status.Contains("설정 저장 실패") && !model.Status.Contains("저장했습니다"), "Recovery retained obsolete save failure/noise");
            writer.Fail = true; Complete(main.SaveCurrentProfileAsync()); writer.Fail = false;
            main.ShowFeedback(FeedbackKind.Error, "게임 창 오류", "게임을 실행하세요", replaceError: true); model.Status = "다른 작업의 현재 오류";
            Complete(main.SaveCurrentProfileAsync());
            Check(main.FeedbackToast.Visibility == Visibility.Visible && main.FeedbackToast.Message == "게임 창 오류" && model.Status == "다른 작업의 현재 오류",
                "Recovered persistence error erased another current error");
            var game = settings.CreateGameProfile("Game"); Complete(main.SwitchProfileAsync(game));
            Named<TextBox>(main, "ProfileName").Text = ""; var invalid = main.SaveWithFeedbackAsync(); Complete(invalid);
            Check(!invalid.Result && model.Status.Contains("1–80"), "Invalid profile name did not report validation reason");
            Named<TextBox>(main, "ProfileName").Text = "Game"; Complete(main.SaveCurrentProfileAsync());
            Check(!model.Status.Contains("1–80") && !model.Status.Contains("저장했습니다") && main.FeedbackToast.Visibility == Visibility.Hidden,
                "Corrected save left its obsolete validation error in the status bar");
        } finally { writer.Fail = false; writer.Gate?.TrySetResult(); Close(main); Directory.Delete(dir, true); }
    }
}
