using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class FeedbackRegressionTests
{
    internal static void SavingGeometry()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new SaveFeedbackTests.Writer(store);
        var now = TimeSpan.Zero; var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], feedbackClock: () => now, persistSettings: writer.Save));
        main.ApplyLoadedSettings(SaveFeedbackTests.Settings());
        try {
            foreach (var size in new[] { new Size(1060, 740), new Size(400, 620), new Size(640, 1008) }) {
                main.ApplyAvailableWindowSize(size); main.ShowProfileSettingsPage(0); Layout(main, size.Width, size.Height);
                var button = Named<Button>(main, "SaveSettingsButton"); var bounds = FeedbackTests.Bounds(main, button);
                writer.Gate = new(); var task = main.SaveWithFeedbackAsync(); now += TimeSpan.FromMilliseconds(130); main.AdvanceFeedback();
                Layout(main, size.Width, size.Height);
                Check(FeedbackTests.Bounds(main, button) == bounds && bounds.Right <= size.Width && bounds.Bottom <= size.Height,
                    "Loading caption moved/clipped responsive save action");
                writer.Gate.SetResult(); Complete(task); Layout(main, size.Width, size.Height);
                Check(FeedbackTests.Bounds(main, button) == bounds && Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Visible,
                    "Completion caption moved/clipped responsive save action");
                Check(!FeedbackTests.Bounds(main, main.FeedbackToast).IntersectsWith(bounds), "Completion toast covered the save button");
                now += TimeSpan.FromSeconds(3); main.AdvanceFeedback(); Layout(main, size.Width, size.Height);
                Check(FeedbackTests.Bounds(main, button) == bounds && Named<TextBlock>(main, "SaveCaption").Text == "설정 저장",
                    "Completion expiry changed responsive save action geometry");
            }
        } finally { writer.Gate?.TrySetResult(); Close(main); Directory.Delete(dir, true); }
    }
    internal static void StaleSavesAndLifecycle()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new SaveFeedbackTests.Writer(store) { Gate = new() };
        var now = TimeSpan.Zero; var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], feedbackClock: () => now, persistSettings: writer.Save));
        main.ApplyLoadedSettings(SaveFeedbackTests.Settings());
        try {
            var held = writer.Gate; var old = main.SaveWithFeedbackAsync();
            writer.Gate = null; writer.Fail = true; Named<Slider>(main, "FontSlider").Value = 30;
            var newer = main.SaveCurrentProfileAsync(); Complete(newer);
            held.SetResult(); Complete(old);
            Check(old.Result && !newer.Result && main.FeedbackToast.Kind == FeedbackKind.Error
                && ((MainViewModel)main.DataContext).Status == "설정 저장 실패 · 저장 위치와 권한을 확인하세요."
                && Named<TextBlock>(main, "LastSaveFailure").ToolTip.ToString()!.Contains("파일 사용 여부")
                && !Named<TextBlock>(main, "LastSaveFailure").ToolTip.ToString()!.Contains("Synthetic save denied"),
                "Older success erased a newer save failure or its detailed status");
            writer.Fail = false; Complete(main.SaveWithFeedbackAsync());
            int notices = main.FeedbackCount;
            string settledStatus = ((MainViewModel)main.DataContext).Status;
            writer.Gate = new(); writer.Fail = true; held = writer.Gate; old = main.SaveWithFeedbackAsync();
            writer.Gate = null; writer.Fail = false; newer = main.SaveCurrentProfileAsync(); Complete(newer);
            held.SetResult(); Complete(old);
            Check(!old.Result && newer.Result && main.FeedbackCount == notices && main.FeedbackToast.Kind == FeedbackKind.Success
                && ((MainViewModel)main.DataContext).Status == settledStatus, "Older failure replaced a newer successful save");

            var tray = new Tray(); main.AttachTray(tray);
            writer.Gate = new(); var pending = main.SaveWithFeedbackAsync();
            now += TimeSpan.FromMilliseconds(150); main.AdvanceFeedback();
            main.HideToTray();
            Check(!main.IsFeedbackTimerRunning && !((RotateTransform)Named<System.Windows.Shapes.Path>(main, "SaveSpinner").RenderTransform).HasAnimatedProperties,
                "Tray hiding left loading animation active");
            notices = main.FeedbackCount; writer.Gate.SetResult(); Complete(pending); main.RestoreMainWindow();
            Check(main.FeedbackCount == notices && main.FeedbackToast.Visibility == Visibility.Hidden, "Background completion resurfaced a stale success after tray restore");

            writer.Gate = new(); pending = main.SaveWithFeedbackAsync(); main.DisposeTray(); main.Close();
            writer.Gate.SetResult(); Complete(pending);
            Until(() => !Application.Current.Windows.Cast<Window>().Contains(main), "Closing during save did not settle");
            Check(!main.IsFeedbackTimerRunning && !Named<TextBlock>(main, "SaveCheck").HasAnimatedProperties,
                "Closing left completion timer/animation alive");
        } finally { writer.Fail = false; writer.Gate?.TrySetResult(); main.DisposeTray(); Close(main); Directory.Delete(dir, true); }
    }
    private sealed class Tray : IApplicationTray
    {
        public bool IsAvailable => true;
        public event Action? OpenRequested { add { } remove { } }
        public event Action? ExitRequested { add { } remove { } }
        public void Dispose() { }
    }
}
