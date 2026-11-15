using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class WpfTests
{
    internal static void ImageImport()
    {
        var root = TempDirectory(); var file = Path.Combine(root, "image.png");
        try {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(12, 8, 96, 96, PixelFormats.Bgr32, null, new byte[12 * 8 * 4], 48);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(file)) encoder.Save(output);
            var task = ImageFileLoader.LoadAsync(file); Complete(task);
            Check(task.Result.Width == 12 && task.Result.Height == 8 && task.Result.Stride == 48, "Background image import changed pixels/dimensions");
            File.WriteAllText(file, "not an image"); bool rejected = false;
            try { Complete(ImageFileLoader.LoadAsync(file)); } catch (Exception ex) when (ex is NotSupportedException or System.IO.FileFormatException) { rejected = true; }
            Check(rejected, "Malformed import accepted");
            using (var large = File.Create(file)) large.SetLength(64L * 1024 * 1024 + 1);
            rejected = false; try { Complete(ImageFileLoader.LoadAsync(file)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Oversized input file decoded");
        } finally { Directory.Delete(root, true); }
    }
    internal static void CapturePolicy()
    {
        // Same decision used by EnsureCaptureSafe before native bounds lookup. No HWND is needed here.
        Check(!MainWindow.NeedsCaptureOverlapCheck(false, false, false, false), "Hidden study blocks capture");
        Check(MainWindow.NeedsCaptureOverlapCheck(true, false, false, false), "Unprotected restored study bypasses overlap guard");
        Check(!MainWindow.NeedsCaptureOverlapCheck(true, false, false, true), "Excluded study/overlay blocks capture");
        Check(!MainWindow.NeedsCaptureOverlapCheck(true, true, false, false), "Minimized study blocks capture");
        Check(!MainWindow.NeedsCaptureOverlapCheck(true, false, true, false), "QA fixture blocks capture");
        Check(MainWindow.NeedsCaptureOverlapCheck(true, false, false, false), "Failed exclusion after new request not guarded");
    }
    internal static StudyResult Example(string translation = "여정은 시작됩니다.") => new(translation, "begin은 시작을 나타냅니다.",
        [new("journey", "/ˈdʒɜːrni/", "An act of travelling.", "여정", [new("Our journey starts today.", "여정은 오늘 시작됩니다.")], "성장 과정에도 사용합니다."),
         new("学校", "がっこう", "学ぶ場所。", "학교", [new("学校へ行きます。", "학교에 갑니다.")], "へ는 방향을 나타냅니다.")]);
    internal static void Study()
    {
        var settings = new AppSettings { StudyTopmost = false, StudyWindow = new() { X = -200, Y = 140, Width = 690, Height = 720 } };
        var window = Track(new StudyWindow(settings, resolveVoiceDirectory: () => null));
        try
        {
            Check(window.Width == 690 && window.Height == 720 && !window.Topmost && !window.ShowActivated,
                "Study reader placement or inactive-on-show policy changed");
            Check(window.Background is SolidColorBrush { Color: var background }
                && background == Color.FromRgb(0x10, 0x1B, 0x2A),
                "Reader must share the launcher dark palette");
            Check(!Elements<TextBox>(window).Any() && !Elements<Button>(window).Any()
                && !Elements<CheckBox>(window).Any(),
                "Read-only reader must not show text inputs, commands, audio, or settings");
            Check(Elements<ScrollViewer>(window).Count() == 1,
                "Reader must contain one scrolling document, not nested panels");

            window.SetSource("The journey begins. 学校へ行きます。", "en");
            Check(window.Input == "The journey begins. 学校へ行きます。"
                && Named<TextBlock>(window, "StudyOriginalText").Text == window.Input,
                "Original source was not shown first");
            window.Render(Example()); Layout(window, 640, 640);
            var text = Text(window);
            Check(text.Contains("원문") && text.Contains("전체 해석")
                && text.Contains("여정은 시작됩니다.") && text.Contains("begin은"),
                "Original, full translation, or grammar section was lost");
            Check(Named<TextBlock>(window, "StudyTranslationText").FontSize >
                  Named<TextBlock>(window, "StudyExplanationText").FontSize,
                "Full Korean translation must be larger than the grammar explanation");
            Check(Named<TextBlock>(window, "StudyOriginalText").FontSize >= 21
                && Named<TextBlock>(window, "StudyTranslationText").FontSize >= 23
                && Named<TextBlock>(window, "StudyExplanationText").FontSize >= 17,
                "All sentence text needs an accessible size, not just kanji");
            Check(Elements<TextBlock>(window).Any(t => t.Text == "여정" && t.FontSize >= 18)
                && Elements<TextBlock>(window).Any(t => t.Text == "journey" && t.FontSize >= 20)
                && Elements<TextBlock>(window).Any(t => t.Text == "Our journey starts today." && t.FontSize >= 16),
                "Vocab meanings or examples remain too small");
            Check(text.Contains("/ˈdʒɜːrni/") && text.Contains("An act of travelling.")
                  && text.Contains("여정"), "IPA text and English vocabulary were dropped");
            var sourceHeading = Elements<TextBlock>(window).Single(t => t.Text == "journey");
            var koreanMeaning = Elements<TextBlock>(window).Single(t => t.Text == "여정");
            Check(sourceHeading.FontSize > koreanMeaning.FontSize
                && Named<TextBlock>(window, "StudyOriginalText").Text == window.Input,
                "English source heading was replaced by its Korean meaning or translation");
            Check(text.Contains("Our journey starts today.") && text.Contains("学校へ行きます。")
                  && text.Contains("학교에 갑니다.") && text.Contains("사용 팁"),
                "Vocabulary examples or Korean usage tips were lost");
            Check(!text.Contains("문장을 이해하는 시간") && !text.Contains("발음 중지")
                  && !text.Contains("HTTP"), "Old controls or HTTP diagnostics leak into the reader");

            var hourglass = Named<System.Windows.Shapes.Path>(window, "StudyLoadingHourglass");
            var progress = Named<DockPanel>(window, "StudyProgressRow");
            window.SetBusy(true, "학습 분석 중… HTTP 1회");
            Check(Named<TextBlock>(window, "StudyFeedback").Visibility == Visibility.Visible
                && Named<TextBlock>(window, "StudyFeedback").FontSize >= 22
                && Text(window).Contains("문장 해설을 가져오는 중")
                && !Text(window).Contains("HTTP 1회"),
                "Loading must use a large message without developer metrics");
            Check(hourglass.Visibility == Visibility.Visible && progress.Visibility == Visibility.Visible
                && hourglass.RenderTransform is RotateTransform && window.IsLoadingAnimationRunning
                && hourglass.Width <= 26 && hourglass.Height <= 26
                && hourglass.StrokeThickness <= 2,
                "A compact, thin-line hourglass must rotate unobtrusively during analysis");
            window.SetBusy(true, "still working");
            Check(window.IsLoadingAnimationRunning, "Repeated busy state stopped the animation");
            window.FinishBusy();
            Check(Named<TextBlock>(window, "StudyFeedback").Visibility == Visibility.Collapsed
                && hourglass.Visibility == Visibility.Collapsed && !window.IsLoadingAnimationRunning,
                "Completed analysis did not stop or hide the hourglass");
            window.SetBusy(true, "another request");
            window.Clear();
            Check(!window.IsLoadingAnimationRunning && hourglass.Visibility == Visibility.Collapsed,
                "Clearing previous results left animation running");
            window.SetStatus("학습 분석 완료 · 학습 1회 / HTTP 1회");
            Check(window.LastStatus.Contains("HTTP 1회") && !Text(window).Contains("학습 분석 완료"),
                "Internal analysis metrics must not be user-visible");
            window.SetBusy(true, "another request");
            window.ShowError("분석 요청 실패 · 번역 제공자 설정을 확인하세요.");
            Check(Named<TextBlock>(window, "StudyFeedback").Visibility == Visibility.Visible
                && Text(window).Contains("번역 제공자 설정")
                && hourglass.Visibility == Visibility.Collapsed && !window.IsLoadingAnimationRunning,
                "Error should stop the spinner and show an actionable message");

            var longText = string.Join(" ", Enumerable.Repeat("Long dialogue with grammar and nuance. 긴 문장과 일본어 学校。", 80));
            window.Render(Example(longText)); Layout(window, 490, 610);
            var paragraph = Elements<TextBlock>(window).Single(t => t.Text == longText);
            var scroll = Elements<ScrollViewer>(window).Single();
            Check(paragraph.TextWrapping == TextWrapping.Wrap &&
                paragraph.ActualHeight > 3 * paragraph.FontSize,
                "Long translations should wrap at narrow reader widths");
            Check(scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0 && scroll.ScrollableWidth == 0,
                "Study explanation must scroll vertically without horizontal scrolling");
            scroll.ScrollToBottom(); Layout(window, 490, 610);
            Check(scroll.VerticalOffset > 0, "Reader scroll failed");

            window.SetSource("新しい日本語の文です。");
            window.Render(new("새 해석", null, [])); Layout(window, 490, 610);
            Check(Text(window).Contains("새 해석") && !Text(window).Contains("journey")
                && scroll.VerticalOffset == 0, "New sentence retained stale result or scroll position");
            window.RenderReading(new("学校（がっこう）", [], "로컬 읽기는 후보입니다."));
            Check(Text(window).Contains("한자 읽기 후보") &&
                Text(window).Contains("学校（がっこう）") &&
                !Text(window).Contains("일본어 읽기 · 로컬 후보"),
                "Legacy Japanese reading without tokens must keep a safe fallback");

            var sentence = "彼女は学校へ行く。";
            var localReading = new JapaneseReading(
                "彼女（かのじょ）は学校（がっこう）へ行（い）く。",
                [
                    new("彼女", "かのじょ", true),
                    new("は", null, false),
                    new("学校", "がっこう", true),
                    new("へ", null, false),
                    new("行く", "いく", true),
                    new("。", null, false)
                ], "로컬 사전의 읽기 후보");
            window.SetSource(sentence);
            window.Render(new StudyResult("그녀는 학교에 간다.", "へ는 이동 방향을 나타내요.",
                [new VocabularyItem("学校", "がっこう", "学ぶ場所。", "학교",
                    [new StudyExample("学校へ行きます。", "학교에 갑니다.")], "학습하는 장소")]),
                localReading);
            Layout(window, 490, 610);
            var rubySource = Named<JapaneseRubyLinePanel>(window, "StudyRubySentence");
            var rubyPairs = Elements<StackPanel>(rubySource)
                .Where(p => p.Name == "JapaneseRubyPair").ToArray();
            Check(window.Input == sentence && rubySource.Visibility == Visibility.Visible
                && Named<TextBlock>(window, "StudyOriginalText").Visibility == Visibility.Collapsed
                && rubySource.ArrangedLineCount == 1 && rubyPairs.Length == 3,
                "Japanese sentence must lay out large base glyphs and ruby in a single aligned line");

            var schoolPair = rubyPairs[1];
            var schoolReading = (TextBlock)schoolPair.Children[0];
            var schoolBase = (TextBlock)schoolPair.Children[1];
            var plainKana = Elements<TextBlock>(rubySource)
                .Single(t => t.Name == "JapaneseRubyPlain" && t.Text == "は");
            Check(schoolReading.Text == "がっこう" && schoolReading.FontSize >= 14
                && schoolBase.Text == "学校" && schoolBase.FontSize >= 21
                && plainKana.FontSize == schoolBase.FontSize,
                "Ruby and ordinary kana must use legible sizes with one consistent base font");
            // Compare real arranged Y coordinates, not only control properties.
            // The previous InlineUIContainer implementation lifted kanji an
            // entire text row above ordinary hiragana.
            double kanjiBottom = schoolBase.TranslatePoint(
                new Point(0, schoolBase.ActualHeight), rubySource).Y;
            double kanaBottom = plainKana.TranslatePoint(
                new Point(0, plainKana.ActualHeight), rubySource).Y;
            double readingBottom = schoolReading.TranslatePoint(
                new Point(0, schoolReading.ActualHeight), rubySource).Y;
            double kanjiTop = schoolBase.TranslatePoint(new Point(0, 0), rubySource).Y;
            Check(Math.Abs(kanjiBottom - kanaBottom) <= 2,
                "Kanji words drift upward relative to plain hiragana on the same sentence line");
            Check(readingBottom <= kanjiTop + 3
                && kanjiTop - readingBottom <= 5,
                "Ruby should touch the kanji line with only a small natural gap");
            Check(Elements<TextBlock>(rubySource).Any(t => t.Text == "へ"
                    && t.Name == "JapaneseRubyPlain")
                && !Text(window).Contains("学校（がっこう）")
                && !JapaneseRubyLayout.CanShowRuby("かな", "かな")
                && !JapaneseRubyLayout.CanShowRuby("不明", null),
                "Ordinary kana must remain on the baseline and unknown readings must not be invented");
            Check(Elements<StackPanel>(window).Any(p => p.Name == "JapaneseRubyPair"
                && p.Children.OfType<TextBlock>().Any(t => t.Text == "がっこう")),
                "Vocabulary kanji must retain its separate word-level reading");

            var before = rubySource.Children.Count;
            Check(!JapaneseRubyLayout.TryRender(rubySource, sentence,
                [new ReadingToken("다른 텍스트", "た", true)], 21, 14,
                Brushes.White, Brushes.LightBlue)
                && rubySource.Children.Count == before,
                "Unaligned reading tokens must not corrupt the displayed sentence");
            // Long text wraps at the available width without breaking ruby
            // apart from its word; each row bottom-aligns plain and ruby words.
            var longRuby = new JapaneseRubyLinePanel();
            var longSentence = string.Concat(Enumerable.Repeat("彼女は学校へ行く。", 8));
            var longTokens = Enumerable.Range(0, 8).SelectMany(_ =>
                localReading.Tokens).ToArray();
            Check(JapaneseRubyLayout.TryRender(longRuby, longSentence,
                longTokens, 21, 14, Brushes.White, Brushes.LightBlue),
                "Repeated real Japanese tokens could not be rendered as ruby");
            longRuby.Measure(new Size(240, double.PositiveInfinity));
            longRuby.Arrange(new Rect(0, 0, 240, longRuby.DesiredSize.Height));
            Check(longRuby.ArrangedLineCount >= 2
                && longRuby.DesiredSize.Width <= 240,
                "Ruby line wrapping overflows a narrow reading window");
            Layout(window, 490, 610);
            Check(Elements<ScrollViewer>(window).Single().ScrollableWidth == 0,
                "Ruby wrapping must not force horizontal scrolling");

            window.SetSource("look up");
            window.Render(new(null, null, []));
            Check(Named<TextBlock>(window, "StudyOriginalText").Text == "look up"
                && Named<TextBlock>(window, "StudyOriginalText").Visibility == Visibility.Visible
                && rubySource.Visibility == Visibility.Collapsed
                && rubySource.Children.Count == 0
                && !Text(window).Contains("学校（がっこう）")
                && !Text(window).Contains("새 해석"),
                "A new source must discard old ruby without changing the original text");
        }
        finally { Close(window); }
    }
    internal static void StudyPronunciationByLanguage()
    {
        var window = Track(new StudyWindow(new(), resolveVoiceDirectory: () => null));
        try {
            window.SetSource("学校へ行きます。", "ja");
            foreach (var pronunciation in new[] { "/ɡakːoː/", "[がっこう] (gakkou)", "gakkou", "がっこう (갓코오)" }) {
                window.Render(new("학교에 갑니다.", "へ는 이동 방향을 나타냅니다.",
                    [new("学校", pronunciation, "学ぶ場所。", "학교", [], "교육 기관을 나타냅니다.")]));
                Layout(window, 490, 610);
                Check(!Text(window).Contains(pronunciation) && Text(window).Contains("学校")
                    && Text(window).Contains("교육 기관을 나타냅니다."),
                    "Japanese vocabulary displayed a raw pronunciation or lost its word/tip");
            }
            window.Render(new(null, null, [new("学校", "がっこう", "学ぶ場所。", "학교", [], "")]));
            Check(Elements<StackPanel>(window).Any(p => p.Name == "JapaneseRubyPair")
                && Text(window).Contains("がっこう"), "Hiding Japanese side pronunciation removed valid furigana");
            window.SetSource("The journey begins.", "en"); window.Render(Example());
            Check(Text(window).Contains("/ˈdʒɜːrni/"), "Switching to English did not restore IPA pronunciation");
            window.SetSource("次の文です。", "ja"); window.Render(Example());
            Check(!Text(window).Contains("/ˈdʒɜːrni/") && Text(window).Contains("journey"),
                "Returning to Japanese retained IPA from the previous English explanation");
        } finally { Close(window); }
    }
    private sealed class MockExplanation : IExplanationService
    {
        public int Calls;
        public Task<ExplanationResult> ExplainAsync(ExplanationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new ExplanationResult(Example(), TimeSpan.Zero, 0)); }
    }
    private sealed class InvalidOllamaExplanation : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var json = System.Text.Json.JsonSerializer.Serialize(new {
                done = true, message = new { content = """{"vocabulary":[{"word":"군인들","meaning_ko":"군대 구성원"}]}""" }
            });
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
    internal static void StudyMock()
    {
        var window = Track(new StudyWindow(new(), resolveVoiceDirectory: () => null)); var session = new ExplanationSession(); var mock = new MockExplanation();
        try
        {
            var request = new ExplanationRequest("journey", "en", new() { Model = "synthetic-model" }); var id = Guid.NewGuid();
            var task = session.AnalyzeAsync(id, request, mock); Complete(task); window.Render(task.Result!.Study);
            Check(Text(window).Contains("여정은 시작됩니다."), "Mock service result did not reach real StudyWindow");
            var cached = session.AnalyzeAsync(id, request, mock); Complete(cached);
            Check(cached.Result!.FromCache && mock.Calls == 1 && session.HttpAttempts == 0, "UI test unexpectedly used network/duplicate request");
            using var client = new System.Net.Http.HttpClient(new InvalidOllamaExplanation());
            var service = new ExplanationService(new OllamaTranslationService(client));
            var bad = service.ExplainAsync(new("The soldiers wait.", "en", new() { Provider = TranslationProvider.Ollama, Model = "synthetic-translation-model" }), default);
            try { Complete(bad); throw new Exception("Bad Ollama result reached reader"); }
            catch (TranslationException ex) {
                window.Clear(); window.ShowError(ex.Message); Layout(window, 490, 610);
                var message = Named<TextBlock>(window, "StudyFeedback");
                Check(message.Visibility == Visibility.Visible && message.Text.Contains("Ollama · synthetic-translation-model")
                    && message.Text.Contains("문장 해설 검사를 통과하지 못했습니다") && message.Text.Contains("번역이 가능해도"),
                    "Study reader omitted provider, exact model or distinction from translation support");
                Check(message.TextWrapping == TextWrapping.Wrap && !Text(window).Contains("여정은 시작됩니다.")
                    && !Elements<TextBlock>(window).Any(t => t.Text == "군인들"), "Invalid vocabulary or previous result remained under the model limitation notice");
            }
        }
        finally { Complete(session.DisposeAsync().AsTask()); Close(window); }
    }
    internal static void Overlay()
    {
        var profile = new Profile { OverlayFontSize = 32, OverlayColor = "#A5DEFF", OverlayOpacity = .65, OverlayOriginalFontSize = 22, OverlayOriginalColor = "#12AB34", ShowOriginal = true };
        var window = Track(new TranslationWindow(profile));
        try
        {
            window.SetClickThrough(true); window.Left = 100; window.Top = 120;
            var handle = Elements<System.Windows.Controls.Primitives.Thumb>(window.Chrome).Single(t => t.Name == "OverlayMoveHandle");
            handle.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(35, 20) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            Check(window.Left == 135 && window.Top == 140 && handle.Visibility == Visibility.Visible, "Click-through move handle failed");
            window.SetClickThrough(false);
            handle.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(35, 20) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            Check(window.Left == 135 && window.Top == 140 && handle.Visibility == Visibility.Collapsed, "Hidden click-through handle accepted a drag");
            window.SetResult("Original source", "번역 결과", "번역 중"); Layout(window, 340, 240);
            var original = Elements<TextBlock>(window).Single(t => t.Text == "Original source");
            var translation = Elements<TextBlock>(window).Single(t => t.Text == "번역 결과");
            Check(original.Visibility == Visibility.Visible && Text(window).Contains("번역 중"), "Original/progress missing");
            Check(original.FontSize == 22 && ((SolidColorBrush)original.Foreground).Color == Color.FromRgb(18,171,52),"Original font/color not applied independently");
            Check(translation.FontSize == 32 && ((SolidColorBrush)translation.Foreground).Color == Color.FromRgb(165, 222, 255) && window.Opacity == .65, "Overlay styling not applied");
            profile.ShowOriginal = false; profile.OverlayFontSize = 20; window.Apply(profile);
            Check(original.Visibility == Visibility.Collapsed && translation.FontSize == 20, "Profile update not applied");
            window.SetResult(null, null, "네트워크 오류"); Check(translation.Text == "번역 결과" && Text(window).Contains("네트워크 오류"), "Status-only error destroyed result");
            window.SetResult("Next", string.Join(" ", Enumerable.Repeat("긴 번역 문장 wraps naturally.", 30)), "완료"); Layout(window, 320, 200);
            Check(translation.TextWrapping == TextWrapping.Wrap && translation.ActualHeight > 60 && !Text(window).Contains("번역 결과"), "Small overlay wrap/replacement failed");
            window.SetResult("", "", "대기"); Check(translation.Text == "" && original.Text == "", "Clear left stale text");
        }
        finally { Close(window); }
    }
    private static string TempDirectory()
    { var path = Path.Combine(Path.GetTempPath(), "KoTrans2.WpfTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static void Profiles()
    {
        var directory = TempDirectory(); var store = new SettingsStore(directory);
        var settings = new AppSettings { VoiceDirectory = Path.Combine(directory, "voice"), StudyWindow = new() { X = -500, Y = 120, Width = 700, Height = 650 } };
        settings.Profiles.Add(new() { Name = "일본어 프로필", Language = "일본어", ReadingMode = "NVL", Vertical = true, Translation = new() { Provider = TranslationProvider.Ollama, Model = "chosen-local-model" } }); settings.Validate();
        var window = Track(new MainWindow(store, new ApiKeyStore(directory)));
        try
        {
            Complete(store.SaveAsync(settings)); var load = store.LoadAsync(); Complete(load); var restored = load.Result; window.ApplyLoadedSettings(restored);
            var profiles = (ComboBox)window.FindName("Profiles"); profiles.SelectedIndex = 1;
            Until(() => !window.IsUserSavePending, "Profile switch did not finish");
            Check(restored.ActiveProfileId == restored.Profiles[1].Id && ((ComboBox)window.FindName("SourceLanguage")).SelectedIndex == 1 && ((ComboBox)window.FindName("ReadingMode")).SelectedIndex == 1, "Profile restoration did not bind language/mode");
            Check(((CheckBox)window.FindName("VerticalText")).IsChecked == true && ((TextBlock)window.FindName("TranslationSummary")).Text.Contains("chosen-local-model"), "Profile/model summary mismatch");
            ((ComboBox)window.FindName("SourceLanguage")).SelectedIndex = 0; ((ComboBox)window.FindName("ReadingMode")).SelectedIndex = 0;
            Check(restored.Profiles[1].Language == "영어" && restored.Profiles[1].ReadingMode == "ADV" && restored.Profiles[0].Language == "영어", "Control changes did not update active profile");
            Check(restored.VoiceDirectory == settings.VoiceDirectory && restored.StudyWindow.X == -500 && restored.StudyWindow.Width == 700, "Voice/placement settings lost");
            Check(((FrameworkElement)window.FindName("Workspace")).IsEnabled, "Restored workspace not enabled");
        }
        finally { Close(window); Directory.Delete(directory, true); }
    }
    internal static void Settings()
    {
        var directory = TempDirectory(); var profile = new Profile(); var hotkeys = new HotkeyOptions();
        var window = Track(new TranslationSettingsWindow(profile, hotkeys, new ApiKeyStore(directory)));
        try
        {
            Check(Named<TextBox>(window, "ExplainHotkey").Text == "Shift+F2"
                && Named<TextBox>(window, "ScrollUpHotkey").Text == "Shift+PageUp"
                && Named<TextBox>(window, "ScrollDownHotkey").Text == "Shift+PageDown", "Study defaults missing from real settings controls");
            var provider = Named<ComboBox>(window, "Provider"); var model = Named<TextBox>(window, "Model");
            Check(provider.Items[0] is TranslationProvider.HyMtLocal && provider.SelectedItem is TranslationProvider.HyMtLocal,
                "A fresh profile did not show local AI first and selected");
            provider.SelectedItem = TranslationProvider.Gemini;
            model.Text = "gemini-test-exact"; provider.SelectedItem = TranslationProvider.OpenAI;
            Check(model.Text == "", "Gemini model leaked into the unconfigured OpenAI slot");
            model.Text = "openai-test-exact"; provider.SelectedItem = TranslationProvider.Ollama;
            Check(model.Text == "", "OpenAI model leaked into the unconfigured Ollama slot");
            model.Text = "local-model-exact"; provider.SelectedItem = TranslationProvider.Gemini;
            Check(model.Text == "gemini-test-exact" && profile.Translation.Model == "", "Provider switch lost the draft or changed settings before Apply");
            provider.SelectedItem = TranslationProvider.OpenAI; Check(model.Text == "openai-test-exact", "OpenAI draft was not restored");
            provider.SelectedItem = TranslationProvider.Ollama; Check(model.Text == "local-model-exact", "Ollama draft was not restored");
            Named<TextBox>(window, "Model").Text = "local-model-exact"; Named<TextBox>(window, "ExplainHotkey").Text = "Ctrl+Alt+F8";
            Check(window.ApplyChanges(profile, hotkeys) && profile.Translation.Provider == TranslationProvider.Ollama && profile.Translation.Model == "local-model-exact" && hotkeys.Explain == "Ctrl+Alt+F8", "Settings did not apply exact model/hotkey");
            Check(profile.Translation.GeminiModel == "gemini-test-exact" && profile.Translation.OpenAIModel == "openai-test-exact" && profile.Translation.OllamaModel == "local-model-exact", "Apply lost inactive provider model IDs");
            var format = Named<ComboBox>(window,"OllamaFormat"); var timeoutBox = Named<TextBox>(window,"Timeout");
            model.Text="translategemma:12b"; model.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            format.SelectedItem=OllamaRequestFormat.TranslateGemma; timeoutBox.Text="120";
            model.Text="other-local-model"; model.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check((OllamaRequestFormat)format.SelectedItem==OllamaRequestFormat.Chat,"Specialized formatter leaked into another model");
            timeoutBox.Text="45"; model.Text="translategemma:12b"; model.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check((OllamaRequestFormat)format.SelectedItem==OllamaRequestFormat.TranslateGemma && timeoutBox.Text=="120","Model request format/timeout not restored");
            Check(window.ApplyChanges(profile,hotkeys)&&profile.Translation.SettingsForOllama().Format==OllamaRequestFormat.TranslateGemma,"Specialized request profile not applied");
            provider.SelectedItem=TranslationProvider.Gemini;
            Check(model.Text=="gemini-test-exact"&&!format.IsEnabled&&timeoutBox.Text=="30","Cloud model/timeout not restored independently");
            provider.SelectedItem=TranslationProvider.Ollama;
            Check(model.Text=="translategemma:12b"&&(OllamaRequestFormat)format.SelectedItem==OllamaRequestFormat.TranslateGemma,"Provider switch lost Ollama format");
            Named<TextBox>(window, "SaveGameQaHotkey").Text = "Shift+F9";
            Check(window.ApplyChanges(profile, hotkeys) && hotkeys.SaveGameQa == "Shift+F9", "QA hotkey editor did not apply");
            Named<TextBox>(window, "SaveGameQaHotkey").Text = "Control+Alt+F8";
            Check(!window.ApplyChanges(profile, hotkeys) && hotkeys.SaveGameQa == "Shift+F9", "QA alias conflict accepted");
            Named<TextBox>(window, "SaveGameQaHotkey").Text = "Shift+F9";
            Named<TextBox>(window, "ExplainHotkey").Text = hotkeys.ShowSettings;
            Check(!window.ApplyChanges(profile, hotkeys) && hotkeys.Explain == "Ctrl+Alt+F8" && Named<TextBlock>(window, "SettingsStatus").Text.Contains("서로 다르게"), "Duplicate hotkey accepted or changed saved setting");
            Named<TextBox>(window, "Timeout").Text = "invalid";
            Check(!window.ApplyChanges(profile, hotkeys) && Named<TextBlock>(window, "SettingsStatus").Text.Contains("정수"), "Invalid timeout not surfaced");
            Named<TextBox>(window, "Timeout").Text = "30"; Named<TextBox>(window, "ExplainHotkey").Text = "Ctrl+Alt+F8";
            var triggerKeys = Named<KeyboardTriggerEditor>(window, "TriggerKeys");
            var triggerPad = Named<GamepadTriggerEditor>(window, "TriggerPad");
            triggerKeys.Value = "NoSuchKey";
            Check(!window.ApplyChanges(profile, hotkeys) && profile.Triggers.Keys != "NoSuchKey", "Invalid legacy trigger saved");
            triggerKeys.Value = "Enter,Space"; triggerPad.Value = "Joy9";
            Check(!window.ApplyChanges(profile, hotkeys) && profile.Triggers.GamepadButtons != "Joy9", "Invalid legacy gamepad saved");
            triggerPad.Value = "Joy1,Joy4";
            Check(window.ApplyChanges(profile, hotkeys) && profile.Triggers.Keys == "Enter,Space", "Supported triggers rejected");
            bool blockedCtrl = false; try { DesktopInput.ParseGameQaHotkey("Ctrl+Alt+F9"); } catch (TranslationException) { blockedCtrl = true; }
            Check(blockedCtrl && DesktopInput.ParseGameQaHotkey("F8").Modifiers == 0, "Ctrl QA key accepted or F8 not unmodified");
            var retention = Named<TextBox>(window,"OllamaKeepAlive"); retention.Text="-1";
            Check(!window.ApplyChanges(profile,hotkeys)&&Named<TextBlock>(window,"SettingsStatus").Text.Contains("0–60"),"Invalid retention was saved");
            retention.Text="12"; Check(window.ApplyChanges(profile,hotkeys)&&profile.Translation.SettingsForOllama().KeepAliveMinutes==12,"Retention preference was not saved");
            var restored = Track(new TranslationSettingsWindow(profile,hotkeys,new ApiKeyStore(directory)));
            try {
                Check(Named<TextBox>(restored,"Model").Text=="translategemma:12b" && (OllamaRequestFormat)Named<ComboBox>(restored,"OllamaFormat").SelectedItem==OllamaRequestFormat.TranslateGemma && Named<TextBox>(restored,"OllamaKeepAlive").Text=="12","Reopened settings lost model-specific protocol/retention");
                var canceledModel=Named<TextBox>(restored,"Model"); canceledModel.Text="canceled-edit"; canceledModel.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                Check(profile.Translation.Model=="translategemma:12b"&&!profile.Translation.OllamaModels.ContainsKey("canceled-edit"),"Closing without Apply changed persisted model settings");
            } finally { Close(restored); }
            Check(DesktopInput.ParseHotkey("Control+Alt+F8") == DesktopInput.ParseHotkey("Ctrl+Alt+F8"), "Hotkey aliases differ");
            Check(!Directory.EnumerateFiles(directory).Any(), "Settings control test accessed/wrote credentials");
        }
        finally { Close(window); Directory.Delete(directory, true); }
    }
    internal static void GameQa()
    {
        var directory = TempDirectory(); var settings = new AppSettings(); settings.Profiles.Add(new() { Name = "Other" }); settings.Validate();
        var window = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory))); window.ApplyLoadedSettings(settings);
        var viewModel = (MainViewModel)window.DataContext;
        try
        {
            var registered = DesktopInput.RegisteredHotkeys(settings.Hotkeys);
            Check(viewModel.GameQaStatus.Contains("연결되어 있지 않습니다"), "Disconnected QA still advertised a working save shortcut");
            Check(registered.Length == 5 && !registered.Contains("F8")
                && registered.SequenceEqual(new[] { settings.Hotkeys.ShowSettings, settings.Hotkeys.ToggleTranslation,
                    settings.Hotkeys.Explain, settings.Hotkeys.StudyScrollUp, settings.Hotkeys.StudyScrollDown }),
                "F8 QA binding remains or a functional shortcut was removed");
            settings.Hotkeys.SaveGameQa = "Shift+F7";
            Check(!DesktopInput.RegisteredHotkeys(settings.Hotkeys).Contains("Shift+F7"),
                "Saved alternate QA chord silently reactivated diagnostic saving");
            Complete(window.SaveGameQaAsync(directory));
            Check(viewModel.GameQaStatus.Contains("입력이 없습니다") && !Directory.EnumerateFiles(directory, "input.png", SearchOption.AllDirectories).Any(), "No-frame QA captured desktop or created files");
            var frame = new ImageFrame(16, 12, 64, new byte[64 * 12]);
            var id = window.GameQa.Begin(frame, new("en", "ADV", false), GameQaProfile.From(settings.Profiles[0]));
            var save = window.SaveGameQaAsync(Path.Combine(directory, "cases"));
            window.GameQa.Complete(id, new([new("new result", 1, new(2, 2, 4, 4))], TimeSpan.Zero), "new result", true);
            Check(ReferenceEquals(save, window.SaveGameQaAsync(Path.Combine(directory, "cases"))), "UI queued duplicate save instead of suppressing it");
            Complete(save);
            var savedImage = Directory.EnumerateFiles(directory, "input.png", SearchOption.AllDirectories).Single();
            var load = GameQaFiles.ReadAsync(savedImage); Complete(load);
            Check(load.Result.Record.OcrStatus == "pending" && load.Result.Record.Ocr is null && viewModel.GameQaStatus.Contains("저장 완료"), "UI save reread newer state or lost nonmodal status");
            ((ComboBox)window.FindName("Profiles")).SelectedIndex = 1;
            Until(() => settings.ActiveProfileId == settings.Profiles[1].Id && !window.IsUserSavePending,
                "QA test did not complete the profile switch");
            Check(window.GameQa.Snapshot is null, "Profile changed but old scene remained saveable");
            Check(!window.IsVisible && !window.IsActive, "QA save changed focus/visibility");
            var hotkey = DesktopInput.ParseHotkey(new HotkeyOptions().SaveGameQa); var key = new PassThroughHotkey(); key.Configure(hotkey.Modifiers, hotkey.Key);
            Check(key.Observe(hotkey.Key, hotkey.Modifiers, true, false) && !key.Observe(hotkey.Key, hotkey.Modifiers, true, false), "QA chord repeats not suppressed");
            key.Observe(hotkey.Key, hotkey.Modifiers, false, false);
            Check(!key.Observe(hotkey.Key, hotkey.Modifiers, true, true) && key.Observe(hotkey.Key, hotkey.Modifiers, true, false), "Injected key activated QA or real key ignored");
            key.Reset(); Check(!key.Observe(hotkey.Key, hotkey.Modifiers, true, false), "Unregistered QA chord still active");
        }
        finally { Close(window); Directory.Delete(directory, true); }
    }
    internal static void AnimatedSubtitle()
    {
        var directory = TempDirectory(); var settings = new AppSettings(); settings.Validate();
        var window = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory))); window.ApplyLoadedSettings(settings);
        var overlay = Track(new TranslationWindow(settings.Profiles[0]));
        int tick = 0; bool animate = false; byte scene = 1;
        ImageFrame Frame() {
            var bytes = new byte[200 * 100 * 4]; byte shade = (byte)(animate && ++tick % 2 == 0 ? 180 : 80);
            for (int y = 30; y < 45; y++) for (int x = 40; x < 80; x++) for (int c = 0; c < 3; c++) bytes[y * 800 + x * 4 + c] = shade;
            var source = Volatile.Read(ref scene);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) {
                int p = y * 800 + x * 4;
                bytes[p] = source; bytes[p + 1] = bytes[p + 2] = (byte)(source * 50);
            }
            return new(200,100,800,bytes);
        }
        var pipeline = new TranslationPipeline(new TextOcr(), new MockTranslation(false), _ => Task.FromResult(Frame()), new("en","ADV",false), new(), new(), "synthetic", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(25));
        try {
            window.ObserveTranslation(pipeline, settings.ActiveProfileId);
            pipeline.Updated += u => overlay.Dispatcher.BeginInvoke(() => { if (u.Version == pipeline.Version) overlay.SetResult(u.Original, u.Translation, u.Message); });
            pipeline.Start(true);
            var translated = (TextBox)window.FindName("TranslatedText");
            animate = true;
            var sourceText = (TextBox)window.FindName("OcrText");
            for (byte screen = 1; screen <= 4; screen++) {
                Volatile.Write(ref scene, screen); string expected = "synthetic dialogue" + screen;
                Until(() => sourceText.Text == expected && translated.Text == "new response"
                    && ((MainViewModel)window.DataContext).Status.Contains("다음 입력 대기"), "Four-screen WPF flow failed to complete");
                var timer = System.Diagnostics.Stopwatch.StartNew(); bool kept = true;
                while (timer.ElapsedMilliseconds < 180) {
                    Pump(); kept &= translated.Text == "new response" && sourceText.Text == expected
                        && ((MainViewModel)window.DataContext).Status.Contains("다음 입력 대기")
                        && Text(overlay).Contains(expected) && Text(overlay).Contains("다음 입력 대기");
                    Thread.Sleep(2);
                }
                Check(kept, "A completed WPF screen left waiting or lost/mismatched its subtitle");
                Check(!Text(overlay).Contains("문자 인식 중"), "Overlay repeated recognizing after scene completion");
            }
        } finally { Complete(pipeline.DisposeAsync().AsTask()); Close(window); Close(overlay); Directory.Delete(directory,true); }
    }
    // No streaming callback: source rendering must not depend on the provider producing a token.
    private sealed class HeldTranslation : ITranslationService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<TranslationResult>> pending = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<string>> progress = new();
        public bool Has(string source) => pending.ContainsKey(source);
        public void Stream(string source, string text) { if (progress.TryGetValue(source, out var callback)) callback(text); }
        public void Finish(string source, string text) => pending[source].TrySetResult(new(text, TimeSpan.Zero, 0));
        public void Fail(string source) => pending[source].TrySetException(new TranslationException("Synthetic translation failure"));
        public void ReleaseAll() { foreach (var item in pending.Values) item.TrySetResult(new("cleanup", TimeSpan.Zero, 0)); }
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            var result = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (request.Progress is not null) progress[request.Text] = request.Progress;
            if (!pending.TryAdd(request.Text, result)) throw new Exception("Unexpected duplicate AI request");
            return result.Task; // Deliberately ignores cancellation to exercise stale-response protection.
        }
    }
    internal static void OriginalBeforeTranslation()
    {
        foreach (bool triggered in new[] { false, true })
        {
            var directory = TempDirectory(); var settings = new AppSettings(); settings.Profiles[0].ShowOriginal = true; settings.Validate();
            var window = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory))); window.ApplyLoadedSettings(settings);
            var overlay = Track(new TranslationWindow(settings.Profiles[0])); var service = new HeldTranslation();
            ImageFrame Frame(byte scene) => new(200, 100, 800, Enumerable.Repeat(scene, 80000).ToArray());
            var frame = Frame(1);
            var pipeline = new TranslationPipeline(new TextOcr(), service, _ => Task.FromResult(frame), new("en", "ADV", false), new(), new(), "synthetic", settle:TimeSpan.FromMilliseconds(400));
            try
            {
                var updates = new System.Collections.Concurrent.ConcurrentQueue<PipelineUpdate>();
                pipeline.Updated += updates.Enqueue;
                window.ObserveTranslation(pipeline, settings.ActiveProfileId);
                pipeline.Updated += update => overlay.Dispatcher.BeginInvoke(() => {
                    if (update.Version == pipeline.Version) overlay.SetResult(update.Original, update.Translation, update.Message);
                });
                var original = (TextBox)window.FindName("OcrText"); var translated = (TextBox)window.FindName("TranslatedText");
                pipeline.Start(false); Complete(pipeline.RequestManualAsync(image:frame));
                Until(() => service.Has("synthetic dialogue1"), "First held AI request missing");
                Check(original.Text == "synthetic dialogue1" && Text(overlay).Contains("synthetic dialogue1") && translated.Text == "", "OCR source waited for the first translation response");
                if (triggered)
                {
                    void Candidate(byte scene) => Until(() => updates.Any(u => u.Message.Contains("문장 확인 중") && u.Ocr?.Text == "synthetic dialogue" + scene), "Automatic OCR candidate was not inspected");
                    frame = Frame(2); Complete(pipeline.RequestTriggeredAsync()); Candidate(2);
                    Check(!service.Has("synthetic dialogue2") && original.Text == "synthetic dialogue1" && Text(overlay).Contains("synthetic dialogue1") && !Text(overlay).Contains("synthetic dialogue2"), "Unconfirmed OCR replaced the displayed original");
                    Check(updates.Where(u => u.Message.Contains("문장 확인 중")).All(u => u.Original is null), "Candidate event exposed unstable source text");
                    service.Stream("synthetic dialogue1", "partial translation1"); Pump();
                    Check(original.Text == "synthetic dialogue1", "Pending OCR disturbed the accepted source during streaming");
                    service.Finish("synthetic dialogue1", "translation1");
                    Until(() => translated.Text == "translation1", "Earlier valid translation did not complete during candidate confirmation");
                    Until(() => service.Has("synthetic dialogue2"), "Confirmed source never reached AI");
                    Check(original.Text == "synthetic dialogue2" && Text(overlay).Contains("synthetic dialogue2") && translated.Text == "translation1", "Confirmed source was not shown before the AI response");
                    service.Finish("synthetic dialogue2", "translation2");
                    Until(() => translated.Text == "translation2", "Second source translation missing");
                    frame = Frame(3); Complete(pipeline.RequestTriggeredAsync()); Candidate(3);
                    Check(original.Text == "synthetic dialogue2" && Text(overlay).Contains("synthetic dialogue2"), "Transient OCR candidate flashed on screen");
                    frame = Frame(2); Complete(pipeline.RequestTriggeredAsync());
                    Until(() => ((MainViewModel)window.DataContext).Status.Contains("다음 입력 대기"), "Known source did not return to waiting after transient OCR");
                    Check(!service.Has("synthetic dialogue3") && translated.Text == "translation2" && original.Text == "synthetic dialogue2", "Transient candidate resubmitted AI or changed the original");
                    // Changing typewriter/noisy candidates must stay private until the final source settles.
                    frame = Frame(4); Complete(pipeline.RequestTriggeredAsync()); Candidate(4);
                    Check(original.Text == "synthetic dialogue2", "First changing OCR candidate was displayed");
                    frame = Frame(5); Complete(pipeline.RequestTriggeredAsync()); Candidate(5);
                    Check(original.Text == "synthetic dialogue2" && !service.Has("synthetic dialogue4"), "Intermediate OCR candidate reached the screen or AI");
                    Until(() => service.Has("synthetic dialogue5"), "Final stable OCR source did not reach AI");
                    Check(original.Text == "synthetic dialogue5" && Text(overlay).Contains("synthetic dialogue5") && translated.Text == "translation2", "Stable final source waited for the translation response");
                    Check(!updates.Any(u => u.Original is "synthetic dialogue3" or "synthetic dialogue4"), "Discarded candidates leaked through another UI event");
                    service.Finish("synthetic dialogue5", "translation5"); Until(() => translated.Text == "translation5", "Stable final translation missing");
                }
                else
                {
                    service.Finish("synthetic dialogue1", "translation1");
                    Until(() => translated.Text == "translation1", "Initial translation missing");
                    Complete(pipeline.RequestManualAsync(image:Frame(2))); Until(() => service.Has("synthetic dialogue2"), "Second held AI request missing");
                    Check(original.Text == "synthetic dialogue2" && Text(overlay).Contains("synthetic dialogue2") && translated.Text == "translation1", "New OCR failed to render immediately while retaining the prior subtitle");
                    Complete(pipeline.RequestManualAsync(image:Frame(3))); Until(() => service.Has("synthetic dialogue3"), "Third held AI request missing");
                    service.Finish("synthetic dialogue2", "stale translation2");
                    service.Fail("synthetic dialogue3");
                    Until(() => ((MainViewModel)window.DataContext).Status.Contains("Synthetic translation failure"), "AI failure was not surfaced");
                    Check(original.Text == "synthetic dialogue3" && translated.Text == "translation1" && !Text(overlay).Contains("stale translation2"), "Failure/stale AI erased or reverted the latest source/subtitle");
                    Complete(pipeline.RequestManualAsync(image:Frame(4))); Until(() => service.Has("synthetic dialogue4"), "Retry request missing");
                    Check(original.Text == "synthetic dialogue4" && translated.Text == "translation1", "Retry source waited for AI");
                    service.Finish("synthetic dialogue4", "translation4");
                    Until(() => translated.Text == "translation4", "Retry result missing");
                }
                Check(!window.IsVisible && !overlay.IsVisible, "Original timing test displayed a desktop window");
            }
            finally { pipeline.Stop(); service.ReleaseAll(); Complete(pipeline.DisposeAsync().AsTask()); Close(window); Close(overlay); Directory.Delete(directory, true); }
        }
    }
    internal static void StreamingSubtitleRetention()
    {
        var directory=TempDirectory(); var settings=new AppSettings(); settings.Profiles[0].ShowOriginal=true; settings.Validate();
        var window=Track(new MainWindow(new SettingsStore(directory),new ApiKeyStore(directory))); window.ApplyLoadedSettings(settings);
        var overlay=Track(new TranslationWindow(settings.Profiles[0])); var service=new HeldTranslation();
        ImageFrame Frame(byte scene)=>new(200,100,800,Enumerable.Repeat(scene,80000).ToArray());
        var frame=Frame(1); var context=new TranslationSession();
        var pipeline=new TranslationPipeline(new TextOcr(),service,_=>Task.FromResult(frame),new("en","ADV",false),new(),context,"synthetic",settle:TimeSpan.Zero);
        try {
            window.ObserveTranslation(pipeline,settings.ActiveProfileId);
            pipeline.Updated+=u=>overlay.Dispatcher.BeginInvoke(()=>{if(u.Version==pipeline.Version)overlay.SetResult(u.Original,u.Translation,u.Message);});
            var translated=(TextBox)window.FindName("TranslatedText"); var original=(TextBox)window.FindName("OcrText");
            pipeline.Start(false); Complete(pipeline.RequestManualAsync(image:frame)); Until(()=>service.Has("synthetic dialogue1"),"Initial AI missing");
            service.Finish("synthetic dialogue1","translation1"); Until(()=>translated.Text=="translation1","Initial completed subtitle missing");
            for(byte scene=2;scene<=11;scene++) {
                string source="synthetic dialogue"+scene, previous="translation"+(scene-1), final="translation"+scene;
                frame=Frame(scene); Complete(pipeline.RequestTriggeredAsync()); Until(()=>service.Has(source),"Triggered AI missing");
                foreach(string blank in new[]{""," ","\r\n","\u3000"}) {
                    service.Stream(source,blank); Pump();
                    Check(translated.Text==previous && Text(overlay).Contains(previous) && original.Text==source,"Blank stream prefix cleared the completed subtitle");
                }
                service.Stream(source,"partial "+scene); Pump();
                Check(translated.Text=="partial "+scene,"Valid nonempty streaming stopped updating the subtitle");
                service.Finish(source,final); Until(()=>translated.Text==final,"Final subtitle missing");
                service.Stream(source,""); service.Stream(source,"late partial"); Pump();
                Check(translated.Text==final && Text(overlay).Contains(final) && ((MainViewModel)window.DataContext).Status.Contains("다음 입력 대기"),"Post-completion progress cleared/replaced the final subtitle or restored Translating");
            }
            frame=Frame(12); Complete(pipeline.RequestTriggeredAsync()); Until(()=>service.Has("synthetic dialogue12"),"Empty final request missing");
            service.Finish("synthetic dialogue12","  ");
            Until(()=>((MainViewModel)window.DataContext).Status.Contains("AI 번역 결과 없음"),"Empty final result was accepted instead of reported");
            Check(translated.Text=="translation11" && Text(overlay).Contains("translation11") && !context.TryLast("synthetic dialogue12",out _),"Empty final result cleared/cached a blank subtitle");
            service.Stream("synthetic dialogue12",""); service.Stream("synthetic dialogue12","late failure partial"); Pump();
            Check(translated.Text=="translation11" && ((MainViewModel)window.DataContext).Status.Contains("AI 번역 결과 없음"),"Late progress hid the failure or replaced the retained subtitle");
        }
        finally {pipeline.Stop();service.ReleaseAll();Complete(pipeline.DisposeAsync().AsTask());Close(window);Close(overlay);Directory.Delete(directory,true);}
    }
    private sealed class TextOcr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) =>
            Task.FromResult(new OcrResult([new("synthetic dialogue" + frame.Pixels.Span[0],1,new(0,0,200,100))],TimeSpan.Zero));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoOcr : IOcrService
    {
        public OcrState State => OcrState.NotConfigured;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => throw new Exception("Wpf test must not initialize OCR");
        public Task<OcrResult> RecognizeAsync(ImageFrame image, OcrOptions options, CancellationToken token) => throw new Exception("Wpf test must not call OCR");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class MockTranslation(bool delayed) : ITranslationService
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Started.TrySetResult(); request.Progress?.Invoke("partial response"); if (delayed) await Release.Task; return new(delayed ? "old response" : "new response", TimeSpan.Zero, 0); }
    }
    internal static void StaleTranslation()
    {
        var directory = TempDirectory(); var settings = new AppSettings(); settings.Profiles.Add(new() { Name = "Second" }); settings.Validate();
        var window = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory))); window.ApplyLoadedSettings(settings);
        var oldService = new MockTranslation(true); var newService = new MockTranslation(false);
        TranslationPipeline Make(ITranslationService service) => new(new NoOcr(), service, _ => throw new Exception("No real capture allowed"), new("en", "ADV", false), new(), new(), "synthetic");
        var old = Make(oldService); var current = Make(newService);
        try
        {
            using var queued = new ManualResetEventSlim();
            window.ObserveTranslation(old, settings.ActiveProfileId);
            old.Updated += update => { if (update.Translation == "old response") queued.Set(); };
            old.Start(false); old.SubmitClipboard("Old synthetic dialogue", true, false);
            Until(() => oldService.Started.Task.IsCompleted, "Mock translation not started");
            oldService.Release.TrySetResult();
            // Do not pump: queue a valid old result before changing the profile.
            Check(queued.Wait(TimeSpan.FromSeconds(3)), "Old result was not queued on the dispatcher");
            ((ComboBox)window.FindName("Profiles")).SelectedIndex = 1;
            // Selection now commits after the atomic save, while the previous profile stays live.
            Until(() => settings.ActiveProfileId == settings.Profiles[1].Id && !window.IsUserSavePending,
                "Profile switch did not commit its saved selection");
            Check(((TextBox)window.FindName("TranslatedText")).Text == "", "Already queued old response survived profile change");
            window.ObserveTranslation(current, settings.ActiveProfileId); current.Start(false); current.SubmitClipboard("New synthetic dialogue", true, false);
            var text = (TextBox)window.FindName("TranslatedText"); Until(() => text.Text == "new response", "New pipeline result not rendered");
            oldService.Release.TrySetResult(); Complete(old.DisposeAsync().AsTask()); Pump();
            Check(text.Text == "new response" && ((TextBox)window.FindName("OcrText")).Text == "New synthetic dialogue", "Retired pipeline overwrote current UI");
        }
        finally { oldService.Release.TrySetResult(); Close(window); Complete(old.DisposeAsync().AsTask()); Complete(current.DisposeAsync().AsTask()); Directory.Delete(directory, true); }
    }
}
