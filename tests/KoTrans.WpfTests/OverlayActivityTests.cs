using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class OverlayActivityTests
{
    internal static void LayoutAndCommands()
    {
        var profile = new Profile();
        var window = Track(new TranslationWindow(profile)); Track(window.Chrome);
        try
        {
            var service = Named<Button>(window.Chrome, "OverlayService");
            var busy = Named<TextBlock>(window, "OverlayBusy");
            var notice = Named<TextBlock>(window, "OverlayNotice");
            var pass = Named<CheckBox>(window.Chrome, "OverlayPassThrough");
            var dictionary = Named<CheckBox>(window.Chrome, "OverlayWordDictionary");
            var area = Named<Button>(window.Chrome, "OverlaySelectArea");
            var explain = Named<Button>(window.Chrome, "OverlayExplain");
            var toggle = Named<Button>(window.Chrome, "OverlayToggleTranslation");
            var close = Named<Button>(window.Chrome, "CloseOverlay");
            window.SetResult("Original 日本語", "번역", "");
            Check(Elements<TextBlock>(window).Single(t => t.Text == "Original 日本語").FontSize == 21,
                "New original font default is not 21 DIP");
            foreach (var provider in TranslationProviderCatalog.All)
            {
                profile.Translation.Provider = provider.Provider; window.Apply(profile);
                Check(Equals(service.Content, provider.DisplayName) && service.Visibility == Visibility.Visible,
                    "Service caption did not follow the selected provider");
                Check(explain.IsEnabled == (KoTrans.App.App.MockAi || provider.SupportsStudy),
                    "Explanation button availability did not follow the existing study service");
            }
            profile.Translation.Provider = TranslationProvider.OpenAI; window.Apply(profile);
            Layout(window.Chrome, 320, 150); Layout(window, 320, 150);
            Layout(window.Chrome, 320, 150);
            var chromeRoot = (Visual)window.Chrome.Content;
            Rect Bounds(FrameworkElement control) => control.TransformToAncestor(chromeRoot).TransformBounds(new Rect(control.RenderSize));
            Check(Bounds(pass).Right <= Bounds(dictionary).Left && Bounds(dictionary).Right <= Bounds(area).Left
                && Bounds(area).Right <= Bounds(explain).Left && Bounds(explain).Right <= Bounds(toggle).Left && Bounds(toggle).Right <= Bounds(close).Left,
                "Minimum-width command row overlaps or toggle is outside checkbox/X");
            Check(Bounds(pass).Left >= 0 && Bounds(close).Right <= 320,
                $"Minimum-width command row extends beyond the subtitle window: {Bounds(pass).Left:F1}..{Bounds(close).Right:F1}");
            Check(Equals(pass.Content, "클릭 통과") && Equals(dictionary.Content, "사전")
                && Equals(area.Content, "영역") && Equals(explain.Content, "해설")
                && area.Template == toggle.Template && explain.Template == toggle.Template
                && area.Width == toggle.Width && area.Height == toggle.Height
                && explain.Width == toggle.Width && explain.Height == toggle.Height,
                "Overlay commands lost their labels or shared button shape");
            var switchTrack = (Border)pass.Template.FindName("Track", pass);
            bool originalPass = pass.IsChecked == true;
            pass.IsChecked = false; Pump();
            var offColor = ((SolidColorBrush)switchTrack.Background).Color;
            pass.IsChecked = true; Pump();
            Check(((SolidColorBrush)switchTrack.Background).Color != offColor, "Click-through switch does not display its on state");
            pass.IsChecked = originalPass; Pump();
            var serviceBounds = Bounds(service);
            Check(service.ActualWidth >= 40 && serviceBounds.Left >= 40 && !serviceBounds.IntersectsWith(Bounds(pass))
                && !serviceBounds.IntersectsWith(Bounds(toggle)) && !serviceBounds.IntersectsWith(Bounds(close)),
                "Service caption collides with the handle or commands at minimum width");
            Check(window.Chrome.InputRegions(320, 150).Any(r => r.Contains(serviceBounds.TopLeft + new Vector(3, 3))),
                "Clickable service button has no native input region while click-through is enabled");
            Check(window.Chrome.InputRegions(320, 150).Any(r => r.Contains(Bounds(toggle).TopLeft + new Vector(10, 10))),
                "Start/stop button has no native input region");
            Check(window.Chrome.InputRegions(320, 150).Any(r => r.Contains(Bounds(explain).TopLeft + new Vector(10, 10))),
                "Explanation button has no native input region");
            Check(Visuals<Border>(toggle).Any(b => b.CornerRadius.TopLeft == 13), "Toggle has no capsule shape");
            var green = ((SolidColorBrush)service.Foreground).Color;
            var yellow = ((SolidColorBrush)busy.Foreground).Color;
            Check(green.G > green.R && green.G > green.B && yellow.R > yellow.B && yellow.G > yellow.B,
                "Service/activity indicator color roles were lost");
            int clicks = 0; window.ToggleTranslationRequested += () => clicks++;
            int areaRequests = 0; window.CaptureAreaRequested += () => areaRequests++;
            int explanationRequests = 0; window.ExplanationRequested += () => explanationRequests++;
            int mainRequests = 0; window.ShowMainRequested += () => mainRequests++;
            Click(service);
            Check(mainRequests == 1 && service.Cursor == System.Windows.Input.Cursors.Hand && !service.Focusable
                && service.ToolTip is null && System.Windows.Automation.AutomationProperties.GetName(service).Contains("메인창 열기"),
                "Service button is not discoverable/clickable or added a separate tooltip window");
            Click(toggle); window.SetAutomaticTranslation(true); Click(toggle);
            Click(area); Check(areaRequests == 1, "Area button did not request the existing region selector");
            Click(explain); Check(explanationRequests == 1 && !explain.Focusable
                && Equals(explain.ToolTip, "현재 문장 해설 보기 (Shift+F2)"), "Explanation button did not dispatch or changed game focus behavior");
            profile.ShowOriginal = false; profile.ShowTranslation = true; window.Apply(profile);
            window.SetResult("line A", "번역 A", "");
            Check(window.CurrentExplanationSource == "line A", "Translation-only subtitle lost its linked source");
            window.SetResult("line B", null, "", complete: false);
            Check(window.CurrentExplanationSource == "line A", "In-flight source replaced the translated subtitle's explanation target");
            window.SetResult(null, "번역 B", "");
            Check(window.CurrentExplanationSource == "line B", "Completed translation did not switch its explanation target");
            window.ShowStoppedPrompt();
            Check(window.CurrentExplanationSource.Length == 0, "Hidden prior subtitle remained an explanation target");
            profile.ShowOriginal = true; window.Apply(profile); window.SetResult("現在の文", "현재 문장", "");
            Check(window.CurrentExplanationSource == "現在の文", "Visible original was not selected for explanation");
            Check(clicks == 2 && Equals(toggle.Content, "중단") && !toggle.Focusable,
                "Toggle did not dispatch both actions or takes keyboard focus");
            window.SetAutomaticTranslation(false, true); Check(!toggle.IsEnabled, "Pending start permits repeated clicks");
            window.SetAutomaticTranslation(false); Check(toggle.IsEnabled && Equals(toggle.Content, "시작"), "Stopped control did not recover");
            window.SetClickThrough(false);
            Layout(window.Chrome, 320, 150); Layout(window, 320, 150);
            Layout(window.Chrome, 320, 150);
            Check(service.Visibility == Visibility.Visible && service.Margin.Left == 20 && !window.Chrome.IsMoveHandleVisible,
                "Off mode hides the service caption or reserves space for the hidden handle");
            Click(service); Check(mainRequests == 2 && !window.CanDragBody(service), "Off-mode service click dragged the overlay or failed to dispatch");
            foreach (bool through in new[] { true, false }) foreach (double width in new[] { 320d, 430d, 640d }) {
                window.SetClickThrough(through); Layout(window, width, 180); Layout(window.Chrome, width, 180);
                var bounds = Bounds(service); var regions = window.Chrome.InputRegions(width, 180);
                Check(bounds.Width >= 64 && bounds.Height == 26 && bounds.Left >= (through ? 44 : 20)
                    && !bounds.IntersectsWith(Bounds(pass)) && !bounds.IntersectsWith(Bounds(toggle)) && !bounds.IntersectsWith(Bounds(close)),
                    "Service button overlaps native commands at width " + width);
                foreach (var point in new[] { bounds.TopLeft + new Vector(2, 2), bounds.BottomRight - new Vector(2, 2) })
                    Check(regions.Any(r => r.Contains(point)), "Service button is outside native hit region after resize/click-through change");
                Check(!regions.Any(r => r.Contains(new Point(width / 2, 120))), "Service button intercepted the subtitle body");
            }
            Layout(window, 320, 150); Layout(window.Chrome, 320, 150);
            window.BeginSession(false);
            foreach (var state in new[] { PipelineState.Recognizing, PipelineState.Translating, PipelineState.Watching })
            {
                window.SetPipelineResult(new(1, state, "routine progress") { IsBusy = state != PipelineState.Watching });
                Check(notice.Visibility == Visibility.Collapsed && busy.Visibility == (state == PipelineState.Watching ? Visibility.Collapsed : Visibility.Visible),
                    "Routine message was shown or hourglass state was wrong");
            }
            window.SetPipelineResult(new(1, PipelineState.Error, "모의 오류") { FailureArea = PipelineStatusArea.Translation });
            window.SetBusy(false);
            Check(notice.Visibility == Visibility.Visible && notice.Text == "모의 오류", "Activity update erased an error");
            window.SetPipelineResult(new(1, PipelineState.Recognizing, "OCR 진행") { RecoveredAreas = PipelineStatusArea.Capture });
            Check(notice.Text == "모의 오류", "OCR progress erased an unresolved translation error");
            window.SetPipelineResult(new(2, PipelineState.Recognizing, "다음 OCR"));
            Check(notice.Text == "모의 오류", "New input version erased an unresolved translation error");
            window.SetPipelineResult(new(2, PipelineState.Watching, "중요 안내") { Important = true });
            Check(notice.Text == "모의 오류", "Warning replaced an unresolved error");
            window.SetPipelineResult(new(2, PipelineState.Watching, "번역 복구") { RecoveredAreas = PipelineStatusArea.Translation });
            Check(notice.Text == "중요 안내", "Recovered translation error did not reveal pending warning");
            window.SetPipelineResult(new(2, PipelineState.Watching, "입력 복구") { RecoveredAreas = PipelineStatusArea.Input });
            Check(notice.Visibility == Visibility.Collapsed, "Recovered warning remains visible");
            window.SetPipelineResult(new(2, PipelineState.Watching, "캡처 대기") { Important = true, FailureArea = PipelineStatusArea.Capture });
            window.SetPipelineResult(new(2, PipelineState.Recognizing, "OCR 진행"));
            Check(notice.Text == "캡처 대기", "Routine progress erased capture wait");
            window.SetPipelineResult(new(2, PipelineState.Watching, "복구") { RecoveredAreas = PipelineStatusArea.Capture });
            Check(notice.Visibility == Visibility.Collapsed, "Capture wait was not cleared by capture recovery");
            window.BeginSession(false);
            window.SetPipelineResult(new(3, PipelineState.Error, "새 세션 오류") { FailureArea = PipelineStatusArea.Ocr });
            window.SetPipelineResult(new(2, PipelineState.Error, "지연된 이전 오류") { FailureArea = PipelineStatusArea.Ocr });
            Check(notice.Text == "새 세션 오류", "Old session changed the current notice");
            window.SetPipelineResult(new(3, PipelineState.Stopped, "중지"));
            Check(notice.Visibility == Visibility.Collapsed, "Stopped session retained its error");
            window.SetResult(null, string.Join(" ", Enumerable.Repeat("long translation", 60)), ""); window.SetBusy(true);
            Layout(window, 320, 150);
            var scroll = Elements<ScrollViewer>(window).Single();
            var before = busy.TransformToAncestor((Visual)window.Content).Transform(new Point()); scroll.ScrollToEnd(); Pump();
            var after = busy.TransformToAncestor((Visual)window.Content).Transform(new Point());
            Check(before == after && after.Y > 100 && after.X > 270 && !busy.IsHitTestVisible,
                "Hourglass scrolls with text or intercepts game input");
            window.ReportInputFailure(5);
            Check(notice.Text.Contains("자막 버튼 오류") && !notice.Text.Contains("Windows 오류"),
                "Native input failure leaked technical details into the subtitle");
            window.BeginSession(false);
            window.SetPipelineResult(new(4, PipelineState.Recognizing, "routine"));
            Check(notice.Text.Contains("자막 버튼 오류"), "A routine pipeline event erased the unavailable controls warning");
        }
        finally { Close(window); Close(window.Chrome); }
    }

    internal static void PipelineLifecycle()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Activity", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings = new AppSettings(); settings.Validate(); settings.DefaultProfile.ReadingMode = "NVL";
        settings.DefaultProfile.Translation.Provider = TranslationProvider.Gemini;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(settings);
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        var engine = new HeldOcr(); var ai = new HeldAi(); int captures = 0;
        var pipeline = main.CreateProfilePipeline(engine, ai, _ => Task.FromResult(new ImageFrame(80, 40, 320, Enumerable.Repeat((byte)++captures, 12800).ToArray())));
        var busy = Named<TextBlock>(overlay, "OverlayBusy"); var notice = Named<TextBlock>(overlay, "OverlayNotice");
        var toggle = Named<Button>(overlay.Chrome, "OverlayToggleTranslation");
        bool Busy() => busy.Visibility == Visibility.Visible;
        try
        {
            main.ObserveTranslation(pipeline, settings.ActiveProfileId, watch: true); pipeline.Start(false); Pump();
            Check(!Busy() && Equals(toggle.Content, "중단"), "Idle automatic watcher was busy or displayed Start");
            Complete(pipeline.RequestManualAsync()); Until(() => engine.Calls == 1 && Busy(), "OCR did not turn on hourglass");
            engine.Results[0].SetResult(Result("first complete source"));
            Until(() => ai.Calls == 1, "First AI did not start");
            Check(Busy() && notice.Visibility == Visibility.Collapsed && Text(overlay).Contains("first complete source"),
                "Accepted source/activity was not shown before AI completion");
            Complete(pipeline.RequestTriggeredAsync()); Until(() => engine.Calls == 2, "Overlapping OCR did not start");
            ai.Results[0].SetResult(new("first translation", TimeSpan.Zero));
            Until(() => Text(overlay).Contains("first translation"), "AI completion missing");
            Check(Busy(), "Earlier AI completion hid hourglass while newer OCR still runs");
            engine.Results[1].SetResult(Result("first complete source"));
            Until(() => !Busy(), "Duplicate OCR left hourglass stuck");
            Check(ai.Calls == 1 && notice.Visibility == Visibility.Collapsed, "Duplicate probe sent AI or showed routine status");
            Complete(pipeline.RequestManualAsync(true)); Until(() => engine.Calls == 3, "Retry OCR did not start");
            engine.Results[2].SetResult(Result("second complete source")); Until(() => ai.Calls == 2, "Retry AI did not start");
            ai.Results[1].SetException(new TranslationException("모의 서버 오류 · 다시 시도하세요."));
            Until(() => !Busy() && notice.Text == "모의 서버 오류 · 다시 시도하세요.", "AI failure left busy latched or hid the error");
            Check(notice.Visibility == Visibility.Visible && Text(overlay).Contains("first translation"), "Failure erased the previous translation");
            Complete(pipeline.RequestManualAsync(true)); Until(() => engine.Calls == 4 && Busy(), "OCR restart missing");
            Click(toggle);
            Check(!pipeline.IsRunning && !Busy() && Equals(toggle.Content, "시작"), "Capsule Stop did not stop actual pipeline/activity");
            engine.Results[3].SetResult(Result("late discarded source")); Complete(pipeline.DisposeAsync().AsTask()); Pump();
            Check(!Busy() && ai.Calls == 2 && !Text(overlay).Contains("late discarded source"), "Late OCR revived a stopped overlay");
            // A missing model/key in the isolated settings fails before Show or any network call.
            Click(toggle);
            Check(Equals(toggle.Content, "시작") && toggle.IsEnabled && !Busy() && notice.Visibility == Visibility.Visible,
                "Start command did not report setup failure and restore retry state");
        }
        finally
        {
            foreach (var result in engine.Results) result.TrySetResult(new([], TimeSpan.Zero));
            foreach (var result in ai.Results) result.TrySetCanceled();
            Complete(pipeline.DisposeAsync().AsTask()); Close(overlay); Close(overlay.Chrome); Close(main); Directory.Delete(dir, true);
        }
    }

    internal static void EmptyAndFaultedOcr()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.ActivityErrors", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings = new AppSettings(); settings.Validate(); settings.DefaultProfile.ReadingMode = "NVL";
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(settings);
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        var engine = new HeldOcr(); var ai = new HeldAi();
        var pipeline = main.CreateProfilePipeline(engine, ai, _ => Task.FromResult(new ImageFrame(80, 40, 320, new byte[12800])));
        var busy = Named<TextBlock>(overlay, "OverlayBusy"); var notice = Named<TextBlock>(overlay, "OverlayNotice");
        try
        {
            main.ObserveTranslation(pipeline, settings.ActiveProfileId); pipeline.Start(false);
            Complete(pipeline.RequestManualAsync()); Until(() => engine.Calls == 1 && busy.Visibility == Visibility.Visible, "Empty OCR never started");
            engine.Results[0].SetResult(new([], TimeSpan.Zero));
            Until(() => busy.Visibility == Visibility.Collapsed, "Empty OCR left activity stuck");
            Check(ai.Calls == 0 && notice.Visibility == Visibility.Visible && notice.Text.Contains("인식 영역과 언어"),
                "Manual empty OCR did not explain how to find a subtitle");
            Complete(pipeline.RequestManualAsync()); Until(() => engine.Calls == 2 && busy.Visibility == Visibility.Visible, "Faulting OCR never started");
            engine.Results[1].SetException(new InvalidDataException("synthetic OCR failure"));
            Until(() => busy.Visibility == Visibility.Collapsed && notice.Visibility == Visibility.Visible, "OCR failure hid its error or left activity stuck");
            Check(ai.Calls == 0 && notice.Text.Contains("문자 인식 중 오류"), "OCR failure was treated as a normal completion: " + notice.Text);
            pipeline.SubmitClipboard("synthetic clipboard request", manual: true);
            Until(() => ai.Calls == 1 && busy.Visibility == Visibility.Visible, "Clipboard request has no busy indicator");
            pipeline.SubmitClipboard("", manual: true);
            Until(() => busy.Visibility == Visibility.Collapsed && notice.Text.Contains("문자 인식 중 오류"),
                "Empty clipboard supersession replaced an unresolved OCR error or left busy stale");
            ai.Results[0].SetResult(new("late clipboard response", TimeSpan.Zero));
            Complete(pipeline.DisposeAsync().AsTask()); Pump();
            Check(!Text(overlay).Contains("late clipboard response") && busy.Visibility == Visibility.Collapsed,
                "Superseded AI response restored subtitle or activity");
        }
        finally
        {
            foreach (var result in engine.Results) result.TrySetResult(new([], TimeSpan.Zero));
            foreach (var result in ai.Results) result.TrySetCanceled();
            Complete(pipeline.DisposeAsync().AsTask()); Close(overlay); Close(overlay.Chrome); Close(main); Directory.Delete(dir, true);
        }
    }

    internal static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static OcrResult Result(string text) => new([new(text, 1, new(0, 0, 80, 40))], TimeSpan.Zero);
    private sealed class HeldOcr : IOcrService
    {
        internal int Calls;
        internal TaskCompletionSource<OcrResult>[] Results = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) => Results[Interlocked.Increment(ref Calls) - 1].Task;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class HeldAi : ITranslationService
    {
        internal int Calls;
        internal TaskCompletionSource<TranslationResult>[] Results = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) => Results[Interlocked.Increment(ref Calls) - 1].Task;
    }
}
