using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class ClipboardAndTrayTests
{
    internal static void Settings()
    {
        string dir = Temp(); var store = new SettingsStore(dir);
        var settings = new AppSettings(); settings.Validate();
        var first = settings.CreateGameProfile("first"); var second = settings.CreateGameProfile("second");
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], copyOcrText: _ => throw new Exception("Settings touched clipboard")));
        try {
            main.ApplyLoadedSettings(settings); main.ShowProfileSettingsPage(1);
            var box = Named<CheckBox>(main, "CopyOcrResultToClipboard");
            Check(box.IsChecked != true && !settings.DefaultProfile.CopyOcrToClipboard
                && !JsonSerializer.Deserialize<Profile>("{}")!.CopyOcrToClipboard, "Fresh/legacy default is not OFF");
            Check(Equals(box.Content, "OCR 결과 클립보드로 복사"), "Clipboard option text missing");
            Check(((SolidColorBrush)box.Foreground).Color == Color.FromRgb(0xD8, 0xE8, 0xF5),
                "Clipboard checkbox did not use the dark settings theme's readable foreground");
            var page = Named<ScrollViewer>(main, "CapturePage");
            var cards = ((StackPanel)page.Content).Children.OfType<Border>().ToArray();
            Check(cards.Select(card => Elements<TextBlock>(card).First().Text)
                .SequenceEqual(new[] { "게임 캡처", "OCR 모델" }),
                "Primary capture/OCR settings must be first and contain no advanced cards");
            var advancedPage = Named<ScrollViewer>(main, "AdvancedPage");
            var advancedCards = ((StackPanel)advancedPage.Content).Children.OfType<Border>().ToArray();
            Check(advancedCards.Select(card => Elements<TextBlock>(card).First().Text)
                .SequenceEqual(new[] { "클립보드", "확대 화면 캡처", "OCR 문제 해결" }),
                "Advanced settings should contain optional capture and OCR recovery options");
            Check(Elements<TextBlock>(cards[0]).Any(text => text.Text == "캡처 영역")
                && Elements<CaptureAreaMap>(cards[0]).Any()
                && Elements<CheckBox>(advancedCards[0]).Contains(box)
                && Elements<CheckBox>(advancedCards[1]).Any(option => option.Name == "OriginalCaptureWhenScaled"),
                "Capture area, clipboard, or scaled capture was placed in the wrong section");
            main.ShowProfileSettingsPage(6); Layout(main, 1060, 740);
            Check(advancedPage.Visibility == Visibility.Visible && page.Visibility == Visibility.Collapsed,
                "Advanced options should be accessible from their own navigation page");
            Complete(main.SwitchProfileAsync(first)); box.IsChecked = true;
            Check(first.CopyOcrToClipboard && !second.CopyOcrToClipboard, "Checkbox changed another profile");
            Complete(main.SwitchProfileAsync(second)); Check(box.IsChecked != true, "OFF profile restored ON");
            Complete(main.SwitchProfileAsync(first)); Check(box.IsChecked == true, "ON profile lost choice");
            Named<ComboBox>(main, "InputMode").SelectedIndex = 2;
            Check(!box.IsEnabled && box.IsChecked == true && first.CopyOcrToClipboard, "Clipboard mode lost saved OCR export preference");
            Named<ComboBox>(main, "InputMode").SelectedIndex = 1;
            Check(box.IsEnabled && box.IsChecked == true, "Returning to OCR did not restore checkbox");
            Click(Button(main, "설정 저장")); var load = store.LoadAsync(); Complete(load);
            Check(load.Result.Profiles.Single(p => p.Id == first.Id).CopyOcrToClipboard
                && !load.Result.Profiles.Single(p => p.Id == second.Id).CopyOcrToClipboard, "Profile copy preference did not roundtrip");
        } finally { Close(main); Directory.Delete(dir, true); }
    }

    internal static void AcceptedSource()
    {
        foreach (var scenario in new[] { "off", "on", "busy", "original-only", "clipboard-input" }) {
            string dir = Temp(); var settings = new AppSettings(); settings.Validate();
            var profile = settings.DefaultProfile; profile.CopyOcrToClipboard = scenario != "off";
            profile.CaptureMode = scenario == "clipboard-input" ? CaptureMode.Clipboard : CaptureMode.Region;
            profile.ShowTranslation = scenario != "original-only";
            int attempts = 0; var copies = new List<string>();
            var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [], copyOcrText: text => {
                attempts++; if (scenario == "busy") throw new ExternalException("synthetic clipboard lock"); copies.Add(text);
            }));
            main.ApplyLoadedSettings(settings);
            var ocr = new SyntheticOcr();
            var ai = new CheckedAi(() => {
                Check(scenario is "off" or "clipboard-input" ? attempts == 0 : attempts == 1,
                    "AI request started before clipboard acceptance or copied a non-OCR input");
            });
            var pipeline = main.CreateProfilePipeline(ocr, profile.ShowTranslation ? ai : null, _ => Task.FromResult(Frame()));
            main.ObserveTranslation(pipeline, profile.Id); pipeline.Start(false);
            try {
                if (scenario == "clipboard-input") pipeline.SubmitClipboard("Clipboard source text.", true);
                else Complete(pipeline.RequestManualAsync());
                Until(() => profile.ShowTranslation ? ai.Calls == 1 : attempts == 1, "Accepted source was not processed"); Pump();
                Check(attempts == (scenario is "off" or "clipboard-input" ? 0 : 1), "Incorrect clipboard write count");
                Check(copies.All(t => t == "Stable English dialogue."), "Clipboard received translation/candidate instead of OCR source");
                if (scenario == "busy") Check(main.FeedbackToast.Visibility == Visibility.Visible, "Clipboard failure was silently ignored");
                if (scenario == "original-only") Check(ai.Calls == 0 && copies.Count == 1, "Original-only OCR required AI or missed copy");
                if (scenario == "clipboard-input") Check(ocr.Calls == 0 && copies.Count == 0, "Clipboard input was OCRed or echoed");
                // Queue an accepted notification, then invalidate before UI execution.
                var queued = main.CopyAcceptedOcrAsync(pipeline, profile.Id, pipeline.Version, "Obsolete source", CancellationToken.None);
                pipeline.Stop(); Complete(queued);
                Check(!copies.Contains("Obsolete source"), "Stopped/old result overwrote clipboard");
            } finally { Complete(pipeline.DisposeAsync().AsTask()); Close(main); Directory.Delete(dir, true); }
        }
    }

    internal static void TrayCommand()
    {
        string dir = Temp(); var settings = new AppSettings(); settings.Validate();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        var tray = new CommandTray(); main.ApplyLoadedSettings(settings); main.AttachTray(tray);
        var pipeline = main.CreateProfilePipeline(new SyntheticOcr(), new CheckedAi(() => { }), _ => Task.FromResult(Frame()));
        try {
            Check(tray.Enabled && tray.Caption == "번역 시작" && Equals(Named<Button>(main, "WatchButton").Content, tray.Caption), "Initial tray/main captions differ");
            Named<CheckBox>(main, "ShowKorean").IsChecked = false;
            Check(tray.Caption == "원문 인식 시작", "Tray did not follow original-only mode");
            var other = settings.CreateGameProfile("other"); other.ShowTranslation = true;
            Complete(main.SwitchProfileAsync(other)); Check(tray.Caption == "번역 시작", "Tray caption stale after profile switch");
            main.ObserveTranslation(pipeline, other.Id, true); pipeline.Start(false);
            Until(() => tray.Caption == "번역 중지", "Running tray caption did not update");
            Check(Equals(Named<Button>(main, "WatchButton").Content, tray.Caption), "Running tray/main captions differ");
            tray.Command!(); Until(() => !pipeline.IsRunning, "Tray stop did not reach the main stop path");
            Check(tray.Caption == "번역 시작", "Stopped tray caption was stale");
            // A failed tray start uses the real main start validation without displaying HWNDs.
            Named<ComboBox>(main, "InputMode").SelectedIndex = 0;
            int notices = main.FeedbackCount; tray.Command!();
            Until(() => main.FeedbackCount > notices, "Tray start did not report missing game window");
            Check(tray.Enabled && tray.Caption == "번역 시작" && main.FeedbackToast.Visibility == Visibility.Visible,
                "Failed start left tray disabled or gave no explanation");
            main.DisposeTray(); Check(tray.Command is null && tray.Disposed, "Tray retained translation command after disposal");
        } finally { Complete(pipeline.DisposeAsync().AsTask()); main.DisposeTray(); Close(main); Directory.Delete(dir, true); }
    }
    private static string Temp() { string d = Path.Combine(Path.GetTempPath(), "KoTrans2.ClipboardTray", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    internal static void StableAndCanceled()
    {
        ImageFrame frame = Frame(); var later = new ImageFrame(100, 60, 400, Enumerable.Repeat((byte)255, 24000).ToArray());
        var copies = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var ocr = new GrowingOcr(() => frame = later);
        var ai = new CheckedAi(() => Check(copies.SequenceEqual(new[] { "Final stable sentence." }), "Draft was copied or AI ran before accepted-copy callback"));
        var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(frame), new("en", "ADV", false), new(), new(), "",
            interval: TimeSpan.FromMilliseconds(15), settle: TimeSpan.FromMilliseconds(140),
            onOcrAccepted: (_, text, _) => { copies.Enqueue(text); return Task.CompletedTask; });
        try {
            pipeline.Start(true); Until(() => ai.Calls == 1, "Stable automatic source was not translated");
            Complete(Task.Delay(180));
            Check(ocr.Calls >= 2 && copies.Count == 1 && ai.Calls == 1, "Unstable candidate/unchanged frame produced extra exports or AI requests");
        } finally { Complete(pipeline.DisposeAsync().AsTask()); }
        bool entered = false;
        var canceledAi = new CheckedAi(() => throw new Exception("Canceled acceptance reached AI"));
        pipeline = new TranslationPipeline(new SyntheticOcr(), canceledAi, _ => Task.FromResult(Frame()), new("en", "ADV", false), new(), new(), "",
            onOcrAccepted: (_, _, token) => { entered = true; return Task.Delay(Timeout.Infinite, token); });
        try {
            pipeline.Start(false); Complete(pipeline.RequestManualAsync()); Until(() => entered, "Acceptance hook was not entered");
            pipeline.Stop(); Complete(pipeline.DisposeAsync().AsTask());
            Check(canceledAi.Calls == 0, "Canceled clipboard acceptance leaked an AI request");
        } finally { Complete(pipeline.DisposeAsync().AsTask()); }
    }
    private static ImageFrame Frame() => new(100, 60, 400, new byte[24000]);
    private sealed class SyntheticOcr : IOcrService {
        public int Calls; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) {
            Calls++; return Task.FromResult(new OcrResult([new("Stable English dialogue.", 1, new(0, 0, 100, 30))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class GrowingOcr(Action advance) : IOcrService {
        public int Calls; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) {
            Calls++; bool draft = frame.Pixels.Span[0] == 0; if (draft) advance();
            return Task.FromResult(new OcrResult([new(draft ? "Draft fragment" : "Final stable sentence.", 1, new(0, 0, 100, 30))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CheckedAi(Action before) : ITranslationService {
        public int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) {
            before(); Calls++; return Task.FromResult(new TranslationResult("합성 번역", TimeSpan.Zero, 0));
        }
    }
    private sealed class CommandTray : IApplicationTray {
        public bool IsAvailable => !Disposed; public bool Disposed, Enabled; public string Caption = ""; public Action? Command;
        public event Action? OpenRequested { add { } remove { } }
        public event Action? ExitRequested { add { } remove { } }
        public void SetTranslationCommand(string caption, bool enabled, Action? execute) { Caption = caption; Enabled = enabled; Command = execute; }
        public void Dispose() { Disposed = true; Command = null; }
    }
}
