using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class SubtitleUxTests
{
    internal static void RubyGap()
    {
        var profile = new Profile { Language = "일본어" };
        var window = Track(new TranslationWindow(profile, readJapanese: (text, _) => Task.FromResult(new JapaneseReading(text,
            [new("日本", "にほん", true), new("学校", "がっこう", true)], "")))); Track(window.Chrome);
        try {
            foreach (double font in new[] { 21d, 30d }) foreach (double width in new[] { 640d, 340d }) {
                profile.OverlayOriginalFontSize = font;
                window.SetResult("日本の学校です。\n日本の学校へ行きます。", "한국어 번역입니다.", "");
                double Gap(bool ruby) {
                    profile.ShowJapaneseFurigana = ruby; window.Apply(profile); Layout(window, width, 420);
                    var source = Named<DictionarySubtitle>(window, "OverlayOriginal");
                    var translated = Named<TextBlock>(window, "OverlayTranslation");
                    var end = source.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                    return translated.TranslatePoint(new Point(), (UIElement)window.Content).Y
                        - source.TranslatePoint(end.BottomLeft, (UIElement)window.Content).Y;
                }
                double plain = Gap(false), withRuby = Gap(true);
                Console.WriteLine($"Ruby gap {font} DIP, width {width}: OFF={plain:F3}, ON={withRuby:F3}");
                Check(Math.Abs(plain - withRuby) < .1 && plain >= 9.9, "Ruby changed the actual last glyph-to-translation gap");
                Check(Gap(false) == plain, "Returning to OFF lost original spacing");
            }
        } finally { Close(window); Close(window.Chrome); }
    }
    internal static void PreviewAndColors()
    {
        string dir = Temp(); var data = new AppSettings(); data.Validate(); data.DefaultProfile.Language = "일본어";
        var other = data.CreateGameProfile("other"); other.Language = "영어"; other.OverlayColor = "#FF0000"; other.OverlayOriginalFontSize = 35;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        try {
            main.ShowProfileSettingsPage(3); Layout(main, 1060, 900);
            var original = Named<TextBlock>(main, "SubtitlePreviewOriginal"); var ko = Named<TextBlock>(main, "SubtitlePreviewTranslation");
            var panel = Named<Border>(main, "SubtitlePreviewSurface");
            var subtitleCards = (StackPanel)Named<SmoothScrollViewer>(main, "SubtitlePage").Content;
            Check(subtitleCards.Children.Count == 2
                && panel.IsDescendantOf(subtitleCards.Children[0])
                && Named<Button>(main, "OverlayVisibilityButton").IsDescendantOf(subtitleCards.Children[0]),
                "Subtitle preview/window action must be part of the first settings card");
            Check(Equals(Named<Button>(main, "OverlayVisibilityButton").Content, "자막창 열기")
                && !Elements<TextBlock>(main).Any(t => t.Text is "자막 미리보기" or "번역 자막창"),
                "Obsolete subtitle cards/titles or button label remained");
            var clickHelp = Named<TextBlock>(main, "OverlayPassThroughHelp");
            Check(clickHelp.ToolTip?.ToString()?.Contains("게임") == true
                && !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(Named<CheckBox>(main, "OverlayPassThrough"))),
                "Click-through option lost tooltip or accessibility explanation");
            Check(original.Text.Contains("日本語") && ko.Text.Contains("한국어"), "Preview sample language incorrect");
            var color = Named<ComboBox>(main, "FontColor");
            var sourceColor = Named<ComboBox>(main, "OriginalFontColor");
            string[] palette = ["#FFFFFF", "#FFE68A", "#A5DEFF", "#D3D3D3", "#FF819A", "#D3A7FF"];
            Check(palette.All(c => color.Items.Contains(c) && sourceColor.Items.Contains(c)),
                "Korean and source colors must expose the same six named presets");
            Check(new OverlayColorLabelConverter().Convert("#FF819A", typeof(string), null!, CultureInfo.InvariantCulture).Equals("코랄 핑크")
                && new OverlayColorLabelConverter().Convert("#D3A7FF", typeof(string), null!, CultureInfo.InvariantCulture).Equals("연보라색"),
                "Added subtitle colors have no friendly labels");
            Check(new OverlayColorLabelConverter().Convert("#FFFFFF", typeof(string), null!, CultureInfo.InvariantCulture).Equals("흰색"), "Preset color not named");
            var caption = (FrameworkElement)color.ItemTemplate.LoadContent(); caption.DataContext = "#FFE68A";
            var host = Track(new Window { Content = caption });
            try { Layout(host, 200, 80); Check(Text(host).Contains("노란색") && !Text(host).Contains("#"), "Actual ComboBox item template shows hex codes"); }
            finally { Close(host); }
            var fontSize = Named<Slider>(main, "FontSlider"); var sourceSize = Named<Slider>(main, "OriginalFontSlider");
            fontSize.Value = 22.5; sourceSize.Value = 27;
            Layout(main, 1060, 900);
            Check(Named<TextBlock>(main, "TranslationFontSizeValue").Text == "22.5"
                && Named<TextBlock>(main, "OriginalFontSizeValue").Text == "27",
                "Subtitle font size values are not shown live or lose fractional settings");
            fontSize.Value = 32;
            color.SelectedItem = "#A5DEFF"; sourceColor.SelectedItem = "#FFE68A";
            Named<Slider>(main, "OpacitySlider").Value = .45;
            Check(ko.FontSize == 32 && original.FontSize == 27 && panel.Opacity == .45,
                "Preview sizes/opacity did not update immediately");
            Check(((SolidColorBrush)ko.Foreground).Color == Color.FromRgb(0xA5, 0xDE, 0xFF)
                && ((SolidColorBrush)original.Foreground).Color == Color.FromRgb(0xFF, 0xE6, 0x8A), "Preview colors differ from settings");
            Named<CheckBox>(main, "ShowKorean").IsChecked = false;
            Check(ko.Visibility == Visibility.Collapsed && original.Visibility == Visibility.Visible, "Translation OFF not previewed");
            Named<CheckBox>(main, "ShowKorean").IsChecked = true; Named<CheckBox>(main, "ShowOriginal").IsChecked = false;
            Check(original.Visibility == Visibility.Collapsed && ko.Visibility == Visibility.Visible, "Original OFF not previewed");
            Complete(main.SwitchProfileAsync(other));
            Check(original.Text.Contains("original") && original.FontSize == 35
                && ((SolidColorBrush)ko.Foreground).Color == Colors.Red, "Preview not refreshed on profile switch");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void Prompts()
    {
        var profile = new Profile { OverlayFontSize = 31, OverlayColor = "#FFE68A", AutomaticWatch = false };
        var window = Track(new TranslationWindow(profile)); Track(window.Chrome);
        try {
            var guide = Named<TextBlock>(window, "OverlayInstruction"); var translation = Named<TextBlock>(window, "OverlayTranslation");
            var source = Named<DictionarySubtitle>(window, "OverlayOriginal");
            Check(guide.Visibility == Visibility.Visible && guide.Text.Contains("시작") && guide.FontSize == translation.FontSize
                && ((SolidColorBrush)guide.Foreground).Color == ((SolidColorBrush)translation.Foreground).Color, "Stopped guide not prominent/styled as subtitles");
            window.BeginSession(true);
            Check(guide.Visibility == Visibility.Visible && guide.Text.Contains("지정한 키나 버튼"), "Initial trigger instruction missing");
            window.SetPipelineResult(new(1, PipelineState.Watching, "waiting")); window.SetAutomaticTranslation(true);
            Check(guide.Visibility == Visibility.Visible, "Idle updates removed first-trigger instruction early");
            window.SetBusy(true); Check(guide.Visibility == Visibility.Collapsed, "First input/OCR did not clear instruction");
            window.SetPipelineResult(new(1, PipelineState.Watching, "next input")); window.SetBusy(false);
            Check(guide.Visibility == Visibility.Collapsed, "Instruction reappeared before next trigger");
            window.SetResult("Actual source.", "실제 번역", ""); window.ShowStoppedPrompt();
            Check(guide.Visibility == Visibility.Visible && translation.Visibility == Visibility.Collapsed && source.Visibility == Visibility.Collapsed,
                "Stopped guide mixed with old subtitle text");
            window.BeginSession(false);
            Check(guide.Visibility == Visibility.Collapsed && source.Text.Length == 0 && translation.Text.Length == 0,
                "Restart retained instruction or previous session text");
            profile.ShowTranslation = false; window.Apply(profile); window.ShowStoppedPrompt();
            Check(guide.Text.Contains("원문 인식") && guide.Visibility == Visibility.Visible, "Original-only stop has no instruction");
            window.BeginSession(true); window.DismissInputPrompt();
            Check(guide.Visibility == Visibility.Collapsed && !source.Text.Contains("트리거") && translation.Text.Length == 0,
                "Instruction polluted source/translation data");
            window.SetPipelineResult(new(2, PipelineState.Stopped, "stopped"));
            Check(guide.Visibility == Visibility.Visible, "Pipeline stop did not restore stopped instruction");
            window.BeginSession(false, waitForClipboard: true);
            Check(guide.Visibility == Visibility.Visible && guide.Text == "클립보드의 데이터를 기다리고 있습니다",
                "Clipboard startup lacks its initial waiting instruction");
            window.SetPipelineResult(new(3, PipelineState.Watching, "waiting")); window.SetBusy(false);
            Check(guide.Visibility == Visibility.Visible, "Idle update removed clipboard instruction before first input");
            window.SetResult("Copied source.", "복사된 번역", "");
            window.SetPipelineResult(new(4, PipelineState.Watching, "waiting again")); window.SetBusy(false);
            Check(guide.Visibility == Visibility.Collapsed && source.Text == "Copied source." && translation.Text == "복사된 번역",
                "Clipboard instruction returned after the first result or replaced subtitle content");
            window.BeginSession(false, waitForClipboard: true);
            window.SetPipelineResult(new(5, PipelineState.Error, "합성 입력 오류") { FailureArea = PipelineStatusArea.Input });
            Check(guide.Visibility == Visibility.Collapsed && Named<TextBlock>(window, "OverlayNotice").Text == "합성 입력 오류",
                "Clipboard instruction covered an actual error");
            window.ShowStoppedPrompt();
            Check(guide.Text.Contains("시작") && !guide.Text.Contains("클립보드"), "Stopped clipboard session still claims to wait for input");
        } finally { Close(window); Close(window.Chrome); }
    }
    internal static void OriginalOnlyRecognition()
    {
        string dir = Temp(); var data = new AppSettings(); data.Validate();
        var profile = data.DefaultProfile; profile.AutomaticWatch = true; profile.CaptureMode = CaptureMode.Region;
        profile.CharacterDictionaryEnabled = true; profile.CharacterDictionaryPath = Path.Combine(dir, "absent.csv");
        profile.Translation.SwitchProvider(TranslationProvider.HyMtLocal);
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        var engine = new OriginalOnlyOcr();
        var initial = new TranslationPipeline(engine, new MockTranslationService(), _ => throw new Exception("Inactive fixture captured"),
            profile.OcrOptions(), new(), new(), "");
        TranslationPipeline? resumed = null; int restarts = 0;
        try {
            main.ObserveTranslation(initial, profile.Id, watch: true); initial.Start(false);
            main.RestartSubtitleRecognitionOverride = () => {
                restarts++;
                // Production construction must bypass AI, local model preparation and character dictionaries.
                resumed = main.CreateProfilePipeline(engine, null, _ => Task.FromResult(new ImageFrame(120, 60, 480, new byte[28800])));
                main.ObserveTranslation(resumed, profile.Id, watch: true);
                main.StartProfileMonitoring(resumed, true, _ => { });
                return Task.FromResult(true);
            };
            Named<CheckBox>(main, "ShowKorean").IsChecked = false;
            Until(() => Named<TextBox>(main, "OcrText").Text.Contains("Original source"), "Disabling translation stopped automatic OCR");
            Check(restarts == 1 && !initial.IsRunning && resumed?.IsRunning == true && !profile.ShowTranslation,
                "Source-only mode did not restart the running recognition session");
            Check(Text(overlay).Contains("Original source") && Named<TextBlock>(overlay, "OverlayTranslation").Text == "",
                "OCR-only result was not shown as original subtitles");
            Check(Named<Button>(overlay.Chrome, "OverlayService").MinWidth == 112, "Source-only badge remained narrow");
            // A display preference must never start a previously stopped session.
            Click(Named<Button>(overlay.Chrome, "OverlayToggleTranslation"));
            Named<CheckBox>(main, "ShowKorean").IsChecked = true;
            Check(restarts == 1 && resumed?.IsRunning == false, "Display change started a stopped recognition session");
        } finally {
            Complete(initial.DisposeAsync().AsTask()); if (resumed is not null) Complete(resumed.DisposeAsync().AsTask());
            Close(overlay); Close(overlay.Chrome); Close(main); Directory.Delete(dir, true);
        }
    }
    private sealed class OriginalOnlyOcr : IOcrService
    {
        public OcrCapabilities Capabilities => OcrCapabilities.Basic;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) =>
            Task.FromResult(new OcrResult([new("Original source without translation.", 1, new(10, 10, 90, 20))], TimeSpan.Zero));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static string Temp() { string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.SubtitleUx", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
}
