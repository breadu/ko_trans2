using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class UiTransitionReviewTests
{
    internal static void NavigationDuringSave()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir);
        var writer = new SaveFeedbackTests.Writer(store); var settings = SaveFeedbackTests.Settings();
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], persistSettings: writer.Save));
        main.ApplyLoadedSettings(settings);
        try {
            foreach (bool selectInSidebar in new[] { true, false }) {
                main.ShowProfileSettingsPage(0); writer.Gate = new();
                var pending = ReturnToLauncher(main);
                Check(!pending.IsCompleted, "Navigation fixture did not hold the save");
                if (selectInSidebar) Named<ListBox>(main, "ProfilePages").SelectedIndex = 1;
                else main.ShowProfileSettingsPage(2); // Wizard/overlay navigation while a save is pending.
                writer.Gate.SetResult(); Complete(pending);
                Check(!pending.Result && Named<FrameworkElement>(main, "ProfileSettingsView").Visibility == Visibility.Visible
                    && Named<ListBox>(main, "ProfilePages").SelectedIndex == (selectInSidebar ? 1 : 2),
                    "An old save completion replaced the user's newer settings page with the launcher");
            }
            writer.Gate = null; writer.Fail = true;
            var failed = ReturnToLauncher(main); Complete(failed);
            Check(!failed.Result && Named<FrameworkElement>(main, "ProfileSettingsView").Visibility == Visibility.Visible,
                "Failed navigation save hid the settings page");
            writer.Fail = false; var normal = ReturnToLauncher(main); Complete(normal);
            Check(normal.Result && Named<FrameworkElement>(main, "LauncherView").Visibility == Visibility.Visible,
                "Uninterrupted successful navigation no longer reaches the launcher");
        } finally { writer.Gate?.TrySetResult(); writer.Fail = false; Close(main); Directory.Delete(dir, true); }
    }

    internal static void ChangedCaptureInputRejectsOldWork()
    {
        foreach (string change in new[] { "mode", "target", "scaling", "area" }) ChangedCaptureInputRejectsOldWork(change);
    }
    internal static void CaptureCompletionKeepsNewUiState()
    {
        string dir = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        main.ApplyLoadedSettings(settings); var model = (MainViewModel)main.DataContext;
        var frame = new ImageFrame(40, 20, 160, new byte[3200]);
        TaskCompletionSource<ImageFrame>? pending = null;
        try {
            foreach (string result in new[] { "success", "canceled", "failed" }) {
                Named<ComboBox>(main, "InputMode").SelectedIndex = 1;
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var capture = main.CaptureFrameAsync(_ => pending.Task);
                Named<ComboBox>(main, "InputMode").SelectedIndex = 2;
                model.Status = "new input status";
                if (result == "success") pending.SetResult(frame);
                else if (result == "canceled") pending.SetCanceled();
                else pending.SetException(new IOException("old capture failure"));
                Complete(capture);
                Check(model.Preview is null && Field("lastFrame").GetValue(main) is null && model.Status == "new input status",
                    $"Late capture {result} overwrote a new input's preview or status");
            }
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = main.CaptureFrameAsync(_ => pending.Task);
            Click(Button(main, "취소")); pending.SetResult(frame); Complete(canceled);
            Check(model.Status == "작업을 취소했습니다." && model.Preview is null,
                "Explicit cancellation retained a pending label or accepted the canceled frame");
            Complete(main.CaptureFrameAsync(_ => Task.FromException<ImageFrame>(new IOException("synthetic error"))));
            Check(model.Status.Contains("화면 캡처 실패"), "Current capture failure stopped reporting errors");
            Complete(main.CaptureFrameAsync(_ => Task.FromResult(frame)));
            Check(model.Preview is not null && ReferenceEquals(Field("lastFrame").GetValue(main), frame)
                && model.Status.Contains("캡처 완료"), "Current successful capture failed to update its preview");
        } finally { pending?.TrySetResult(frame); Close(main); Directory.Delete(dir, true); }
    }
    private static void ChangedCaptureInputRejectsOldWork(string change)
    {
        string dir = FeedbackTests.DirectoryPath(); var service = new HeldOcr();
        var descriptor = new OcrEngineDescriptor("ui-transition", "Mock OCR", "v1", ["en"], false, false, OcrCapabilities.Basic, false, true, "Synthetic");
        var entry = new OcrEngineRegistration(descriptor, _ => service, (_, _) => new(true, "Synthetic ready"));
        var chosen = new WindowTarget(new IntPtr(123), 123, 123, "Synthetic game", "synthetic", "C:\\synthetic\\game.exe", "SyntheticClass");
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        profile.OcrEngineId = descriptor.Id; profile.OcrModelPresetId = "default"; profile.ShowTranslation = false;
        profile.CaptureMode = CaptureMode.Window; profile.GameWindow = chosen.Identity;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [chosen],
            _ => new PixelRect(0, 0, 800, 600), ocrFactory: new(new([entry]), dir)));
        main.ApplyLoadedSettings(settings); var model = (MainViewModel)main.DataContext;
        using var capture = new CancellationTokenSource();
        try {
            Field("lastFrame").SetValue(main, new ImageFrame(40, 20, 160, new byte[3200]));
            model.Preview = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, new byte[3200], 160);
            Click(Button(main, "OCR 실행"));
            Check(service.Started && Field("ocrTask").GetValue(main) is Task { IsCompleted: false }, "OCR fixture did not hold real UI work");
            Field("captureCancellation").SetValue(main, capture);
            switch (change) {
                case "mode": Named<ComboBox>(main, "InputMode").SelectedIndex = 2; break;
                case "target": main.SelectGameWindow(chosen with { Handle = new IntPtr(124), ProcessId = 124, StartedTicks = 124 }); break;
                case "scaling": var option = Named<CheckBox>(main, "OriginalCaptureWhenScaled"); option.IsChecked = !option.IsChecked; break;
                case "area": Complete(main.ConfirmCaptureAreaAsync(new PixelRect(10, 10, 200, 100), profile.Id)); break;
            }
            Check(service.Token.IsCancellationRequested && capture.IsCancellationRequested,
                $"Changing {change} left OCR/capture work for the previous input alive");
            Check(Field("lastFrame").GetValue(main) is null && model.Preview is null, $"Changing {change} retained a previous-input preview");
            service.Gate.SetResult(); Until(() => Field("ocrTask").GetValue(main) is null, "Old OCR did not drain");
            Check(Named<TextBox>(main, "OcrText").Text.Length == 0, $"Late OCR from previous {change} populated the new input");
            Check(model.OcrExecution == "OCR: 대기", $"Changing {change} left an active OCR status after completion");
        } finally {
            service.Gate.TrySetResult(); Field("captureCancellation").SetValue(main, null);
            Until(() => Field("ocrTask").GetValue(main) is null, "OCR fixture failed to drain");
            Close(main); Directory.Delete(dir, true);
        }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static Task<bool> ReturnToLauncher(MainWindow main) => (Task<bool>)typeof(MainWindow)
        .GetMethod("ReturnToLauncherAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null)!;
    private sealed class HeldOcr : IOcrService
    {
        internal readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token; internal bool Started;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) {
            Token = token; Started = true;
            await Gate.Task; // Native work can finish after cancellation; UI must still reject the result.
            return new([new("old input", 1, new(0, 0, 40, 20))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
