using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class TranslationStartupUiTests
{
    private sealed class Ocr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) => throw new Exception("No capture/OCR expected");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static void Run()
    {
        LocalPreparation(); OriginalOnly();
    }
    private static void LocalPreparation()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.StartupUi", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        profile.CaptureMode = CaptureMode.Clipboard; profile.Translation.Provider = TranslationProvider.HyMtLocal;
        var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => [])); main.ApplyLoadedSettings(settings);
        main.AttachTray(new TrayTests.Tray());
        int cleanupFailures = 0;
        main.ConfirmShutdownFailureOverride = _ => { cleanupFailures++; return true; };
        var window = Track(main.CreateOverlayWindow()); Track(window.Chrome);
        bool presented = false, rendered = false; int presents = 0, prepared = 0, resumed = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        main.PresentStartingOverlayOverride = actual => {
            Check(ReferenceEquals(actual, window), "Preparation replaced existing overlay"); presented = true; presents++;
            Check(main.IsInTray, "Launcher presented the preparation overlay before hiding the main window");
            Check(Named<TextBlock>(window, "OverlayInstruction").Text.Contains("준비"), "Overlay presented before preparing instruction");
            main.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => rendered = true));
        };
        main.PrepareLocalOverride = token => {
            prepared++; Check(presented && rendered, "Heavy preparation ran before overlay presentation/render opportunity");
            return gate.Task.WaitAsync(token);
        };
        main.StartPreparedLocalOverride = () => {
            resumed++;
            var active = main.CreateProfilePipeline(new Ocr(), new MockTranslationService(), _ => throw new Exception("No capture expected"));
            main.ObserveTranslation(active, profile.Id, watch: true); active.Start(false);
            window.BeginSession(true); return Task.FromResult(true);
        };
        try {
            window.SetResult("stale source", "old translation", "old notice");
            var start = main.StartMainTranslationAsync(true); Until(() => prepared == 1, "Preparation did not begin");
            Layout(window, 680, 270);
            var guide = Named<TextBlock>(window, "OverlayInstruction");
            Check(!start.IsCompleted && guide.Visibility == Visibility.Visible && guide.Text.Contains("번역 준비 중"), "Pending start has no visible preparing instruction");
            Check(Named<TextBlock>(window, "OverlayTranslation").Text == "" && Named<DictionarySubtitle>(window, "OverlayOriginal").Text == "", "Preparing message mixed with old OCR/translation data");
            Check(!Named<Button>(window.Chrome, "OverlayToggleTranslation").IsEnabled, "Pending start allows duplicate input");
            var duplicate = main.StartTranslationAsync(true); Complete(duplicate); Check(!duplicate.Result && presents == 1, "Repeated Start duplicated preparation/overlay");
            gate.SetResult(true); Complete(start);
            Check(start.Result && resumed == 1 && guide.Visibility == Visibility.Visible && guide.Text.Contains("지정한 키나 버튼") && !guide.Text.Contains("준비"), "Ready session did not replace preparation with trigger guidance");
            main.PrepareLocalOverride = _ => Task.FromResult(false);
            start = main.StartMainTranslationAsync(true); Complete(start);
            Check(!start.Result && guide.Text.Contains("시작") && !guide.Text.Contains("준비"), "Declined preparation left the preparing instruction stuck");
            Check(!main.IsInTray, "Declined preparation did not restore the main window");
            main.PrepareLocalOverride = _ => Task.FromException<bool>(new TranslationException("합성 준비 실패"));
            start = main.StartMainTranslationAsync(true); Complete(start);
            Check(!start.Result && !guide.Text.Contains("준비") && Named<TextBlock>(window, "OverlayNotice").Text.Contains("로컬 AI 모델을 준비하지 못"), "Failed preparation hid actionable guidance or retained preparing state");
            bool cancelPending = false;
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously); main.PrepareLocalOverride = token => { cancelPending = true; return gate.Task.WaitAsync(token); };
            start = main.StartMainTranslationAsync(true); Until(() => cancelPending, "Cancellation fixture not preparing");
            Complete(window.CloseForExitAsync()); Complete(start);
            Check(!start.Result && resumed == 1, "Closing preparation overlay resumed translation");
        } finally {
            main.DisposeTray(); Close(main);
            Close(window); Close(window.Chrome); if (Directory.Exists(root)) Directory.Delete(root, true);
            Check(cleanupFailures == 0, "Canceled/handled startup work became a cleanup failure");
        }
    }
    private static void OriginalOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.StartupSource", Guid.NewGuid().ToString("N"));
        bool presented = false, rendered = false, created = false;
        var descriptor = new OcrEngineDescriptor("startup-fixture", "Mock OCR", "v1", ["en"], false, false, OcrCapabilities.Basic, false, true, "Synthetic");
        var registration = new OcrEngineRegistration(descriptor, _ => { Check(presented && rendered, "OCR service created before preparation overlay rendered"); created = true; return new Ocr(); }, (_, _) => new(true, "Mock ready"));
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        profile.CaptureMode = CaptureMode.Clipboard; profile.ShowTranslation = false; profile.OcrEngineId = descriptor.Id; profile.OcrModelPresetId = "default";
        var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => [], ocrFactory: new(new([registration]), root)));
        int cleanupFailures = 0; main.ConfirmShutdownFailureOverride = _ => { cleanupFailures++; return true; };
        main.ApplyLoadedSettings(settings); var window = Track(main.CreateOverlayWindow()); Track(window.Chrome);
        main.PresentStartingOverlayOverride = _ => {
            presented = true; Check(Named<TextBlock>(window, "OverlayInstruction").Text.Contains("원문 인식"), "OCR-only start uses wrong preparation label");
            main.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => rendered = true));
        };
        try {
            var start = main.StartTranslationAsync(false); Complete(start);
            Check(start.Result && created && Named<TextBlock>(window, "OverlayInstruction").Visibility == Visibility.Collapsed, "OCR-only ready session left preparation visible");
        } finally {
            Close(main);
            Close(window); Close(window.Chrome); if (Directory.Exists(root)) Directory.Delete(root, true);
            Check(cleanupFailures == 0, "Source-only startup failed cleanup");
        }
    }
}
