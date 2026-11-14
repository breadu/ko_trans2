using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class RecognitionModeTests
{
    internal static void ProfileControls()
    {
        string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate();
        var game = data.CreateGameProfile("trigger game"); game.AutomaticWatch = false;
        var store = new SettingsStore(dir); var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        try {
            main.ShowProfileSettingsPage(4); Layout(main, 1060, 740);
            var watch = Named<RadioButton>(main, "AutomaticWatch"); var enabled = Named<RadioButton>(main, "GameTriggersEnabled");
            var group = Named<StackPanel>(main, "InputTriggerGroup"); var fields = Named<StackPanel>(main, "InputTriggerFields");
            var keys = Named<KeyboardTriggerEditor>(main, "TriggerKeys");
            Check(data.DefaultProfile.AutomaticWatch && watch.IsChecked == true && Equals(watch.Content, "화면 자동 인식 사용"), "Automatic recognition should remain ON by default");
            Check(group.IsEnabled && enabled.IsEnabled && !fields.IsEnabled && fields.Opacity == .45 && enabled.IsChecked == false && keys.Value == "Enter,Space",
                "Auto mode doesn't clearly disable triggers while preserving values");
            Check(Named<TextBlock>(main, "RecognitionModeHelp").Text == "번역 시작 후 화면 변화를 감지하고, 문장이 안정되면 자동으로 OCR·번역합니다."
                && Named<TextBlock>(main, "LauncherHotkeyHint").Text.Contains("시작·중지:") && Equals(Named<Button>(main, "WatchButton").Content, "번역 시작"),
                "Auto mode needs concise guidance without irrelevant saved-settings text");
            enabled.IsChecked = true;
            Check(group.IsEnabled && group.Opacity == 1 && fields.IsEnabled
                && Named<TextBlock>(main, "RecognitionModeHelp").Text == "번역 시작 후 등록한 키·마우스·게임패드 버튼을 누르면 화면을 확인하고, 문장이 안정되면 OCR·번역합니다.",
                "Trigger mode controls/help did not update to concise guidance");
            Check(watch.IsChecked == false && watch.GroupName == enabled.GroupName, "Radio choices are not exclusive");
            enabled.IsChecked = false;
            Check(group.IsEnabled && !fields.IsEnabled && fields.Opacity == .45 && Named<TextBlock>(main, "RecognitionModeHelp").Text.Contains("인식 방식이 선택되지"),
                "Both-off state is unclear or disables the checkbox needed to recover");
            enabled.IsChecked = true;
            var save = main.SaveCurrentProfileAsync(); Complete(save); Check(save.Result, "Trigger mode couldn't be saved");
            var loaded = store.LoadAsync(); Complete(loaded);
            Check(!loaded.Result.DefaultProfile.AutomaticWatch && loaded.Result.DefaultProfile.Triggers.Enabled
                && loaded.Result.DefaultProfile.Triggers.Keys == "Enter,Space", "Saving selected mode erased preserved trigger settings");
            var switching = main.SwitchProfileAsync(game); Complete(switching); Check(switching.Result, "Could not switch to trigger game");
            watch = Named<RadioButton>(main, "AutomaticWatch"); Check(watch.IsChecked == false, "Stored OFF was overwritten by default ON");
            watch.IsChecked = true; save = main.SaveCurrentProfileAsync(); Complete(save); Check(save.Result && game.AutomaticWatch && !game.Triggers.Enabled, "Game's ON selection didn't persist");
            switching = main.SwitchProfileAsync(data.DefaultProfile); Complete(switching);
            Check(switching.Result && Named<RadioButton>(main, "AutomaticWatch").IsChecked == false, "Game's ON selection leaked into another profile");
            Named<ComboBox>(main, "InputMode").SelectedIndex = 2; main.ShowProfileSettingsPage(4);
            Check(!Named<RadioButton>(main, "AutomaticWatch").IsEnabled && !Named<StackPanel>(main, "InputTriggerGroup").IsEnabled
                && Named<TextBlock>(main, "RecognitionModeHelp").Text.Contains("클립보드")
                && !Named<TextBlock>(main, "RecognitionModeHelp").Text.Contains("보존합니다"),
                "Clipboard mode must still exclude screen/triggers without distracting saved-setting notes");
            Check(Named<TextBlock>(main, "LauncherHotkeyHint").Text.Contains("시작·중지:") && !Named<TextBlock>(main, "LauncherHotkeyHint").Text.Contains("클립보드 감시"), "Launcher should keep the compact shortcut hint after changing capture mode");
            Named<ComboBox>(main, "InputMode").SelectedIndex = 1; main.ShowProfileSettingsPage(4);
            Check(Named<RadioButton>(main, "AutomaticWatch").IsEnabled && !data.DefaultProfile.AutomaticWatch
                && Named<StackPanel>(main, "InputTriggerGroup").IsEnabled, "Leaving clipboard mode lost stored trigger mode");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void RuntimeModes()
    {
        foreach (var mode in new[] { CaptureMode.Region, CaptureMode.Window, CaptureMode.Clipboard })
        foreach (bool watch in new[] { false, true })
        foreach (bool enabled in new[] { false, true }) {
            string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate(); var profile = data.DefaultProfile;
            profile.CaptureMode = mode; profile.AutomaticWatch = watch; profile.Triggers.Enabled = enabled;
            profile.TriggerDelayMs = 0; profile.ReadingMode = "NVL"; profile.ShowTranslation = false;
            var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
            var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
            var engine = new SyntheticOcr(); int captures = 0, inputStarts = 0;
            ImageFrame current = Frame(1);
            var pipeline = new TranslationPipeline(engine, null, _ => { Interlocked.Increment(ref captures); return Task.FromResult(Volatile.Read(ref current)); },
                profile.OcrOptions(), new(), new TranslationSession(), "", interval: TimeSpan.FromMilliseconds(30), settle: TimeSpan.Zero, translationEnabled: false);
            var statusUpdates = new System.Collections.Concurrent.ConcurrentQueue<PipelineUpdate>();
            pipeline.Updated += statusUpdates.Enqueue;
            try {
                Complete(main.RequestInputTriggerAsync()); Check(captures == 0, "Stopped game trigger started OCR");
                main.ObserveTranslation(pipeline, data.ActiveProfileId, watch: true);
                RecognitionInputPlan plan = default;
                main.StartProfileMonitoring(pipeline, true, value => { plan = value; inputStarts++; });
                Check(statusUpdates.Any(u => u.State == PipelineState.Starting && u.Message == "원문 인식 준비 중…")
                    && statusUpdates.Any(u => u.State == PipelineState.Watching && u.Message == (plan.WatchScreen ? "새 원문 대기 중…" : "입력 대기 중")),
                    "Original-only start displayed translation progress or missed its mode-specific waiting state");
                var instruction = Named<TextBlock>(overlay, "OverlayInstruction");
                Check((instruction.Visibility == Visibility.Visible) == plan.UseTriggers,
                    "First-trigger instruction does not match the production recognition plan");
                Check(inputStarts == 1 && (plan.WatchScreen ? 1 : 0) + (plan.UseTriggers ? 1 : 0) + (plan.WatchClipboard ? 1 : 0) <= 1,
                    "Production start requested overlapping screen/input/clipboard monitors");
                if (mode == CaptureMode.Clipboard) {
                    Check(plan.WatchClipboard && !plan.UseTriggers && !plan.WatchScreen, "Clipboard depends on screen/trigger preferences");
                    Complete(main.RequestInputTriggerAsync()); Complete(Task.Delay(70));
                    Check(captures == 0 && engine.Calls == 0, "Clipboard game trigger captured the screen");
                    pipeline.SubmitClipboard("Synthetic clipboard dialogue.");
                    Until(() => Named<TextBox>(main, "OcrText").Text.Contains("clipboard"), "Clipboard event wasn't processed independently");
                } else if (watch) {
                    Check(plan.WatchScreen && !plan.UseTriggers, "Auto mode registered input triggers");
                    Until(() => pipeline.TryGetRecognized(current, out _), "Auto mode didn't recognize a stable synthetic screen");
                    int before = engine.Calls; Complete(main.RequestInputTriggerAsync()); Complete(Task.Delay(70));
                    Check(engine.Calls == before, "Auto mode game trigger queued redundant OCR");
                } else {
                    Complete(Task.Delay(70)); Check(captures == 0, "Trigger/manual mode polled the screen without input");
                    Check(plan.UseTriggers == enabled && !plan.WatchScreen, "Stored trigger enabled flag was ignored");
                    Complete(main.RequestInputTriggerAsync());
                    if (enabled) {
                        Until(() => pipeline.TryGetRecognized(current, out _), "Trigger didn't request OCR after Start");
                        Check(instruction.Visibility == Visibility.Collapsed, "First trigger left the instruction covering the OCR source");
                        int previousOcr = engine.Calls; current = Frame(2); Complete(main.RequestInputTriggerAsync());
                        Until(() => pipeline.TryGetRecognized(current, out _), "Next trigger didn't recognize changed dialogue");
                        Check(engine.Calls > previousOcr && Named<TextBox>(main, "OcrText").Text.Contains("80."), "Changed dialogue trigger reused stale OCR text");
                        Check(instruction.Visibility == Visibility.Collapsed, "Next input redisplayed the first-trigger instruction");
                    } else Check(captures == 0 && engine.Calls == 0, "Disabled trigger started OCR");
                }
                int stoppedAt = captures;
                // Queue an input before Stop to ensure its delayed callback cannot revive the session.
                profile.TriggerDelayMs = 30; var queued = main.RequestInputTriggerAsync();
                Click(Named<Button>(overlay.Chrome, "OverlayToggleTranslation")); Complete(queued);
                Check(statusUpdates.Any(u => u.State == PipelineState.Stopped && u.Message == "원문 인식을 중지했습니다."),
                    "Original-only stop displayed a translation message");
                Complete(pipeline.DisposeAsync().AsTask()); int settledAt = captures;
                Complete(main.RequestInputTriggerAsync()); Complete(Task.Delay(40));
                Check(instruction.Visibility == Visibility.Visible && instruction.Text.Contains("시작"),
                    "Stopped session did not retain its prominent Start instruction");
                Check(!pipeline.IsRunning && captures == settledAt && (plan.UseTriggers ? settledAt == stoppedAt : settledAt <= stoppedAt + 1),
                    "Stop didn't cancel pending input or stopped triggers restarted OCR");
            } finally { Complete(pipeline.DisposeAsync().AsTask()); Close(overlay); Close(overlay.Chrome); Close(main); Directory.Delete(dir, true); }
        }
    }
    internal static void ManualAndLateInputs()
    {
        var p = new Profile { AutomaticWatch = false, Triggers = new() { Keys = "", MouseButton = "None", GamepadButtons = "" } };
        Check(RecognitionInputPlan.For(p, true) == default && RecognitionInputPlan.For(p, true).Label == "인식 방식 미설정", "Empty triggers falsely imply an automatic input source");
        p.AutomaticWatch = true; Check(RecognitionInputPlan.For(p, false) == default, "Manual OCR starts a monitoring session");
        string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate();
        data.DefaultProfile.AutomaticWatch = false; data.DefaultProfile.TriggerDelayMs = 30;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        int captures = 0; var engine = new SyntheticOcr();
        var pipeline = new TranslationPipeline(engine, null, _ => { Interlocked.Increment(ref captures); return Task.FromResult(Frame(1)); },
            new("en", "NVL", false), new(), new TranslationSession(), "", settle: TimeSpan.Zero, translationEnabled: false);
        try {
            main.ObserveTranslation(pipeline, data.ActiveProfileId, watch: true); main.StartProfileMonitoring(pipeline, true, _ => { });
            var queued = main.RequestInputTriggerAsync(); data.DefaultProfile.AutomaticWatch = true;
            Complete(queued); Check(captures == 0, "Delayed input ignored a changed recognition mode");
            Complete(pipeline.RequestManualAsync()); Until(() => engine.Calls > 0, "Explicit manual OCR was blocked by input mode");
        } finally { Complete(pipeline.DisposeAsync().AsTask()); Close(main); Directory.Delete(dir, true); }
    }
    private static ImageFrame Frame(byte value) => new(200, 100, 800, Enumerable.Repeat(checked((byte)(value * 40)), 80000).ToArray());
    private sealed class SyntheticOcr : IOcrService
    {
        internal int Calls;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new OcrResult([new("Stable synthetic dialogue " + frame.Pixels.Span[0] + ".", 1, new(0, 0, 200, 100))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
