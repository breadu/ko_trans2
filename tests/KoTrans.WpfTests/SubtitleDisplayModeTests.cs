using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class SubtitleDisplayModeTests
{
    internal static void TooltipSynchronization()
    {
        var profile = new Profile { ShowOriginal = true, ShowTranslation = true };
        var overlay = Track(new TranslationWindow(profile)); Track(overlay.Chrome);
        try {
            var toggle = Named<Button>(overlay.Chrome, "OverlayToggleTranslation");
            var close = Named<Button>(overlay.Chrome, "CloseOverlay");
            var mode = Named<Button>(overlay.Chrome, "OverlayDisplayMode");
            int clicks = 0; overlay.ToggleTranslationRequested += () => clicks++;
            overlay.SetResult("Retained source.", "유지할 번역", "");
            foreach (bool running in new[] { false, true }) foreach (bool through in new[] { false, true }) {
                profile.ShowTranslation = true; profile.OverlayClickThrough = through; overlay.Apply(profile);
                overlay.SetAutomaticTranslation(running);
                foreach (bool translated in new[] { true, false, true }) {
                    profile.ShowTranslation = translated;
                    // Apply is also used by settings/display-only changes without a start/stop transition.
                    overlay.Apply(profile);
                    string action = translated ? "번역" : "원문 인식";
                    Check(Equals(toggle.ToolTip, action + (running ? " 중지" : " 시작")), "Display change retained the previous mode's start/stop tooltip");
                    Check(Equals(close.ToolTip, $"자막 닫기 · {action} 중지"), "Close tooltip did not follow the display mode");
                    Check(Equals(toggle.Content, running ? "중단" : "시작") && toggle.IsEnabled && mode.IsEnabled,
                        "Tooltip refresh altered button content or availability");
                    Layout(overlay, 320, 180); Layout(overlay.Chrome, 320, 180); Layout(overlay.Chrome, 320, 180);
                    var root = (Visual)overlay.Chrome.Content;
                    foreach (var button in new[] { toggle, close }) {
                        var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                        Check(bounds.Left >= 0 && bounds.Right <= 320 && overlay.Chrome.InputRegions(320, 180).Any(r => r.Contains(bounds.TopLeft + new Vector(5, 5))),
                            "Mode change lost a minimum-width command's input region");
                    }
                    int before = clicks; Click(toggle);
                    Check(clicks == before + 1 && overlay.SubtitleContent == ("Retained source.", "유지할 번역"),
                        "Mode refresh changed toggle dispatch or discarded subtitle content");
                }
            }
            overlay.SetAutomaticTranslation(false, starting: true);
            profile.ShowTranslation = false; overlay.Apply(profile);
            Check(!toggle.IsEnabled && !mode.IsEnabled && Equals(toggle.ToolTip, "원문 인식 시작"), "Tooltip refresh unlocked commands during startup");
            overlay.SetAutomaticTranslation(false);
            bool closed = false; overlay.Closed += (_, _) => closed = true;
            Click(close); Check(closed, "Close command stopped closing the overlay");
        } finally { Close(overlay); Close(overlay.Chrome); }
    }
    internal static void NoticePriority()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.DisplayNotices", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        profile.Translation.Provider = TranslationProvider.Gemini;
        profile.ShowOriginal = false; profile.ShowTranslation = true;
        var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => [])); main.ApplyLoadedSettings(settings);
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        var engine = new Engine(); var translator = new Translator { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var active = main.CreateProfilePipeline(engine, translator,
            _ => Task.FromResult(new ImageFrame(40, 20, 160, new byte[3200])));
        main.RestartSubtitleRecognitionOverride = () => throw new Exception("Display-only change restarted the pipeline");
        try {
            var notice = Named<TextBlock>(overlay, "OverlayNotice");
            var source = Named<DictionarySubtitle>(overlay, "OverlayOriginal");
            var translated = Named<TextBlock>(overlay, "OverlayTranslation");
            var mode = Named<Button>(overlay.Chrome, "OverlayDisplayMode");
            main.ObserveTranslation(active, profile.Id, watch: true); active.Start(false);
            overlay.SetResult("Failed source.", "이전 완료 번역", "");
            active.SubmitClipboard("Failed source.", manual: true);
            Until(() => translator.Calls == 1, "AI fixture did not start");
            translator.Pending.SetException(new TranslationException("synthetic AI detail"));
            Until(() => notice.Text.Contains("AI 서비스 요청에 실패"), "AI failure did not reach the overlay");
            string failure = notice.Text;
            translator.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Complete(main.CycleSubtitleDisplayAsync()); // translation -> both; same pipeline and cached translation
            Check(active.IsRunning && profile.ShowOriginal && profile.ShowTranslation && engine.Calls == 0,
                "Display-only failure fixture unexpectedly restarted or invoked OCR");
            Check(notice.Text == failure, "Display-only switch erased the unresolved AI error");
            Check(translator.Calls == 1, "Cached visibility change retried AI");
            active.SubmitClipboard("Recovered source.", manual: true);
            Until(() => translator.Calls == 2, "Recovery fixture did not start");
            overlay.SetResult("Live source.", "수신 중인 내용", "원문 인식 중…", complete: false);
            Check(notice.Text == failure && source.Text == "Live source." && translated.Text == "수신 중인 내용",
                "Notice protection blocked live subtitle content or yielded to a routine message");
            overlay.SetPipelineResult(new(active.Version, PipelineState.Recognizing, "OCR") { RecoveredAreas = PipelineStatusArea.Ocr });
            Check(notice.Text == failure, "OCR recovery cleared an unresolved AI failure");
            translator.Pending.SetResult(new("복구된 번역", TimeSpan.Zero));
            Until(() => translated.Text == "복구된 번역" && notice.Visibility == Visibility.Collapsed, "Real translation recovery did not clear the error");
            Named<CheckBox>(main, "ShowOriginal").IsChecked = false;
            Until(() => mode.IsEnabled, "Display change did not settle");
            overlay.SetPipelineResult(new(active.Version, PipelineState.Watching, "capture detail") {
                Important = true, FailureArea = PipelineStatusArea.Capture, OverlayMessage = "게임 창을 앞으로 가져오세요." });
            Complete(main.CycleSubtitleDisplayAsync());
            Check(notice.Text == "게임 창을 앞으로 가져오세요.", "Display-only switch erased a capture wait");
            overlay.SetPipelineResult(new(active.Version, PipelineState.Watching, "capture recovered") { RecoveredAreas = PipelineStatusArea.Capture });
            overlay.SetResult(null, null, "일반 안내");
            Check(notice.Text == "일반 안내", "A recovered warning blocked a normal notice");
            overlay.SetPipelineResult(new(active.Version, PipelineState.Error, "old error") { FailureArea = PipelineStatusArea.Translation });
            overlay.BeginSession(false);
            overlay.SetResult("New session.", "새 세션", "일반 안내");
            Check(notice.Text == "일반 안내" && source.Text == "New session.", "New session retained old errors or text");
            overlay.SetPipelineResult(new(active.Version, PipelineState.Error, "stop error") { FailureArea = PipelineStatusArea.Translation });
            Click(Named<Button>(overlay.Chrome, "OverlayToggleTranslation"));
            Check(!active.IsRunning && notice.Visibility == Visibility.Collapsed
                && Named<TextBlock>(overlay, "OverlayInstruction").Text.Contains("시작"), "Stopping retained an error over the stopped instruction");
            overlay.ReportInputFailure(5);
            string chromeError = notice.Text;
            overlay.SetResult("Still readable.", "정상 자막", "일반 안내");
            Check(notice.Text == chromeError && translated.Text == "정상 자막", "Direct UI notice erased the persistent chrome error");
        } finally {
            translator.Pending?.TrySetCanceled(); Complete(active.DisposeAsync().AsTask());
            Close(main); Close(overlay); Close(overlay.Chrome); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class Engine : IOcrService
    {
        public int Calls;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        { Calls++; return Task.FromResult(new OcrResult([new("Fresh source.", 1, new(0, 0, 30, 10))], TimeSpan.Zero)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Translator : ITranslationService
    {
        public int Calls;
        public TaskCompletionSource<TranslationResult>? Pending;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            Calls++;
            if (Pending is { } pending) { request.Progress?.Invoke("수신 중인 번역"); return pending.Task.WaitAsync(token); }
            return Task.FromResult(new TranslationResult("번역: " + request.Text, TimeSpan.Zero));
        }
    }
    internal static void LayoutAndCache()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.Display", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        profile.CaptureMode = CaptureMode.Region; profile.ShowOriginal = true; profile.ShowTranslation = true;
        var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => [])); main.ApplyLoadedSettings(settings);
        main.ConfirmShutdownFailureOverride = _ => throw new Exception("Unexpected cleanup failure");
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        var engine = new Engine(); var translator = new Translator();
        var sessions = new List<TranslationPipeline>(); int restarts = 0;
        TranslationPipeline NewPipeline() {
            var active = main.CreateProfilePipeline(engine, profile.ShowTranslation ? translator : null,
                _ => Task.FromResult(new ImageFrame(40, 20, 160, new byte[3200])));
            sessions.Add(active); main.ObserveTranslation(active, profile.Id, watch: true); return active;
        }
        main.RestartSubtitleRecognitionOverride = () => {
            restarts++;
            var content = overlay.SubtitleContent;
            var active = NewPipeline();
            if (content.Source.Length > 0) overlay.SetResult(content.Source, content.Translation, "");
            active.Start(false, profile.ShowTranslation && content.Translation.Length == 0 ? content.Source : null);
            return Task.FromResult(true);
        };
        try {
            var service = Named<Button>(overlay.Chrome, "OverlayService");
            var mode = Named<Button>(overlay.Chrome, "OverlayDisplayMode");
            var source = Named<DictionarySubtitle>(overlay, "OverlayOriginal");
            var translated = Named<TextBlock>(overlay, "OverlayTranslation");
            var originalCheck = Named<CheckBox>(main, "ShowOriginal"); var koreanCheck = Named<CheckBox>(main, "ShowKorean");
            foreach (var provider in TranslationProviderCatalog.All)
            foreach (bool through in new[] { true, false }) foreach (double width in new[] { 320d, 430d, 500d, 560d, 640d }) {
                profile.Translation.Provider = provider.Provider; overlay.Apply(profile); overlay.SetClickThrough(through);
                Layout(overlay, width, 180); Layout(overlay.Chrome, width, 180); Layout(overlay.Chrome, width, 180);
                var visual = (Visual)overlay.Chrome.Content;
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(visual).TransformBounds(new Rect(element.RenderSize));
                var badge = Bounds(mode); var serviceBounds = Bounds(service);
                Check(badge.Left >= serviceBounds.Right + 7 && badge.Top == serviceBounds.Top && badge.Right <= width - 10,
                    "Display mode is not next to service with an eight-DIP gap or escaped the overlay");
                Check(badge.Height == serviceBounds.Height && badge.Width == 88 && OverlayActivityTests.Visuals<Border>(mode).Any(b => b.CornerRadius.TopLeft == 13), "Display badge lost capsule size/shape");
                var caption = new TextBlock { Text = "원문+번역", FontFamily = mode.FontFamily, FontSize = mode.FontSize };
                caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Check(badge.Width >= caption.DesiredSize.Width + 20, "Display label has no horizontal padding");
                Check(overlay.Chrome.InputRegions(width, 180).Any(r => r.Contains(badge.TopLeft + new Vector(3, 3)) && r.Contains(badge.BottomRight - new Vector(3, 3))), "Display badge has no complete native input region");
                foreach (string name in new[] { "OverlayPassThrough", "OverlayWordDictionary", "OverlayToggleTranslation", "CloseOverlay" })
                    Check(!badge.IntersectsWith(Bounds(Named<FrameworkElement>(overlay.Chrome, name))), "Display badge overlaps " + name);
            }
            var red = ((SolidColorBrush)mode.Background).Color;
            Check(red.R > red.G && red.R > red.B && red.R < 100 && !mode.Focusable, "Display badge is not a muted red or steals focus");
            var initial = NewPipeline(); initial.Start(false); overlay.SetResult("Known source.", "이미 있는 번역", "");
            Complete(main.CycleSubtitleDisplayAsync());
            Check(Equals(mode.Content, "원문") && profile.ShowOriginal && !profile.ShowTranslation && originalCheck.IsChecked == true && koreanCheck.IsChecked == false,
                "Both -> original did not synchronize profile and settings");
            Check(source.Visibility == Visibility.Visible && translated.Visibility == Visibility.Collapsed && source.Text == "Known source.", "Original-only display lost retained content");
            Complete(main.CycleSubtitleDisplayAsync());
            Check(Equals(mode.Content, "번역") && !profile.ShowOriginal && profile.ShowTranslation && originalCheck.IsChecked == false && koreanCheck.IsChecked == true,
                "Original -> translation order/check synchronization is wrong");
            Check(translated.Visibility == Visibility.Visible && translated.Text == "이미 있는 번역" && source.Visibility == Visibility.Collapsed, "Translation-only did not restore completed translation");
            Complete(main.CycleSubtitleDisplayAsync());
            Check(Equals(mode.Content, "원문+번역") && profile.ShowOriginal && profile.ShowTranslation && source.Visibility == Visibility.Visible && translated.Visibility == Visibility.Visible,
                "Translation -> both did not restore both fields");
            Check(engine.Calls == 0 && translator.Calls == 0 && restarts == 2, "Cached display switching performed unnecessary OCR/AI or restarted for visibility only");
            originalCheck.IsChecked = false;
            Until(() => Equals(mode.Content, "번역") && mode.IsEnabled, "Settings change did not refresh the display badge");
            Check(!profile.ShowOriginal && profile.ShowTranslation, "Checkbox state disagrees with profile");
            originalCheck.IsChecked = true;
            Until(() => mode.IsEnabled, "Display save did not settle");
            // Same source retains its completed translation even when OCR-only updates send "".
            Complete(main.CycleSubtitleDisplayAsync());
            overlay.SetPipelineResult(new(1, PipelineState.Watching, "source only", "Known source.", ""));
            Complete(main.CycleSubtitleDisplayAsync());
            Check(translator.Calls == 0 && translated.Text == "이미 있는 번역", "Same-source OCR-only update discarded completed translation");
            // A new source cannot borrow the previous sentence's translation. Translate without OCR.
            Complete(main.CycleSubtitleDisplayAsync()); Complete(main.CycleSubtitleDisplayAsync());
            overlay.SetPipelineResult(new(2, PipelineState.Watching, "new source", "Different source.", ""));
            Complete(main.CycleSubtitleDisplayAsync());
            Until(() => translated.Text == "번역: Different source.", "Missing translation was not generated from the retained source");
            Check(translator.Calls == 1 && engine.Calls == 0, "Missing translation reran OCR or reused the wrong sentence");
            // No accepted text: a mode click requests OCR immediately even in trigger mode.
            overlay.SetResult("", "", "");
            Complete(main.CycleSubtitleDisplayAsync());
            Until(() => source.Text == "Fresh source." && translated.Text == "번역: Fresh source.", "Empty overlay was not filled by OCR and translation");
            Check(engine.Calls == 1 && translator.Calls == 2, "Empty content did not run exactly one OCR and translation");
            overlay.SetResult("", "", "");
            Complete(main.CycleSubtitleDisplayAsync());
            Until(() => source.Text == "Fresh source." && mode.IsEnabled, "Empty original-only display did not request OCR");
            Check(profile.ShowOriginal && !profile.ShowTranslation && engine.Calls == 2 && translator.Calls == 2,
                "Original-only display requested AI or skipped OCR");
            // Turning the source back on while translation streams must retain partial
            // content and the same request, rather than canceling and paying twice.
            overlay.SetResult("Waiting source.", "", "");
            translator.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Complete(main.CycleSubtitleDisplayAsync());
            Until(() => translated.Text == "수신 중인 번역", "Streaming fixture did not publish partial content");
            int pendingCalls = translator.Calls, pendingRestarts = restarts;
            Complete(main.CycleSubtitleDisplayAsync());
            Check(profile.ShowOriginal && profile.ShowTranslation && translated.Text == "수신 중인 번역"
                && translator.Calls == pendingCalls && restarts == pendingRestarts, "Display-only switch discarded streaming text or duplicated AI");
            translator.Pending.SetResult(new("수신 완료", TimeSpan.Zero));
            Until(() => translated.Text == "수신 완료", "Display switch lost the existing translation completion");
            var load = new SettingsStore(root).LoadAsync(); Complete(load); var saved = load.Result;
            Check(saved.DefaultProfile.ShowOriginal == profile.ShowOriginal && saved.DefaultProfile.ShowTranslation == profile.ShowTranslation, "Display choices were not persisted");
        } finally {
            foreach (var active in sessions) Complete(active.DisposeAsync().AsTask());
            Close(main); Close(overlay); Close(overlay.Chrome); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
