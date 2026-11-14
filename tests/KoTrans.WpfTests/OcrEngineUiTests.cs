using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class OcrEngineUiTests
{
    internal static void Selection()
    {
        Check(MainWindow.OcrExecutionLabel(OcrExecutionStatus.Cpu, false) == "OCR: 대기"
            && MainWindow.OcrExecutionLabel(OcrExecutionStatus.Cpu, true) == "OCR: CPU"
            && MainWindow.OcrExecutionLabel(new("Preparing"), true) == "OCR: GPU 준비 중"
            && MainWindow.OcrExecutionLabel(new("DirectML", "Synthetic GPU"), true) == "OCR: GPU"
            && MainWindow.OcrExecutionLabel(new("CPU", Reason: "request-timeout"), true) == "OCR: CPU (GPU 전환 실패)"
            && MainWindow.OcrExecutionLabel(new("Error"), true) == "OCR: 오류"
            && MainWindow.OcrExecutionLabel(new("Error"), false) == "OCR: 오류",
            "OCR backend label did not distinguish idle, CPU, GPU preparation, GPU, fallback and error");
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.OcrUi", Guid.NewGuid().ToString("N"));
        var descriptor = new OcrEngineDescriptor("fake-third", "Third test OCR", "test-v1", ["en"], false, false, OcrCapabilities.Basic, false, true, "Test only");
        var fake = new FakeOcr();
        OcrModelPresetRegistration Preset(string id) => new(new("fake-third", id, "Test " + id, ["en"], "Test preset"),
            (_, _) => fake, (_, _) => new(true, "Synthetic model ready"));
        var registration = new OcrEngineRegistration(descriptor, _ => fake, (_, _) => new(true, "Synthetic model ready"), Presets: [Preset("default"), Preset("next")]);
        var factory = new OcrServiceFactory(new(OcrEngineCatalog.Default.Engines.Select(d => OcrEngineCatalog.Default.Get(d.Id)).Append(registration)), directory);
        var store = new SettingsStore(directory); var settings = new AppSettings(); settings.Validate();
        var game = settings.CreateGameProfile("Third engine game");
        var window = Track(new MainWindow(store, new ApiKeyStore(directory), () => [], ocrFactory: factory));
        try
        {
            window.ApplyLoadedSettings(settings); Complete(window.SwitchProfileAsync(game));
            var gpuOption = (CheckBox)window.FindName("OcrGpuAcceleration");
            Check(gpuOption.IsChecked == false && gpuOption.IsEnabled && gpuOption.Opacity >= 0.99
                && !game.OcrGpuAcceleration, "Default GPU option should be available at full contrast");
            gpuOption.IsChecked = true;
            Check(game.OcrGpuAcceleration && !settings.DefaultProfile.OcrGpuAcceleration && window.CurrentOcrService().State == OcrState.NotConfigured, "GPU preference loaded models or changed another profile");
            var choices = OcrChoices(window);
            Check(choices.Items.OfType<ComboBoxItem>().Count() == factory.Presets.Presets.Count, "Catalog did not generate model choices");
            Check(choices.Items[0] is ComboBoxItem {Tag: OcrModelPresetDescriptor {Id:"v6-medium-full"}}
                && ReferenceEquals(choices.Items[0], choices.SelectedItem), "v6 is not the first/default UI choice");
            var modelDescription = Named<TextBlock>(window, "OcrModelDescription");
            var readiness = Named<TextBlock>(window, "OcrEngineStatus");
            Check(modelDescription.Text.Contains("정확도") && modelDescription.Text.Contains("시간")
                && ((ComboBoxItem)choices.Items[0]).Content.ToString()!.Contains("정확도 우선")
                && modelDescription.ToolTip is null,
                "Default OCR model must explain its tradeoff without a redundant hover tooltip");
            var v5 = MainWindow.OcrModelPresentation(factory.Presets.Presets.Single(p => p.Id == "default" && p.EngineId == "paddle"));
            Check(v5.Label.Contains("v5") && v5.Label.Contains("속도 우선") && v5.Guidance.Contains("오인식"),
                "Legacy OCR should explain that its PP-OCRv5 family prioritizes speed");
            var blend = MainWindow.OcrModelPresentation(factory.Presets.Presets.Single(p => p.Id == "v5-det-v6-medium-rec"));
            Check(blend.Label.Contains("균형형") && blend.Guidance.Contains("글자 위치"),
                "Mixed v5/v6 configuration needs an understandable explanation");
            Check(OcrEngineCatalog.Default.Engines.Single().Id == "paddle"
                && choices.Items.OfType<ComboBoxItem>().Take(3).Select(item => ((OcrModelPresetDescriptor)item.Tag).Id)
                    .SequenceEqual(new[] {"v6-medium-full", "default", "v5-det-v6-medium-rec"}), "Production preset order changed");
            foreach (string preset in new[] {"default", "v5-det-v6-medium-rec"}) {
                ChooseOcr(window, "paddle", preset);
                Check(game.OcrModelPresetId == preset && gpuOption.IsEnabled, "Paddle selection or GPU contract changed");
                Complete(window.SaveCurrentProfileAsync());
                Complete(window.SwitchProfileAsync(settings.DefaultProfile));
                Complete(window.SwitchProfileAsync(game));
                Check(SelectedOcr(window).Tag is OcrModelPresetDescriptor saved && saved.Id == preset, "Paddle preset was not restored");
            }
            choices = OcrChoices(window);
            ChooseOcr(window, "fake-third", "next");
            Check(game.OcrEngineId == "fake-third" && game.OcrModelPresetId == "next"
                && choices.SelectedItem is ComboBoxItem {Tag: OcrModelPresetDescriptor {EngineId:"fake-third", Id:"next"}},
                "Selection not exclusive/persisted");
            Check(!gpuOption.IsEnabled && gpuOption.IsChecked == true && gpuOption.Opacity <= 0.55, "Unsupported GPU choice lost the saved preference");
            Check(modelDescription.Text == "Test preset" && readiness.Visibility == System.Windows.Visibility.Collapsed,
                "Ready OCR model must show its description without redundant file-ready text");
            Complete(window.SaveCurrentProfileAsync()); Complete(window.SwitchProfileAsync(settings.DefaultProfile)); Complete(window.SwitchProfileAsync(game));
            Check(SelectedOcr(window).Tag is OcrModelPresetDescriptor {EngineId:"fake-third", Id:"next"}, "Profile engine not restored");
            var load = store.LoadAsync(); Complete(load); Check(load.Result.Profiles.Single(p => p.Id == game.Id) is { OcrEngineId: "fake-third", OcrModelPresetId: "next" }, "Disk roundtrip lost engine");
            Check(load.Result.Profiles.Single(p => p.Id == game.Id).OcrGpuAcceleration, "GPU option lost in SettingsStore disk roundtrip");
            var ai = new FakeAi(); var frame = new ImageFrame(40, 20, 160, new byte[3200]);
            var pipeline = window.CreateProfilePipeline(window.CurrentOcrService(), ai, _ => Task.FromResult(frame));
            try { pipeline.Start(false); Complete(pipeline.RequestManualAsync()); Until(() => ai.Calls == 1, "Third engine pipeline not translated");
                Check(fake.Last?.EngineId == "fake-third" && fake.Last.ModelPresetId == "next" && fake.Last.SearchArea is null, "Pipeline did not respect registered engine/policy"); }
            finally { Complete(pipeline.DisposeAsync().AsTask()); }
            Complete(window.SwitchProfileAsync(settings.DefaultProfile)); Until(()=>fake.Disposed,"Profile switch did not retire engine");
            Complete(window.SwitchProfileAsync(game));
            ChooseOcr(window, "paddle", "v6-medium-full");
            Check(gpuOption.IsEnabled && gpuOption.Opacity >= 0.99 && gpuOption.IsChecked == true,
                "PaddleOCR v6 should restore the normal enabled GPU control and prior preference");
            Check(game.OcrOptions().ModelPresetId=="v6-medium-full"
                && readiness.Visibility == System.Windows.Visibility.Visible
                && readiness.Text.Contains("준비할 수 없습니다"),"Missing v6 silently fell back or its warning disappeared");
            Check(window.CurrentOcrService().State==OcrState.NotConfigured,"v6 selection loaded ONNX");
            Complete(window.SaveCurrentProfileAsync()); Complete(window.SwitchProfileAsync(settings.DefaultProfile));
            Check(settings.DefaultProfile.OcrModelPresetId=="v6-medium-full", "Another profile's model was changed");
            Complete(window.SwitchProfileAsync(game));
            Check(SelectedOcr(window).Tag is OcrModelPresetDescriptor {Id:"v6-medium-full"},"v6 selection was not restored");
            Complete(window.SwitchProfileAsync(settings.DefaultProfile)); game.OcrModelPresetId="future-model"; Complete(window.SwitchProfileAsync(game));
            Check(((TextBlock)window.FindName("OcrEngineStatus")).Text.Contains("future-model")
                && game.OcrModelPresetId=="future-model" && OcrChoices(window).SelectedIndex == -1,
                "Unknown model replaced or silently selected a fallback");
            Complete(window.SwitchProfileAsync(settings.DefaultProfile));
            game.OcrEngineId = "unknown-saved-engine"; Complete(window.SwitchProfileAsync(game));
            Check(((TextBlock)window.FindName("OcrEngineStatus")).Text.Contains("unknown-saved-engine")
                && game.OcrEngineId == "unknown-saved-engine" && OcrChoices(window).SelectedIndex == -1,
                "Unknown engine silently replaced");
        }
        finally { Close(window); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        GpuOptionStopsTranslation();
    }
    private static void GpuOptionStopsTranslation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KoTrans2.GpuUi", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings(); settings.Validate();
        var window = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => [],
            ocrFactory: new(OcrEngineCatalog.Default, directory)));
        try {
            window.ApplyLoadedSettings(settings); window.ShowProfileSettingsPage(1); Layout(window, 1060, 900);
            var gpu = (CheckBox)window.FindName("OcrGpuAcceleration");
            Check(gpu.Foreground is SolidColorBrush { Color: { R: > 180, G: > 180, B: > 180 } }, "GPU checkbox is unreadable on the dark background");
            var frame = new ImageFrame(40, 20, 160, new byte[3200]);
            foreach (bool enabled in new[] { true, false }) {
                var blocked = new BlockingOcr(); var ai = new FakeAi();
                var active = window.CreateProfilePipeline(blocked, ai, _ => Task.FromResult(frame));
                try {
                    window.ObserveTranslation(active, settings.ActiveProfileId, watch: true); active.Start(false);
                    var request = active.RequestManualAsync(); Until(() => blocked.Entered, "Synthetic OCR did not start");
                    gpu.IsChecked = enabled;
                    Check(!active.IsRunning && settings.DefaultProfile.OcrGpuAcceleration == enabled, "GPU toggle left the old translation running");
                    Check(window.FeedbackToast.Detail.Contains("중지") && window.FeedbackToast.Detail.Contains("다시 시작"), "GPU toggle hid stop/restart guidance");
                    blocked.Release.TrySetResult(); Complete(request);
                    Check(ai.Calls == 0 && !active.IsRunning && window.FeedbackToast.Detail.Contains("다시 시작"), "Retired OCR translated a stale result or restarted automatically");
                } finally { blocked.Release.TrySetResult(); Complete(active.DisposeAsync().AsTask()); }
                var nextOcr = new FakeOcr { Text = enabled ? "Synthetic GPU dialogue." : "Synthetic CPU dialogue." };
                var next = window.CreateProfilePipeline(nextOcr, ai, _ => Task.FromResult(frame));
                try {
                    window.ObserveTranslation(next, settings.ActiveProfileId, watch: true); next.Start(false);
                    Complete(next.RequestManualAsync()); Until(() => ai.Calls == 1, "Restarted pipeline did not translate");
                    Check(nextOcr.Last?.UseDirectMl == enabled, "Restart did not use the changed GPU setting");
                } finally { Complete(next.DisposeAsync().AsTask()); }
            }
        } finally { Close(window); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static ComboBox OcrChoices(MainWindow window) => Named<ComboBox>(window, "OcrEngineChoices");
    private static ComboBoxItem SelectedOcr(MainWindow window) =>
        OcrChoices(window).SelectedItem as ComboBoxItem
        ?? throw new InvalidOperationException("No OCR model is currently selected.");
    private static void ChooseOcr(MainWindow window, string engine, string preset)
    {
        var choices = OcrChoices(window);
        choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(item =>
            item.Tag is OcrModelPresetDescriptor descriptor
                && descriptor.EngineId == engine && descriptor.Id == preset);
    }

    private sealed class FakeOcr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        internal OcrOptions? Last; internal bool Disposed;
        internal string Text = "Complete synthetic dialogue.";
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        { Last=options; return Task.FromResult(new OcrResult([new(Text, 1, new(2,2,30,12))], TimeSpan.Zero)); }
        public ValueTask DisposeAsync() { Disposed=true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeAi : ITranslationService
    {
        internal int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new TranslationResult("합성 번역", TimeSpan.Zero, 0)); }
    }
    private sealed class BlockingOcr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        internal volatile bool Entered;
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            Entered = true; await Release.Task; // Native inference may finish after cancellation.
            return new([new("Late synthetic dialogue.", 1, new(2, 2, 30, 12))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
