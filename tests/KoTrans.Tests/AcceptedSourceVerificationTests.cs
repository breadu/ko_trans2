using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class AcceptedSourceVerificationTests
{
    private const string Old = "「最初の文章です。」", New = "「今の文章を確認してください。」";
    private static int checks;
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    private sealed class Ocr(bool wrongFirst) : IOcrService
    {
        public readonly ConcurrentQueue<OcrOptions> Calls = new();
        public Func<OcrOptions, Task<OcrResult>>? Handler;
        public OcrState State => OcrState.Ready;
        public OcrCapabilities Capabilities => new(true, true, false, false);
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t)
        {
            Calls.Enqueue(o);
            if (Handler is not null) return Handler(o);
            string text = wrongFirst && !o.BypassOcrCaches ? Old : New;
            return Task.FromResult(new OcrResult([new(text, 1, new(50, 80, 220, 20))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public Task<TranslationResult> TranslateAsync(TranslationRequest r, CancellationToken t)
        { Calls.Enqueue(r.Text); return Task.FromResult(new TranslationResult("answer:" + r.Text, TimeSpan.Zero)); }
    }
    private static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Accepted source verification did not recover the unchanged frame"); await Task.Delay(10); }
    }
    internal static async Task Run()
    {
        foreach (bool wrong in new[] { true, false }) {
            var engine = new Ocr(wrong); var ai = new Ai(); var seen = new ConcurrentQueue<PipelineUpdate>(); var debug = new DebugTelemetry();
            // Identical pixels and geometry: neither frame change detection nor
            // the ordinary NVL row stabilizer can repair the first bad reading.
            var frame = new ImageFrame(400, 300, 1600, new byte[480000]);
            await using var pipe = new TranslationPipeline(engine, ai, _ => Task.FromResult(frame),
                new("ja", "NVL", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(20), settle: TimeSpan.Zero, diagnostics: debug);
            pipe.Updated += seen.Enqueue; pipe.Start(true);
            await Until(() => seen.Any(u => u.Translation == "answer:" + (wrong ? Old : New)));
            await Until(() => engine.Calls.Any(o => o.BypassOcrCaches));
            await Until(() => seen.Any(u => u.Translation == "answer:" + New));
            await Task.Delay(2300);
            Check(engine.Calls.Count == 2, "Verification repeated indefinitely on the same frame");
            Check(engine.Calls.Last() is { SearchArea: null, BypassOcrCaches: true }, "Verification reused a partial/cached OCR result");
            Check(ai.Calls.Count == (wrong ? 2 : 1), "Unchanged source called AI again, or corrected source was not translated");
            Check(seen.Last(u => u.Translation is not null).Translation == "answer:" + New, "Old source/answer replaced the recovered subtitle");
            var values = debug.Snapshot().Values;
            Check(values is { SourceVerificationRequests: 1, SourceVerificationCompleted: 1 }
                && values.SourceVerificationMatchesAccepted == !wrong && values.SourceVerificationMismatches == (wrong ? 1 : 0),
                "Independent source comparison diagnostics are incorrect");
            string report = DebugReportWriter.Format(debug.Snapshot(), "test", new string('a', 40));
            Check(!report.Contains(Old) && !report.Contains(New) && !report.Contains("answer:"), "Verification leaked source/result text into diagnostics");
        }
        await StaleVerification();
        await TriggerOnly();
        Console.WriteLine($"PASS: {checks} bounded accepted-source verification assertions; synthetic frames/Mock only.");
    }
    private static async Task StaleVerification()
    {
        var engine = new Ocr(true); var ai = new Ai(); var seen = new ConcurrentQueue<PipelineUpdate>(); var debug = new DebugTelemetry();
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Handler = o => o.BypassOcrCaches ? pending.Task : Task.FromResult(new OcrResult([new(Old, 1, new(50, 80, 220, 20))], TimeSpan.Zero));
        var frame = new ImageFrame(400, 300, 1600, new byte[480000]);
        await using var pipe = new TranslationPipeline(engine, ai, _ => Task.FromResult(frame),
            new("ja", "NVL", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(20), settle: TimeSpan.Zero, diagnostics: debug);
        pipe.Updated += seen.Enqueue; pipe.Start(true);
        try {
            await Until(() => engine.Calls.Any(o => o.BypassOcrCaches));
            pipe.SubmitClipboard(New, manual: true);
            pending.TrySetResult(new([new("STALE_PRIVATE_SOURCE", 1, new(50, 80, 220, 20))], TimeSpan.Zero));
            await Until(() => seen.Any(u => u.Translation == "answer:" + New));
            Check(!ai.Calls.Contains("STALE_PRIVATE_SOURCE"), "Superseded verification started a stale translation");
            Check(debug.Snapshot().Values.SourceVerificationCompleted == 0, "Retired verification contaminated source diagnostics");
        }
        finally { pending.TrySetResult(new([], TimeSpan.Zero)); }
    }
    private static async Task TriggerOnly()
    {
        var engine = new Ocr(false); var ai = new Ai(); var seen = new ConcurrentQueue<PipelineUpdate>();
        var frame = new ImageFrame(400, 300, 1600, new byte[480000]);
        await using var pipe = new TranslationPipeline(engine, ai, _ => Task.FromResult(frame),
            new("ja", "NVL", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(20), settle: TimeSpan.Zero);
        pipe.Updated += seen.Enqueue; pipe.Start(false); await pipe.RequestTriggeredAsync();
        await Until(() => seen.Any(u => u.Translation == "answer:" + New));
        await Task.Delay(2300);
        Check(engine.Calls.Count == 1 && ai.Calls.Count == 1, "Trigger-only mode started unsolicited verification");
    }
}
