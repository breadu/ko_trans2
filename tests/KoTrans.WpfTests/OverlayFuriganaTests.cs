using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class OverlayFuriganaTests
{
    internal static void ProfileControls()
    {
        string dir = Temp(); var store = new SettingsStore(dir); var data = new AppSettings(); data.Validate();
        var first = data.CreateGameProfile("first"); var second = data.CreateGameProfile("second");
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        try {
            main.ShowProfileSettingsPage(0);
            var option = Named<CheckBox>(main, "JapaneseFuriganaOption");
            Check(!data.DefaultProfile.ShowJapaneseFurigana && !JsonSerializer.Deserialize<Profile>("{}")!.ShowJapaneseFurigana
                && option.IsChecked != true && option.Visibility == Visibility.Collapsed, "English/fresh profile enables furigana");
            Check(Equals(option.Content, "일본어 원문에 한자 읽기(후리가나) 표시"), "Furigana checkbox wording changed");
            Complete(main.SwitchProfileAsync(first)); Named<ComboBox>(main, "SourceLanguage").SelectedIndex = 1;
            Check(option.Visibility == Visibility.Visible, "Japanese option remains hidden");
            var ocrOptions = first.OcrOptions(); option.IsChecked = true;
            Check(first.OcrOptions() == ocrOptions, "Furigana display option changed OCR configuration");
            Named<ComboBox>(main, "SourceLanguage").SelectedIndex = 0;
            Check(option.Visibility == Visibility.Collapsed && option.IsChecked == true && first.ShowJapaneseFurigana, "English selection erased Japanese preference");
            Named<ComboBox>(main, "SourceLanguage").SelectedIndex = 1;
            Complete(main.SwitchProfileAsync(second)); Check(option.IsChecked != true, "Profile inherited another game's checkbox");
            Complete(main.SwitchProfileAsync(first)); Check(option.IsChecked == true, "Profile failed furigana restoration");
            Click(Button(main, "설정 저장")); var load = store.LoadAsync(); Complete(load);
            Check(load.Result.Profiles.Single(p => p.Id == first.Id).ShowJapaneseFurigana
                && !load.Result.Profiles.Single(p => p.Id == second.Id).ShowJapaneseFurigana, "Furigana setting did not persist per profile");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void RenderingAndLookup()
    {
        const string source = "日本の学校へ。\n未知の名前😀";
        int reads = 0; var queries = new List<string>();
        var words = new DictionaryWord[] { new(0, 2, "日本", "日本"), new(3, 2, "学校", "学校") };
        var profile = new Profile { Language = "일본어", ShowJapaneseFurigana = true, OverlayClickThrough = false, WordDictionaryEnabled = true };
        var window = Track(new TranslationWindow(profile,
            (_, _, _) => Task.FromResult<IReadOnlyList<DictionaryWord>>(words),
            (uri, _, _) => { queries.Add(Uri.UnescapeDataString(uri.AbsoluteUri)); return Task.CompletedTask; },
            (text, _) => { reads++; return Task.FromResult(Reading(text)); })); Track(window.Chrome);
        try {
            window.SetResult(source, "한국어 번역은 루비 대상이 아닙니다.", ""); Layout(window, 640, 340);
            var original = Named<DictionarySubtitle>(window, "OverlayOriginal");
            var labels = original.RubyLabels();
            Console.WriteLine("Ruby labels: " + string.Join("; ", labels.Select(l => $"{l.Text}: {l.Bounds}, source {l.SourceBounds}, font {l.FontSize:F2}")));
            Check(reads == 1 && labels.Count == 2 && labels.Select(l => l.Text).SequenceEqual(new[] { "にほん", "がっこう" }), "Known kanji ruby missing or unknown names annotated");
            Check(Math.Abs(labels[0].FontSize - original.FontSize * .6) < .01 && labels.All(l => l.FontSize <= original.FontSize * .6 + .01), "Ruby does not use 60% default/collision fit");
            Check(labels.All(l => l.Bounds.Top >= 0 && l.Bounds.Bottom < l.SourceBounds.Top
                && l.Bounds.Right <= original.ActualWidth + .1), "Ruby overlaps base text or escapes layout");
            Check(original.Text == source && original.Words.SequenceEqual(words), "Ruby changed the source or dictionary spans");
            var glyph = labels[0].SourceBounds;
            var hit = original.GetPositionFromPoint(new Point(glyph.Left + glyph.Width / 4, glyph.Top + glyph.Height / 2), true);
            Check(hit?.Parent is System.Windows.Documents.Run run && run.Text == "日本", "Glyph hit testing no longer resolves the original dictionary run");
            var before = original.DesiredSize;
            original.Hover(0); Layout(window, 640, 340);
            Check(original.HoveredWord?.Surface == "日本" && original.DesiredSize == before, "Hover with ruby reflowed the text");
            Check(original.Press(0, new Point(10, 30)) && original.Release(1, new Point(10, 30)) && queries.Single().Contains("日本"), "Kanji lookup lost its original query");
            Check(!original.CreateRubyLayer().IsHitTestVisible && !ToolTipService.GetIsEnabled(original), "Ruby intercepts input or creates tooltips");
            window.SetClickThrough(true); Layout(window, 640, 340);
            Check(!original.LookupEnabled && original.RubyLabels().Count == 2, "Click-through disabled furigana with dictionary interaction");
            window.SetClickThrough(false); Layout(window, 640, 340);
            Check(original.LookupEnabled && original.Words.SequenceEqual(words) && reads == 1, "Click-through toggling changed reading or dictionary words");
            profile.OverlayOriginalFontSize = 30; window.Apply(profile); Layout(window, 640, 340);
            Check(reads == 1 && Math.Abs(original.RubyLabels()[0].FontSize - 18) < .01, "Font change reanalyzed text or failed ratio update");
            var root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap(640, 340, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            if (Environment.GetEnvironmentVariable("KOTRANS_RUBY_PREVIEW") is { Length: > 0 } preview) {
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(preview); png.Save(file);
            }
            profile.ShowJapaneseFurigana = false; window.Apply(profile); Layout(window, 640, 340);
            Check(original.RubyLabels().Count == 0 && double.IsNaN(original.LineHeight) && original.Padding.Top == 0
                && original.Text == source && original.Words.SequenceEqual(words), "OFF did not restore the original/dictionary layout");
            profile.ShowJapaneseFurigana = true; window.Apply(profile); Layout(window, 640, 340);
            Check(reads == 1 && original.RubyLabels().Count == 2, "Same source was unnecessarily reanalyzed after OFF/ON");
            Layout(window, 320, 340); Check(original.RubyLabels().All(l => l.Bounds.Right <= original.ActualWidth + .1), "Narrow wrapping clipped ruby");
        } finally { Close(window); Close(window.Chrome); }
    }
    internal static void CancellationAndFallback()
    {
        var pending = new Dictionary<string, TaskCompletionSource<JapaneseReading>>();
        var text = new DictionarySubtitle(readJapanese: (source, _) => {
            var task = new TaskCompletionSource<JapaneseReading>(TaskCreationOptions.RunContinuationsAsynchronously); pending[source] = task; return task.Task;
        }) { FontSize = 24, TextWrapping = TextWrapping.Wrap };
        var grid = new Grid(); grid.Children.Add(text); grid.Children.Add(text.CreateRubyLayer());
        var host = Track(new Window { Content = grid });
        try {
            text.SetFuriganaEnabled(true); text.SetSource("日本", "ja"); text.SetSource("学校", "ja");
            pending["学校"].SetResult(new("学校", [new("学校", "がっこう", true)], ""));
            Until(() => text.RubySpanCount == 1, "New reading data did not arrive"); Layout(host, 400, 200);
            Console.WriteLine($"Ruby lifecycle: padding={text.Padding}, height={text.LineHeight}, size={text.ActualWidth}x{text.ActualHeight}, start={text.ContentStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward)}");
            Check(text.RubyLabels().Count == 1, "New reading did not render");
            pending["日本"].SetResult(new("日本", [new("日本", "にほん", true)], "")); Pump();
            Check(text.Text == "学校" && text.RubyLabels().Single().Text == "がっこう", "Late reading replaced newer source");
            text.SetSource("日本", "ja"); text.SetFuriganaEnabled(false); pending["日本"].SetResult(Reading("日本")); Pump(); Layout(host, 400, 200);
            Check(text.RubyLabels().Count == 0 && text.Text == "日本", "Canceled reading appeared after OFF");
            text.SetSource("English words", "en"); text.SetFuriganaEnabled(true);
            Check(!pending.ContainsKey("English words") && text.RubyLabels().Count == 0, "English source initialized Japanese reading");
            int errors = 0; text.AnalysisFailed += _ => errors++;
            text.SetSource("漢字", "ja"); pending["漢字"].SetException(new IOException("synthetic missing dictionary"));
            Until(() => errors == 1, "Local reading failure had no feedback");
            Check(text.Text == "漢字" && text.RubyLabels().Count == 0, "Reading failure removed source");
            text.SetSource("日本", "ja"); Complete(text.DisposeAsync()); pending["日本"].SetResult(Reading("日本")); Pump();
            Check(text.RubyLabels().Count == 0, "Disposed subtitle accepted late reading");
        } finally { Complete(text.DisposeAsync()); Close(host); }
    }
    private static JapaneseReading Reading(string text) => new(text,
        text == "日本" ? [new("日本", "にほん", true)] :
        [new("日本", "にほん", true), new("の", null, false), new("学校", "がっこう", true), new("へ。", null, false), new("未知", null, true), new("の", null, false), new("名前", null, true)], "");
    private static string Temp() { string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Furigana", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
}
