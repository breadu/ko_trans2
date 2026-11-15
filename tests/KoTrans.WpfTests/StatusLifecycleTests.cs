using System.IO;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class StatusLifecycleTests
{
    internal static void EmptyOcrRecovery()
    {
        string dir = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(settings);
        var model = (MainViewModel)main.DataContext; var ocr = new Ocr { Fail = true }; var ai = new Ai();
        var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(1)), new("en", "ADV", false), new(), new(), "", settle: TimeSpan.Zero);
        bool busy = false; pipeline.ActivityChanged += (_, value) => Volatile.Write(ref busy, value);
        try {
            main.ObserveTranslation(pipeline, settings.ActiveProfileId); pipeline.Start(false);
            Complete(pipeline.RequestManualAsync(image: Frame(1)));
            Until(() => model.Status == "synthetic OCR failure", "Initial OCR error did not appear");
            ocr.Fail = false; ocr.Empty = true;
            Complete(pipeline.RequestManualAsync(image: Frame(2)));
            Until(() => ocr.Calls == 2 && !Volatile.Read(ref busy), "Empty OCR retry did not finish"); Pump();
            Console.WriteLine("Empty OCR retry status: " + model.Status);
            Check(model.Status.Contains("대사를 찾지 못했습니다") && !model.Status.Contains("synthetic OCR failure") && ai.Pending.Count == 0,
                "Successful empty OCR retained a resolved OCR error or called AI");
            // Empty OCR is not evidence that a previous translation error recovered.
            pipeline.SubmitClipboard("failed"); Until(() => ai.Pending.ContainsKey("failed"), "AI request missing");
            ai.Pending["failed"].SetException(new TranslationException("synthetic AI failure"));
            Until(() => model.Status == "synthetic AI failure", "AI error missing");
            Complete(pipeline.RequestManualAsync(image: Frame(3)));
            Until(() => ocr.Calls == 3 && !Volatile.Read(ref busy), "Second empty OCR did not finish"); Pump();
            Check(model.Status == "synthetic AI failure", "Empty OCR falsely recovered an AI failure");
        } finally { Complete(pipeline.DisposeAsync().AsTask()); Close(main); Directory.Delete(dir, true); }
    }
    internal static void Messages()
    {
        string dir = FeedbackTests.DirectoryPath(); var now = TimeSpan.Zero;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [], feedbackClock: () => now));
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        var model = (MainViewModel)main.DataContext;
        try {
            foreach (bool translated in new[] { true, false }) {
                profile.ShowTranslation = translated; profile.CaptureMode = CaptureMode.Window;
                profile.GameWindow = new("synthetic", "C:\\synthetic\\game.exe", "Synthetic", "Game");
                main.ApplyLoadedSettings(settings);
                string action = translated ? "번역 시작" : "원문 인식 시작";
                Check(model.Status == $"게임을 실행한 뒤 {action}을 누르세요.", "Window ready guidance does not match display mode");
                profile.CaptureMode = CaptureMode.Region; profile.CaptureAreaConfigured = true; main.ApplyLoadedSettings(settings);
                string ready = $"준비 완료 · {action}을 누르세요.";
                Check(model.Status == ready, "Region ready guidance does not match display mode");
                main.ShowCompletionStatus("완료"); now += TimeSpan.FromSeconds(3); main.AdvanceFeedback();
                Check(model.Status == ready && !main.IsFeedbackTimerRunning, "Completion did not return to current ready state");
                main.ShowCompletionStatus("완료"); model.Status = "새 오류";
                now += TimeSpan.FromSeconds(3); main.AdvanceFeedback();
                Check(model.Status == "새 오류", "Old completion timer erased new error");
                main.ShowCompletionStatus("완료"); model.Status = "진행"; model.Status = "완료";
                now += TimeSpan.FromSeconds(3); main.AdvanceFeedback();
                Check(model.Status == "완료", "Text equality confused separate status revisions");
                var choices = Named<ComboBox>(main, "OcrEngineChoices");
                choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().First(c => !ReferenceEquals(c, choices.SelectedItem));
                Check(model.Status == (translated ? "OCR 모델 변경 · 설정 저장 후 번역을 시작하세요." : "OCR 모델 변경 · 설정 저장 후 원문 인식을 시작하세요.")
                    && main.FeedbackToast.Detail.Contains("저장 필요"), "Model change lost mode or unsaved notice");
                Check(MainWindow.StartFailureMessage(new Exception("private internal detail"), translated) == (translated
                    ? "번역 준비 중 오류가 발생했습니다. 다시 시작해 주세요." : "원문 인식 준비 중 오류가 발생했습니다. 다시 시작해 주세요."), "Generic startup failure has wrong mode");
                Check(MainWindow.StartFailureMessage(new CaptureUnavailableException("대상 창 확인 필요"), translated) == "대상 창 확인 필요", "Specific startup cause was hidden");
            }
            main.ShowCompletionStatus("이전 프로필 완료");
            var game = settings.CreateGameProfile("Next"); Complete(main.SwitchProfileAsync(game));
            string switched = model.Status; now += TimeSpan.FromSeconds(3); main.AdvanceFeedback();
            Check(model.Status == switched && !model.Status.Contains("이전 프로필"), "Old profile completion resurfaced");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void Run()
    {
        string dir = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(settings);
        var model = (MainViewModel)main.DataContext; var ocr = new Ocr(); var ai = new Ai();
        var displayed = new List<string>(); model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Status)) displayed.Add(model.Status); };
        var context = new TranslationSession(); context.Accept("cached", "cached translation", false);
        var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(1)), new("en", "ADV", false), new(), context, "", settle: TimeSpan.Zero);
        TranslationPipeline? replacement = null;
        try {
            main.ObserveTranslation(pipeline, settings.ActiveProfileId); pipeline.Start(false);
            pipeline.SubmitClipboard("failed"); Until(() => ai.Pending.ContainsKey("failed"), "AI was not called");
            ai.Pending["failed"].SetException(new TranslationException("synthetic AI failure"));
            Until(() => model.Status == "synthetic AI failure", "AI failure not visible");
            ocr.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Complete(pipeline.RequestManualAsync(image: Frame(2))); Until(() => ocr.Calls == 1, "New OCR did not start");
            Check(model.Status == "synthetic AI failure", "New OCR cleared unresolved AI failure");
            ocr.Gate.SetResult(); Until(() => ai.Pending.ContainsKey("source2"), "Recognized source not translated"); Pump();
            Check(model.Status == "synthetic AI failure", "Valid original cleared AI failure");
            ai.Progress["source2"]("partial"); Pump();
            Check(model.Status == "synthetic AI failure" && Named<TextBox>(main, "TranslatedText").Text == "partial", "Error protection blocked subtitle or accepted partial recovery");
            ai.Pending["source2"].SetResult(new("success", TimeSpan.Zero));
            Until(() => model.Status.Contains("번역 완료"), "Valid final response did not recover AI failure");

            pipeline.SubmitClipboard("failed-again"); Until(() => ai.Pending.ContainsKey("failed-again"), "Second AI missing");
            ai.Pending["failed-again"].SetException(new TranslationException("second AI failure"));
            Until(() => model.Status == "second AI failure", "Second failure missing");
            // Cache stores only the last accepted translation; seed it without a provider request.
            context.Accept("cached", "cached translation", false);
            pipeline.SubmitClipboard("cached"); Until(() => Named<TextBox>(main, "TranslatedText").Text == "cached translation", "Cached subtitle missing");
            Check(model.Status == "second AI failure" && !ai.Pending.ContainsKey("cached"), "Cache display falsely recovered provider");

            replacement = new(ocr, ai, async token => { await Task.Delay(Timeout.Infinite, token); return Frame(3); }, new("en", "ADV", false), new(), new(), "", settle: TimeSpan.Zero);
            pipeline.SubmitClipboard(""); // Its queued event must not enter the new session.
            main.ObserveTranslation(replacement, settings.ActiveProfileId); replacement.Start(false); Pump();
            Check(model.Status == "입력 대기 중", "Restart retained old errors or stale warning");
            replacement.SubmitClipboard(""); Pump(); Check(model.Status.Contains("게임 대사를 복사"), "Important warning not shown");
            Complete(replacement.RequestTriggeredAsync()); Pump();
            Check(model.Status.Contains("게임 대사를 복사"), "Ordinary progress hid important warning");
            replacement.SubmitClipboard("valid"); Until(() => ai.Pending.ContainsKey("valid"), "Valid input not accepted");
            Until(() => !model.Status.Contains("게임 대사를 복사"), "Valid input did not clear warning");
            ai.Pending["valid"].SetResult(new("valid result", TimeSpan.Zero)); Until(() => model.Status.Contains("번역 완료"), "Final result missing");
            ocr.Gate = null; ocr.Fail = true;
            Complete(replacement.RequestManualAsync(image: Frame(4))); Until(() => model.Status.Contains("synthetic OCR failure"), "OCR failure missing");
            ocr.Fail = false;
            Complete(replacement.RequestManualAsync(image: Frame(5))); Until(() => ai.Pending.ContainsKey("source5"), "OCR retry missing"); Pump();
            Check(!model.Status.Contains("synthetic OCR failure"), "Valid OCR did not clear OCR failure");
            ai.Pending["source5"].SetResult(new("retry result", TimeSpan.Zero));
            Until(() => model.Status.Contains("번역 완료"), "OCR retry translation missing");
            replacement.Stop(); Pump(); Check(model.Status == "번역을 중지했습니다.", "Explicit stop retained error");
            Check(!displayed.Contains("인식 완료 · 문장 확인 중"), "Internal candidate state leaked to the user status bar");
        } finally {
            ocr.Gate?.TrySetResult(); foreach (var pending in ai.Pending.Values) pending.TrySetResult(new("cleanup", TimeSpan.Zero));
            Complete(pipeline.DisposeAsync().AsTask()); if (replacement is not null) Complete(replacement.DisposeAsync().AsTask());
            Close(main); Directory.Delete(dir, true);
        }
    }
    private static ImageFrame Frame(byte value) => new(40, 20, 160, Enumerable.Repeat(value, 3200).ToArray());
    private sealed class Ocr : IOcrService
    {
        internal int Calls; internal bool Fail, Empty; internal TaskCompletionSource? Gate;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) {
            Calls++; if (Gate is not null) await Gate.Task.WaitAsync(token);
            if (Fail) throw new OcrInitializationException("synthetic OCR failure");
            if (Empty) return new([], TimeSpan.Zero);
            return new([new("source" + frame.Pixels.Span[0], 1, new(0, 0, 40, 20))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<TranslationResult>> Pending = new();
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<string>> Progress = new();
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) {
            var pending = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (request.Progress is not null) Progress[request.Text] = request.Progress;
            Pending[request.Text] = pending; return pending.Task;
        }
    }
}
