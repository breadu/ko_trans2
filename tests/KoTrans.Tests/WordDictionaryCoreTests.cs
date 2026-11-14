using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class WordDictionaryCoreTests
{
    internal static async Task Settings()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Dictionary", Guid.NewGuid().ToString("N"));
        try {
            var store = new SettingsStore(dir); var settings = new AppSettings(); settings.Validate();
            var p = settings.DefaultProfile;
            Check(!p.WordDictionaryEnabled && p.WordDictionary.EnglishUrl == WordDictionaryOptions.DefaultEnglishUrl,
                "New dictionary defaults incorrect");
            var legacy = JsonSerializer.Deserialize<Profile>("{}")!;
            Check(!legacy.WordDictionaryEnabled && legacy.WordDictionary.JapaneseUrl == WordDictionaryOptions.DefaultJapaneseUrl,
                "Legacy profiles lack safe dictionary defaults");
            Check(legacy.DictionaryPopupWindow is null, "Legacy profiles should use the unsaved centered popup default");
            p.DictionaryPopupWindow = new() { X = -1300, Y = 250, Width = 500, Height = 600,
                MonitorId = "portrait", MonitorDevice = "DISPLAY2", MonitorWorkX = -1600, MonitorWorkY = 0 };
            p.WordDictionaryEnabled = true; p.WordDictionary.EnglishUrl = "https://example.org/?q=%%SS";
            var game = settings.CreateGameProfile("separate"); game.WordDictionary.EnglishUrl = WordDictionaryOptions.DefaultEnglishUrl;
            game.WordDictionaryEnabled = false;
            game.DictionaryPopupWindow!.X = 300; game.DictionaryPopupWindow.MonitorId = "primary";
            await store.SaveAsync(settings); var restored = await store.LoadAsync();
            Check(restored.DefaultProfile.WordDictionaryEnabled && restored.DefaultProfile.WordDictionary.EnglishUrl.Contains("example.org")
                && !restored.Profiles[1].WordDictionaryEnabled && restored.Profiles[1].WordDictionary.EnglishUrl.Contains("naver"),
                "Dictionary settings aren't independently persisted per profile");
            Check(restored.DefaultProfile.DictionaryPopupWindow?.X == -1300 && restored.DefaultProfile.DictionaryPopupWindow?.MonitorId == "portrait"
                && restored.Profiles[1].DictionaryPopupWindow?.X == 300 && restored.Profiles[1].DictionaryPopupWindow?.MonitorId == "primary",
                "Dictionary popup placement isn't independently copied and persisted per profile");
            var options = new WordDictionaryOptions();
            Check(Uri.UnescapeDataString(options.SearchUri("聖杯", "ja").Fragment).Contains("聖杯"), "Japanese query encoding incorrect");
            Check(options.SearchUri("a&b#?", "en").AbsoluteUri.Contains("a%26b%23%3F"), "Dictionary query can escape its URL slot");
            foreach (var url in new[] { "javascript:%%SS", "file:///%%SS", "https://example.com/", "https://user:secret@example.com/%%SS", "https://%%SS.example.com/", "https://ABC%%SS.x/", "https://example.com/%%SS\n" }) {
                try { WordDictionaryOptions.ValidateTemplate(url); throw new Exception("Unsafe/missing template accepted: " + url); }
                catch (InvalidDataException) { count++; }
            }
            using var tokenizer = new WordDictionaryTokenizer();
            var words = tokenizer.Tokenize("Don't look up\nhello-world. 日本語 😀", "en", default);
            Check(words.Select(w => w.Surface).SequenceEqual(new[] { "Don't", "look", "up", "hello-world" }), "English word-only tokenization incorrect");
            Check(words.All(w => "Don't look up\nhello-world. 日本語 😀".Substring(w.Start, w.Length) == w.Surface), "English offsets incorrect");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { tokenizer.Tokenize("words", "en", cts.Token); throw new Exception("Dictionary cancellation ignored"); }
            catch (OperationCanceledException) { count++; }
            Console.WriteLine($"Word dictionary settings/URLs/English: {count} passed");
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    internal static void Japanese()
    {
        using var tokenizer = new WordDictionaryTokenizer();
        const string text = "昨日、食べました。\n言葉を探す😀未来。";
        var words = tokenizer.Tokenize(text, "ja", default);
        if (!words.Any(w => w.Surface == "食べました" && w.SearchText == "食べる") || !words.Any(w => w.Surface == "言葉")
            || !words.Any(w => w.Surface == "未来") || words.Any(w => text.Substring(w.Start, w.Length) != w.Surface)
            || words.Any(w => w.Surface.Contains('。') || w.Surface.Contains('\n')))
            throw new Exception("Japanese dictionary segmentation/lemma/Unicode offsets incorrect: " + string.Join(" / ", words.Select(w => $"{w.Start}:{w.Surface}→{w.SearchText}")));
        Console.WriteLine("Word dictionary local IPA: conjugation, lemma, punctuation, line break, surrogate offsets passed");
    }
}
