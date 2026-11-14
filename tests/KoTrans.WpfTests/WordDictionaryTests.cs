using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class WordDictionaryTests
{
    internal static void ContinuousHighlight()
    {
        var p = new Profile { Language = "일본어", OverlayClickThrough = false, WordDictionaryEnabled = true, OverlayOriginalFontSize = 29.5 };
        var window = Track(new TranslationWindow(p, (text, _, _) => Task.FromResult<IReadOnlyList<DictionaryWord>>([new(0, text.Length, text, text)]))); Track(window.Chrome);
        try {
            var original = Named<DictionarySubtitle>(window, "OverlayOriginal");
            window.SetResult("日本語", null, ""); Layout(window, 640, 300);
            Check(!ToolTipService.GetIsEnabled(original) && original.ToolTip is null && original.Inlines.OfType<Run>().All(r => r.ToolTip is null),
                "Dictionary hover can still create a captured tooltip popup");
            var before = original.DesiredSize; var source = original.Text;
            original.Hover(0); Layout(window, 640, 300);
            Check(original.Background is DrawingBrush && original.DesiredSize == before && original.Text == source,
                "Japanese hover changes layout/source or has no block background");
            var brush = (DrawingBrush)original.Background;
            var drawing = (DrawingGroup)brush.Drawing;
            var blocks = drawing.Children.OfType<GeometryDrawing>().Where(g => g.Brush == Brushes.PaleGoldenrod).ToArray();
            Check(blocks.Length == 1 && original.Inlines.OfType<Run>().All(r => r.Background is null), "Three Japanese characters aren't one highlight block");
            var rect = blocks[0].Geometry.Bounds;
            foreach (double scale in new[] { 1d, 1.25, 2.5 }) {
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
                int width = (int)Math.Ceiling(original.ActualWidth * scale), height = (int)Math.Ceiling(original.ActualHeight * scale);
                var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(visual);
                var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                int y = (int)((rect.Top + rect.Height / 2) * scale);
                bool continuous = true;
                for (int x = (int)Math.Ceiling(rect.Left * scale) + 1; x < (int)Math.Floor(rect.Right * scale) - 1; x++) {
                    int at = (y * width + x) * 4;
                    if (pixels[at] != 170 || pixels[at + 1] != 232 || pixels[at + 2] != 238 || pixels[at + 3] != 255) continuous = false;
                }
                Check(continuous, $"Japanese highlight has raster seams at {scale * 100}% scale");
            }
            original.Hover(1); Check(ReferenceEquals(brush, original.Background), "Moving within a word repaints/rebuilds its background");
            original.Hover(-1); Check(original.Background is null, "Leaving a word didn't restore its background");
            window.SetResult(string.Concat(Enumerable.Repeat("日本語", 6)), null, ""); Layout(window, 320, 320); original.Hover(0); Layout(window, 320, 320);
            var wrapped = ((DrawingGroup)((DrawingBrush)original.Background!).Drawing).Children.OfType<GeometryDrawing>().Where(g => g.Brush == Brushes.PaleGoldenrod).ToArray();
            Check(wrapped.Length >= 2 && wrapped.Select(g => g.Geometry.Bounds.Top).Distinct().Count() == wrapped.Length,
                "Wrapped word highlight isn't exactly one rectangle per visual line");
            window.ChangeClickThrough(true); Check(original.Background is null && original.HoveredWord is null,
                "Disabling dictionary left its hover background behind");
        } finally { Close(window); Close(window.Chrome); }
    }
    internal static void WordsAndLayout()
    {
        using var tokenizer = new WordDictionaryTokenizer();
        var requests = new List<Uri>();
        var p = new Profile { OverlayClickThrough = false, WordDictionaryEnabled = true, ShowOriginal = true, ShowTranslation = false };
        Task<IReadOnlyList<DictionaryWord>> Analyze(string text, string language, CancellationToken token) =>
            Task.FromResult(language == "en" ? tokenizer.Tokenize(text, language, token) : (IReadOnlyList<DictionaryWord>)[new(0, 3, "食べた", "食べる"), new(4, 2, "聖杯", "聖杯")]);
        var window = Track(new TranslationWindow(p, Analyze, (uri, _, _) => { requests.Add(uri); return Task.CompletedTask; })); Track(window.Chrome);
        try {
            const string source = "Don't look up\nthese words.";
            window.SetResult(source, "한국어 번역은 사전 대상이 아님", ""); Layout(window, 720, 280);
            var original = Named<DictionarySubtitle>(window, "OverlayOriginal");
            Check(original.Text == source && original.Words.Count == 5 && original.Words[0].Surface == "Don't", "English words/line breaks were changed or phrase lookup was added");
            var translation = Elements<TextBlock>(window).Single(t => t.Text.StartsWith("한국어 번역"));
            Check(translation is not DictionarySubtitle && translation.Visibility == Visibility.Collapsed, "Korean text gained word lookup");
            var before = original.DesiredSize; original.Hover(0); Layout(window, 720, 280);
            Check(original.HoveredWord?.SearchText == "Don't" && original.DesiredSize == before, "Hover changes text geometry or doesn't select a single word");
            Check(original.Background is DrawingBrush && original.Inlines.OfType<Run>().All(r => r.Background is null), "Hover has no continuous block background or still paints per-glyph backgrounds");
            Check(!window.CanDragBody(original.Inlines.OfType<Run>().Single(r => r.Text == "Don't")), "Word click starts overlay dragging");
            original.Press(0, new(10, 10)); original.Release(0, new(10, 10));
            Check(requests.Count == 1 && requests[0].Host == "en.dict.naver.com" && requests[0].AbsoluteUri.Contains("Don%27t"), "English click did not open encoded Naver word popup");
            original.Hover(source.IndexOf('\n')); Check(original.HoveredWord is null, "Whitespace selects a word");
            original.Press(0, new()); original.Release(0, new(50, 20)); Check(requests.Count == 1, "Dragging opened a dictionary");
            double originalTop = original.TransformToAncestor((Visual)window.Content).Transform(new Point()).Y;
            p.ShowOriginal = false; p.ShowTranslation = true; window.Apply(p); Layout(window, 720, 280);
            double translatedTop = translation.TransformToAncestor((Visual)window.Content).Transform(new Point()).Y;
            Check(originalTop == translatedTop, "Translation-only top spacing differs from original-only spacing");
            Check(!original.LookupEnabled, "Hidden original retains dictionary interaction");
            p.ShowOriginal = true; window.Apply(p); original.Press(0, new()); window.SetClickThrough(true); original.Release(0, new());
            Check(p.WordDictionaryEnabled && !original.LookupEnabled && requests.Count == 1, "Click-through lost saved flag or allowed a pending word click");
            Check(Named<CheckBox>(window.Chrome, "OverlayWordDictionary").IsChecked == true && !Named<CheckBox>(window.Chrome, "OverlayWordDictionary").IsEnabled,
                "Chrome dictionary flag/disabled state doesn't follow click-through");
            window.SetClickThrough(false); Check(original.LookupEnabled, "Dictionary did not resume when click-through was disabled");
            original.Press(0, new()); window.SetResult("replacement source", null, ""); original.Release(0, new());
            Check(requests.Count == 1, "An old source click opened a dictionary after source replacement");
            p.Language = "일본어"; window.SetResult("食べた。聖杯", null, ""); window.Apply(p);
            original.Hover(1); original.Press(1, new()); original.Release(1, new());
            Check(original.HoveredWord?.Surface == "食べた" && requests.Count == 2 && requests[1].Host == "ja.dict.naver.com"
                && Uri.UnescapeDataString(requests[1].Fragment).Contains("食べる"), "Japanese conjugated word wasn't routed to its lemma/Naver Japanese popup");
            Check(window.CanDragBody(translation), "Non-interactive translation no longer permits dragging");
            Layout(window.Chrome, 320, 150);
            var pass = Named<CheckBox>(window.Chrome, "OverlayPassThrough"); var dictionary = Named<CheckBox>(window.Chrome, "OverlayWordDictionary");
            Check(dictionary.TransformToAncestor((Visual)window.Chrome.Content).Transform(new Point()).X >= pass.TransformToAncestor((Visual)window.Chrome.Content).Transform(new Point()).X + pass.ActualWidth,
                "Dictionary checkbox isn't to the right of click-through");
        } finally { Close(window); Close(window.Chrome); }
    }

    internal static void StaleAndFailure()
    {
        var pending = new List<TaskCompletionSource<IReadOnlyList<DictionaryWord>>>();
        var p = new Profile { OverlayClickThrough = false, WordDictionaryEnabled = true };
        var window = Track(new TranslationWindow(p, (_, _, _) => { var task = new TaskCompletionSource<IReadOnlyList<DictionaryWord>>(); pending.Add(task); return task.Task; })); Track(window.Chrome);
        try {
            var original = Named<DictionarySubtitle>(window, "OverlayOriginal");
            window.SetResult("old", null, ""); window.SetResult("new", null, "");
            pending[0].SetResult([new(0, 3, "old", "old")]); Pump(); Check(original.Text == "new" && original.Words.Count == 0, "Late tokenization replaced newer OCR source");
            pending[1].SetResult([new(0, 3, "new", "new")]); Pump(); Check(original.Words.Single().Surface == "new", "Latest tokenization wasn't applied");
            window.SetResult("failure", null, ""); pending[2].SetException(new IOException("synthetic dictionary unavailable")); Pump();
            Check(original.Text == "failure" && Named<TextBlock>(window, "OverlayNotice").Text.Contains("단어 정보"), "Analyzer failure erased source or silently failed");
            window.SetResult("closed", null, ""); Close(window); pending[3].SetResult([new(0, 6, "closed", "closed")]); Pump();
            Check(original.Words.Count == 0, "Late analysis recreated runs after close");
        } finally { foreach (var task in pending) task.TrySetCanceled(); Close(window); Close(window.Chrome); }
    }

    internal static void ProfileSynchronization()
    {
        string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate(); var p = data.DefaultProfile;
        var game = data.CreateGameProfile("dictionary game"); game.WordDictionary.EnglishUrl = "https://example.com/lookup?q=%%SS";
        var store = new SettingsStore(dir);
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        var window = Track(main.CreateOverlayWindow()); Track(window.Chrome);
        try {
            main.ShowProfileSettingsPage(3); Layout(main, 1060, 740);
            var pass = Named<CheckBox>(main, "OverlayPassThrough"); var option = Named<CheckBox>(main, "WordDictionaryOption");
            var chrome = Named<CheckBox>(window.Chrome, "OverlayWordDictionary");
            Check(option.IsChecked == false && !option.IsEnabled && !chrome.IsEnabled, "Dictionary defaults are not off/disabled under click-through");
            Check(option.Opacity == .45 && chrome.Opacity == .45, "Profile and overlay dictionary disabled appearance differ");
            pass.IsChecked = false; Pump(); option.IsChecked = true; Pump();
            Check(p.WordDictionaryEnabled && chrome.IsChecked == true && chrome.IsEnabled, "Settings dictionary checkbox didn't update chrome/profile");
            Check(option.Opacity == 1 && chrome.Opacity == 1, "Enabled dictionary still appears disabled");
            Named<CheckBox>(window.Chrome, "OverlayPassThrough").IsChecked = true; Pump();
            Check(pass.IsChecked == true && option.IsChecked == true && !option.IsEnabled && !chrome.IsEnabled, "Chrome click-through didn't preserve/synchronize dictionary state");
            pass.IsChecked = false; chrome.IsChecked = false; Pump(); Check(!p.WordDictionaryEnabled && option.IsChecked == false, "Chrome dictionary checkbox didn't update settings");
            var url = Named<TextBox>(main, "EnglishDictionaryUrl"); url.Text = "javascript:%%SS";
            Check(!CompleteBool(main.SaveCurrentProfileAsync()) && main.FeedbackToast.Kind == FeedbackKind.Error, "Unsafe URL was saved");
            url.Text = "https://example.org/?word=%%SS"; option.IsChecked = true;
            Check(CompleteBool(main.SaveCurrentProfileAsync()), "Valid dictionary settings weren't saved");
            var load = store.LoadAsync(); Complete(load); var restored = load.Result;
            Check(restored.DefaultProfile.WordDictionaryEnabled && restored.DefaultProfile.WordDictionary.EnglishUrl == url.Text, "Profile dictionary preference/URL didn't persist");
            Check(CompleteBool(main.SwitchProfileAsync(game)), "Game profile switch failed");
            Check(option.IsChecked == false && Named<TextBox>(main, "EnglishDictionaryUrl").Text.Contains("example.com"), "Game profile inherited another profile's edits");
            Check(CompleteBool(main.SwitchProfileAsync(p)) && option.IsChecked == true && url.Text.Contains("example.org"), "Returning profile lost dictionary controls");
        } finally { Close(window); Close(window.Chrome); Close(main); Directory.Delete(dir, true); }
    }
    internal static void PopupPolicyAndFailure()
    {
        var bounds = DictionaryPopupPlacement.Calculate(0, 0, 1600, 1000, 640);
        Check(bounds == new DictionaryPopupBounds(480, 166, 640, 667), "Dictionary popup isn't centered at reference size");
        var physical = new DictionaryPopupBounds(-1200, 400, 800, 1000);
        var converted = DictionaryBrowserCoordinates.Convert(physical, [
            new(new(0, 0, 2560, 1440), new(0, 0, 2560, 1400), 96),
            new(new(-1600, 0, 1600, 2560), new(-1600, 0, 1600, 2500), 240)]);
        Check(converted.Left < 0 && converted.Width == 320 && converted.Height == 400, "Mixed-DPI popup coordinates lost secondary display/scale");
        var info = DictionaryBrowser.BuildStartInfo(@"C:\browser\msedge.exe", @"C:\Test Profile\KoTrans2\DictionaryBrowser\msedge", bounds);
        Check(!info.UseShellExecute && info.ArgumentList.Contains("--remote-debugging-address=127.0.0.1")
            && info.ArgumentList.Contains("--remote-debugging-port=0") && info.ArgumentList.Any(a => a.StartsWith("--app=file:///"))
            && info.ArgumentList.Count(a => a.StartsWith("--user-data-dir=")) == 1, "Dictionary launch doesn't isolate its app popup/profile");
        using var tokenizer = new WordDictionaryTokenizer();
        var request = new TaskCompletionSource(); CancellationToken requestedToken = default;
        var p = new Profile { OverlayClickThrough = false, WordDictionaryEnabled = true };
        var window = Track(new TranslationWindow(p,
            (text, lang, token) => Task.FromResult(tokenizer.Tokenize(text, lang, token)),
            (_, _, token) => { requestedToken = token; return request.Task; })); Track(window.Chrome);
        try {
            var original = Named<DictionarySubtitle>(window, "OverlayOriginal");
            window.SetResult("word", "번역 유지", ""); original.Press(0, new()); original.Release(0, new());
            window.ChangeClickThrough(true);
            Check(requestedToken.IsCancellationRequested, "Enabling click-through didn't cancel pending dictionary lookup");
            request.SetException(new IOException("late popup error")); Pump();
            Check(Named<TextBlock>(window, "OverlayNotice").Visibility == Visibility.Collapsed, "Canceled popup error replaced current overlay status");
            request = new TaskCompletionSource(); window.ChangeClickThrough(false);
            original.Press(0, new()); original.Release(0, new()); request.SetException(new IOException("synthetic browser unavailable")); Pump();
            Check(Named<TextBlock>(window, "OverlayNotice").Text.Contains("사전 페이지를 열지 못") && Text(window).Contains("번역 유지")
                && original.Text == "word", "Popup failure erased source/translation or hid the cause");
        } finally { request.TrySetCanceled(); Close(window); Close(window.Chrome); }
    }
    private static bool CompleteBool(Task<bool> task) { Complete(task); return task.Result; }
}
