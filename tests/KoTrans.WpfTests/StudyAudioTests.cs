using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class StudyAudioTests
{
    private static StudyResult Result() => new("전체 문장 번역", "문장 설명", [
        new("Hello", "/həˈləʊ/", "greeting", "인사", [new("Hello, world.", "안녕하세요.")], "인사말"),
        new("missing", "", "", "없음", [], ""), new("look up", "", "", "찾다", [], ""), new("学校", "がっこう", "", "학교", [], "")]);
    internal static void VocabularyOnly()
    {
        var audio = new Audio(); int resolves = 0;
        var window = Track(new StudyWindow(new(), audio, () => { Interlocked.Increment(ref resolves); return "synthetic-voice"; }));
        try {
            Check(resolves == 0 && audio.Discoveries == 0, "Voice engine initialized at window startup");
            window.SetSource("Hello, world.", "en"); window.Render(Result()); Complete(window.VoicePreparation); Layout(window, 500, 600);
            var buttons = Elements<Button>(window).Where(b => b.Visibility == Visibility.Visible).ToArray();
            Check(buttons.Length == 1 && Equals(buttons[0].Tag, "Hello"), "Playback shown for missing/phrase/Japanese words or examples");
            Check(audio.Words.SequenceEqual(new[] { "Hello", "missing" }), "Non-word or example text reached voice lookup");
            var scroll = Elements<ScrollViewer>(window).Single();
            scroll.ScrollToVerticalOffset(100); Layout(window, 500, 600);
            var heading = Elements<TextBlock>(window).Single(t => t.Text == "Hello");
            double y = heading.TranslatePoint(new Point(0, 0), scroll).Y, extent = scroll.ExtentHeight, offset = scroll.VerticalOffset;
            Click(buttons[0]); Check(audio.Played.SequenceEqual(new[] { "Hello" }), "Word playback not wired to real heading button");
            Layout(window, 500, 600);
            Check(Named<TextBlock>(window, "StudyAudioFeedback").Visibility == Visibility.Collapsed
                && !Text(window).Contains("로컬 발음 재생 중"), "Playback start inserted an informational row into the reader");
            audio.EmitStatus("발음 재생 완료"); Layout(window, 500, 600);
            Check(Math.Abs(scroll.ExtentHeight - extent) < .1 && Math.Abs(scroll.VerticalOffset - offset) < .1
                && Math.Abs(heading.TranslatePoint(new Point(0, 0), scroll).Y - y) < .1
                && Named<TextBlock>(window, "StudyAudioFeedback").Visibility == Visibility.Collapsed,
                "Playback start/end shifted the document, scroll offset or word position");
            for (int i = 0; i < 3; i++) { Click(buttons[0]); audio.EmitStatus("발음 재생 완료"); }
            Layout(window, 500, 600);
            Check(Math.Abs(scroll.ExtentHeight - extent) < .1 && Math.Abs(heading.TranslatePoint(new Point(0, 0), scroll).Y - y) < .1,
                "Repeated playback made study text jump");
            audio.Emit("MP3 파일 누락"); Layout(window, 500, 600);
            Check(Named<TextBlock>(window, "StudyAudioFeedback").Visibility == Visibility.Visible && Text(window).Contains("MP3 파일 누락"),
                "Separating playback status hid real audio errors");
            Click(buttons[0]); Layout(window, 500, 600);
            Check(Named<TextBlock>(window, "StudyAudioFeedback").Visibility == Visibility.Collapsed,
                "Successful retry left a stale pronunciation error visible");
            window.Close(); Check(audio.Stops > 0 && !audio.Disposed, "Hiding reader did not stop audio or destroyed resident player");
            audio.Emit("MP3 파일 누락"); Check(Text(window).Contains("MP3 파일 누락"), "Audio failure not presented in reader");
            int before = audio.Discoveries;
            window.SetSource("日本語の文です。", "ja"); window.Render(Result()); Complete(window.VoicePreparation);
            Check(audio.Discoveries == before && !Elements<Button>(window).Any(b => b.Visibility == Visibility.Visible), "Japanese study exposed English playback");
            window.SetSource("Hello again.", "en"); audio.Broken = true; window.Render(Result()); Complete(window.VoicePreparation);
            Check(!Elements<Button>(window).Any(b => b.Visibility == Visibility.Visible)
                && Text(window).Contains("로컬 발음 자료를 읽을 수 없습니다"), "Corrupt voice folder left playback enabled or hid diagnosis");
            audio.Broken = false; window.Render(Result()); Complete(window.VoicePreparation);
            Check(Elements<Button>(window).Count(b => b.Visibility == Visibility.Visible) == 1, "Voice error prevented retry");
        } finally { Close(window); }
        Check(audio.Disposed, "Closing reader leaked pronunciation player");

        var absent = new Audio(); var noData = Track(new StudyWindow(new(), absent, () => null));
        try {
            noData.SetSource("Hello.", "en"); noData.Render(Result()); Complete(noData.VoicePreparation);
            Check(absent.Discoveries == 0 && !Elements<Button>(noData).Any(b => b.Visibility == Visibility.Visible), "Missing voice directory showed a playback button");
        } finally { Close(noData); }
    }
    internal static void StaleAndDiscovery()
    {
        var delayed = new Audio { Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var window = Track(new StudyWindow(new(), delayed, () => "synthetic-voice"));
        try {
            window.SetSource("Hello.", "en"); window.Render(Result()); Until(() => delayed.Discoveries == 1, "Voice lookup did not start");
            var oldButton = Elements<Button>(window).First(b => Equals(b.Tag, "Hello"));
            window.SetSource("次の文。", "ja"); window.Render(Result()); delayed.Delayed.SetResult(new HashSet<string> { "Hello" });
            Complete(window.VoicePreparation);
            Check(oldButton.Visibility == Visibility.Collapsed && !Elements<Button>(window).Any(), "Late voice discovery restored previous source controls");
        } finally { delayed.Delayed!.TrySetResult(new HashSet<string>()); Close(window); }

        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.AudioDiscovery", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var player = new PronunciationPlayer();
        try {
            byte[] Entry(string word, string path) => Encoding.Unicode.GetBytes(word).Concat(new byte[] { 0, 0, 0 }).Concat(Encoding.ASCII.GetBytes(path)).Append((byte)0).ToArray();
            File.WriteAllBytes(Path.Combine(dir, "word.idx"), Entry("Hello", "hello.mp3").Concat(Entry("Missing", "missing.mp3")).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "hello.mp3"), [1, 2, 3]);
            var task = player.FindWordsAsync(dir, ["HELLO", "missing", "unknown"], CancellationToken.None); Complete(task);
            Check(task.Result.SetEquals(new[] { "HELLO" }), "Real availability lookup enabled absent index entry or missing MP3");
            File.Delete(Path.Combine(dir, "hello.mp3"));
            task = player.FindWordsAsync(dir, ["HELLO"], CancellationToken.None); Complete(task);
            Check(task.Result.Count == 0, "Cached index bypassed current MP3 validation");
        } finally { Complete(player.DisposeAsync().AsTask()); Directory.Delete(dir, true); }
    }
    private sealed class Audio : IPronunciationPlayer
    {
        public int Discoveries, Stops; public bool Disposed, Broken;
        public string[] Words = []; public readonly List<string> Played = [];
        public TaskCompletionSource<IReadOnlySet<string>>? Delayed;
        public event Action<string>? Status;
        public event Action<string>? Error;
        public Task<IReadOnlySet<string>> FindWordsAsync(string directory, IReadOnlyList<string> words, CancellationToken token)
        { Discoveries++; Words = words.ToArray(); if (Broken) throw new InvalidDataException(); return Delayed?.Task ?? Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Hello" }); }
        public Task PlayAsync(string directory, string word) { Played.Add(word); Status?.Invoke("로컬 발음 재생 중"); return Task.CompletedTask; }
        public void Stop() => Stops++;
        public void Emit(string text) => Error?.Invoke(text);
        public void EmitStatus(string text) => Status?.Invoke(text);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
