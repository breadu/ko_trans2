using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class FeedbackTests
{
    internal static void ToastLifecycle()
    {
        string dir = DirectoryPath(); var now = TimeSpan.Zero;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [], feedbackClock: () => now));
        main.ApplyLoadedSettings(new AppSettings());
        var tray = new Tray(); main.AttachTray(tray);
        try {
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && main.FeedbackCount == 0, "Startup falsely reported an operation success");
            main.ShowFeedback(FeedbackKind.Success, "완료", "프로필"); Layout(main, 820, 485);
            Check(main.FeedbackToast.Visibility == Visibility.Visible && main.IsFeedbackTimerRunning, "Inline toast did not appear");
            now = TimeSpan.FromSeconds(2); main.ShowFeedback(FeedbackKind.Information, "새 안내", "저장 필요");
            now = TimeSpan.FromSeconds(2.6); main.AdvanceFeedback();
            Check(main.FeedbackToast.Message == "새 안내" && main.FeedbackToast.Visibility == Visibility.Visible, "Old expiry dismissed a newer toast");
            now = TimeSpan.FromSeconds(4.5); main.AdvanceFeedback();
            now = TimeSpan.FromSeconds(4.7); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && !main.IsFeedbackTimerRunning,
                "Transient toast did not finish fade/stop timer");
            main.ShowFeedback(FeedbackKind.Error, "저장 실패", "다시 시도"); int before = main.FeedbackCount;
            var model = (MainViewModel)main.DataContext; model.Status = "오류 상세는 상태바에 유지";
            now += TimeSpan.FromMilliseconds(2900); main.AdvanceFeedback();
            main.ShowFeedback(FeedbackKind.Information, "모델 변경");
            Check(main.FeedbackToast.Kind == FeedbackKind.Error && main.FeedbackToast.Visibility == Visibility.Visible && main.FeedbackCount == before,
                "Error disappeared before three seconds or was covered by information");
            now += TimeSpan.FromMilliseconds(300); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && !main.IsFeedbackTimerRunning && model.Status == "오류 상세는 상태바에 유지",
                "Error toast did not expire after three seconds or cleared error state/status");
            main.ShowFeedback(FeedbackKind.Error, "이전 오류"); now += TimeSpan.FromSeconds(2);
            main.ShowFeedback(FeedbackKind.Error, "새 오류"); now += TimeSpan.FromMilliseconds(1200); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Visible && main.FeedbackToast.Message == "새 오류",
                "Earlier error deadline dismissed a newer error");
            main.ShowProfileSettingsPage(0); Layout(main, 1060, 740);
            Check(ReferenceEquals(Named<ContentControl>(main, "SettingsFeedbackHost").Content, main.FeedbackToast), "Toast did not follow settings navigation");
            main.HideToTray(); now += TimeSpan.FromSeconds(2); main.RestoreMainWindow(); main.ShowLauncher(); Layout(main, 820, 485);
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && !main.IsFeedbackTimerRunning,
                "Tray round trip resurrected an expired error");
            main.ShowFeedback(FeedbackKind.Success, "재시도 완료", replaceError: true);
            main.HideToTray(); main.RestoreMainWindow();
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && !main.IsFeedbackTimerRunning, "Hidden transient feedback resurfaced from tray");
            main.ShowFeedback(FeedbackKind.Success, "종료 전"); main.DisposeTray(); Close(main);
            Check(!main.IsFeedbackTimerRunning && !main.FeedbackToast.HasAnimatedProperties, "Closing left feedback timer/animation alive");
        } finally { main.DisposeTray(); Close(main); Directory.Delete(dir, true); }
    }
    internal static void ToastLayout()
    {
        string dir = DirectoryPath(); var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        main.ApplyLoadedSettings(new AppSettings());
        try {
            foreach (var size in new[] { new Size(1060, 740), new Size(400, 620), new Size(640, 1008) }) {
                main.ApplyAvailableWindowSize(size); main.ShowProfileSettingsPage(0); Layout(main, size.Width, size.Height);
                var save = Button(main, "설정 저장"); var before = Bounds(main, save);
                main.ShowFeedback(FeedbackKind.Success, "설정이 저장되었어요", "길이가 긴 프로필 이름도 다른 컨트롤을 가리지 않아야 합니다");
                Layout(main, size.Width, size.Height);
                var toast = Bounds(main, main.FeedbackToast);
                Check(Bounds(main, save) == before, "Toast moved the save button");
                Check(toast.X >= 0 && toast.Y >= 0 && toast.Right <= size.Width + 1 && toast.Bottom <= size.Height + 1,
                    "Toast overflowed narrow/portrait content");
                foreach (var control in new FrameworkElement[] { save, Named<ComboBox>(main, "Profiles"), Named<Button>(main, "ProfileSettingsButton") })
                    Check(!toast.IntersectsWith(Bounds(main, control)), "Toast covered a primary action or profile selector");
                main.ShowLauncher(); Layout(main, size.Width, size.Height);
                var launcherToast = Bounds(main, main.FeedbackToast);
                Check(!launcherToast.IntersectsWith(Bounds(main, Named<Button>(main, "WatchButton"))), "Launcher toast covered automatic start");
            }
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void CompactOverlayLayout()
    {
        string dir = DirectoryPath(); var now = TimeSpan.Zero;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [], feedbackClock: () => now));
        main.ApplyLoadedSettings(new AppSettings());
        try {
            foreach (var size in new[] { new Size(1060, 740), new Size(400, 620), new Size(640, 1008) }) {
                main.ApplyAvailableWindowSize(size); main.ShowProfileSettingsPage(0); Layout(main, size.Width, size.Height);
                var heading = Named<Grid>(main, "SettingsHeading");
                var content = Named<DockPanel>(main, "ProfileContent");
                Check(Bounds(main, Named<TextBlock>(main, "ProfilePageHeading")).Y == Bounds(main, content).Y
                    && heading.ActualHeight < 70, "Settings still reserve a blank row above their title for feedback");
                var controls = new FrameworkElement[] { heading, Named<ScrollViewer>(main, "BasicPage"), Button(main, "설정 저장"),
                    Named<ListBox>(main, "ProfilePages"), Named<ComboBox>(main, "Profiles") };
                var before = controls.Select(c => Bounds(main, c)).ToArray();
                main.ShowFeedback(FeedbackKind.Success, "설정이 저장되었어요", "기본 프로필"); Layout(main, size.Width, size.Height);
                var host = Named<ContentControl>(main, "SettingsFeedbackHost");
                Check(host.Parent is Canvas { IsHitTestVisible: false } && !main.FeedbackToast.IsHitTestVisible,
                    "Toast does not use a passive overlay layer");
                Check(Bounds(main, main.FeedbackToast).IntersectsWith(Bounds(main, heading)), "Toast is not floating above existing heading content");
                for (int i = 0; i < controls.Length; i++) Check(before[i] == Bounds(main, controls[i]), "Showing feedback moved settings content");
                now += TimeSpan.FromSeconds(3); main.AdvanceFeedback(); Layout(main, size.Width, size.Height);
                for (int i = 0; i < controls.Length; i++) Check(before[i] == Bounds(main, controls[i]), "Dismissing feedback moved settings content");

                main.ShowLauncher();
                var chrome = DesktopWindows.ChromeSize(main);
                Layout(main, main.Width - chrome.Width, main.MinHeight - chrome.Height);
                double minimum = main.MinHeight;
                var title = Named<TextBlock>(main, "LauncherTitle");
                var launcherHeading = Named<Grid>(main, "LauncherHeading");
                Check(Math.Abs(launcherHeading.ActualHeight - title.ActualHeight) < 0.1 && launcherHeading.ActualHeight < 40,
                    "Launcher retained a fixed 44 DIP toast slot instead of its natural title height");
                var start = Named<Button>(main, "WatchButton"); var startBounds = Bounds(main, start);
                main.ShowFeedback(FeedbackKind.Information, "프로필로 전환했어요", "기본 프로필");
                Layout(main, main.Width - chrome.Width, minimum - chrome.Height);
                var label = Elements<TextBlock>(Named<StackPanel>(main, "LauncherContent")).First(t => t.Text == "번역 서비스");
                Check(Named<ContentControl>(main, "LauncherFeedbackHost").Parent is Canvas canvas
                    && canvas.Parent == Named<Grid>(main, "LauncherCardBody"), "Launcher toast is clipped inside the scroll viewport");
                Check(Bounds(main, main.FeedbackToast).Bottom <= Bounds(main, label).Top + 0.1,
                    "Launcher feedback covered service information below its title");
                Check(startBounds == Bounds(main, start) && main.MinHeight == minimum
                    && Named<ScrollViewer>(main, "LauncherScroll").ScrollableHeight < 0.1,
                    "Overlay feedback changed launcher minimum size, start button or scrolling");
                now += TimeSpan.FromSeconds(3); main.AdvanceFeedback();
            }
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static Rect Bounds(Window window, FrameworkElement control) => new(control.TranslatePoint(new Point(), (UIElement)window.Content), control.RenderSize);
    internal static string DirectoryPath()
    { string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Feedback", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    private sealed class Tray : IApplicationTray
    {
        public bool IsAvailable => true;
        public event Action? OpenRequested { add { } remove { } }
        public event Action? ExitRequested { add { } remove { } }
        public void Dispose() { }
    }
}
