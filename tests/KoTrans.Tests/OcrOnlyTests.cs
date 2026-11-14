using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class OcrOnlyTests
{
    internal static async Task Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        foreach (var mode in new[] { "ADV", "NVL" }) {
            var ocr = new Engine(); var forbidden = new ForbiddenAi(); var context = new TranslationSession(); var qa = new GameQaRecorder();
            var frame = new ImageFrame(120, 60, 480, new byte[28800]);
            await using var pipeline = new TranslationPipeline(ocr, forbidden, _ => Task.FromResult(frame), new("ja", mode, false), new(), context, "",
                settle: TimeSpan.Zero, gameQa: qa, qaProfile: GameQaProfile.From(new()), translationEnabled: false);
            var updates = new System.Collections.Concurrent.ConcurrentQueue<PipelineUpdate>();
            pipeline.Updated += updates.Enqueue; pipeline.Start(false);
            await pipeline.RequestManualAsync(); await Until(() => updates.Any(u => u.State == PipelineState.Watching && u.Original == "最初の文。"));
            var result = updates.Last(u => u.Original == "最初の文。");
            Check(result.Translation == "" && result.TranslationRequests == 0 && result.HttpAttempts == 0 && forbidden.Calls == 0, "OCR-only sent a translation request");
            Check(context.History.Count == 0 && qa.Snapshot?.Record.Translation.Status == "not_requested", "OCR-only contaminated translation context/QA");
            int calls = ocr.Calls; await pipeline.RequestTriggeredAsync();
            await Until(() => updates.Count(u => u.State == PipelineState.Watching && u.Original == "最初の文。") >= 2);
            Check(ocr.Calls == calls && forbidden.Calls == 0, "Stable OCR-only frame was not reused");
            pipeline.SubmitClipboard("追加された文。", manual: true);
            await Until(() => updates.Any(u => u.State == PipelineState.Watching && u.Original == "追加された文。"));
            Check(ocr.Calls == calls && forbidden.Calls == 0 && context.History.Count == 0, "Source-only clipboard used OCR/AI/context");
            pipeline.SubmitClipboard("追加された文。", manual: true, force: true);
            await Until(() => updates.Count(u => u.State == PipelineState.Watching && u.Original == "追加された文。") == 2);
            Check(forbidden.Calls == 0, "Explicit source-only retry reached AI");
            await pipeline.DisposeAsync(); Check(!ocr.Disposed && !pipeline.IsRunning, "Pipeline disposed shared OCR or failed to stop");
        }
        var nullAi = new TranslationPipeline(new Engine(), null, _ => throw new Exception("Capture must not run"), new("en", "ADV", false), new(), new(), "", translationEnabled: false);
        nullAi.Start(false); var done = new TaskCompletionSource<PipelineUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        nullAi.Updated += u => { if (u.Original == "No API key or model." && u.State == PipelineState.Watching) done.TrySetResult(u); };
        nullAi.SubmitClipboard("No API key or model.", manual: true);
        Check((await done.Task.WaitAsync(TimeSpan.FromSeconds(3))).TranslationRequests == 0, "No-provider OCR-only failed");
        await nullAi.DisposeAsync();
        Console.WriteLine($"PASS: {count} OCR-only assertions. No AI, key, model loading or desktop.");
    }
    private static async Task Until(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!ready()) await Task.Delay(5, timeout.Token); }
    private sealed class ForbiddenAi : ITranslationService
    { public int Calls; public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) { Calls++; throw new Exception("AI must not be called"); } }
    private sealed class Engine : IOcrService
    {
        public int Calls; public bool Disposed; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new OcrResult([new("最初の文。", 1, new(10, 10, 80, 20))], TimeSpan.Zero)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
