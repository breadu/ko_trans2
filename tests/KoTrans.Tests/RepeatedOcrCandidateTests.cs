using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class RepeatedOcrCandidateTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); assertions++; }
    internal static async Task Run()
    {
        var history = new RepeatedOcrCandidate();
        long start = Stopwatch.GetTimestamp();
        long At(double seconds) => start + (long)(seconds * Stopwatch.Frequency);
        var settle = TimeSpan.FromMilliseconds(500);
        Check(!history.Observe("A", At(0), settle), "One OCR cannot confirm a candidate");
        Check(!history.Observe("A", At(0), TimeSpan.Zero), "The same captured frame counted twice");
        Check(!history.Observe("A", At(.1), settle), "Repeated text bypassed the confirmation interval");
        Check(!history.Observe("B", At(.3), settle), "Different text confirmed the earlier source");
        Check(history.Observe("A", At(.6), settle), "A/B/A could not confirm the current exact source");
        Check(!history.Observe("C", At(.7), settle), "History substituted an old source for current text");
        Check(!history.Observe("A", At(11), settle), "Expired text evidence confirmed a later scene");
        history.Reset();
        start = Stopwatch.GetTimestamp();
        Check(!history.Observe("A", At(0), settle), "Reset retained prior scene evidence");
        for (int i = 1; i <= 9; i++)
            Check(!history.Observe("A" + new string('x', i), At(i), settle), "Growing typewriter text was confirmed");
        Check(!history.Observe("A", At(9.5), settle), "Old candidates escaped the history bound");
        history.Reset();
        Check(!history.Observe("A", start - 1, settle), "In-flight capture from before reset repopulated evidence");

        await AlternatingText();
        await GrowingText();
        await ChangedDuringOcr();
        Console.WriteLine($"PASS: {assertions} repeated OCR candidate assertions (synthetic capture, Mock OCR/AI).");
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 4000) throw new Exception(message); await Task.Delay(5); }
    }
    // A changing region overlapping the body defeats both pixel quiet and exact
    // row stabilization, as in text drawn over an animated background.
    private static ImageFrame Frame(int number) => new(120, 60, 480,
        Enumerable.Repeat((byte)(20 + number * 37 % 220), 28800).ToArray());
    private sealed class Ocr(Func<int, Task<string>> recognize) : IOcrService
    {
        public int Count;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            string text = await recognize(Interlocked.Increment(ref Count));
            return new([new(text, .9f, new(5, 5, 110, 45))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        public readonly ConcurrentQueue<string> Sources = new();
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Sources.Enqueue(request.Text); return Task.FromResult(new TranslationResult("T:" + request.Text, TimeSpan.Zero, 0)); }
    }
    private static async Task AlternatingText()
    {
        int capture = 0;
        var ai = new Ai(); var telemetry = new DebugTelemetry();
        var ocr = new Ocr(n => Task.FromResult(n % 2 == 1 ? "Current text A" : "Current text B"));
        await using var pipe = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref capture))),
            new("en", "NVL", false), new(), new(), "test", interval: TimeSpan.FromMilliseconds(20), settle: TimeSpan.FromMilliseconds(100), diagnostics: telemetry)
            { SuppressRepeatedAnimations = false };
        pipe.Start(true);
        await Until(() => !ai.Sources.IsEmpty, "Alternating OCR starved translation indefinitely");
        Check(ai.Sources.First() == "Current text A" && ocr.Count >= 3, "Repeat confirmation selected an unconfirmed source");
        Check(telemetry.Snapshot().Events.Any(e => e.Code == DebugCode.OCR_TEXT_ACCEPTED && e.Reason == DebugReason.RepeatedTextConfirmed),
            "Repeat confirmation was not connected to pipeline diagnostics");
    }
    private static async Task GrowingText()
    {
        int capture = 0; bool complete = false;
        var ai = new Ai(); var ocr = new Ocr(n => Task.FromResult(Volatile.Read(ref complete) ? "Completed text" : "Growing " + new string('x', n)));
        await using var pipe = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref capture))),
            new("en", "NVL", false), new(), new(), "test", interval: TimeSpan.FromMilliseconds(20), settle: TimeSpan.FromMilliseconds(100));
        pipe.Start(true);
        await Until(() => ocr.Count >= 5, "Growing source was not sampled");
        Check(ai.Sources.IsEmpty, "Unique typewriter prefixes bypassed the quiet wait");
        Volatile.Write(ref complete, true);
        await Until(() => !ai.Sources.IsEmpty, "A completed sentence could not recover after typewriter changes");
        Check(ai.Sources.All(s => s == "Completed text"), "A previous incomplete source was published");
    }
    private static async Task ChangedDuringOcr()
    {
        int capture = 0; var ai = new Ai();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new Ocr(async n => {
            if (n == 3) { held.TrySetResult(); await release.Task; }
            return n switch { 1 or 3 => "Old candidate A", 2 => "Old candidate B", _ => "Fresh scene" };
        });
        await using var pipe = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref capture))),
            new("en", "NVL", false), new(), new(), "test", interval: TimeSpan.FromMilliseconds(20), settle: TimeSpan.FromMilliseconds(100));
        try {
            pipe.Start(true); await held.Task.WaitAsync(TimeSpan.FromSeconds(4));
            int before = capture;
            await Until(() => capture > before + 2, "Scene did not change while OCR was held");
            release.TrySetResult();
            await Until(() => !ai.Sources.IsEmpty, "Fresh scene was not accepted after stale repeat");
            Check(ai.Sources.All(s => s == "Fresh scene"), "A repeated but superseded capture bypassed verification");
        } finally { release.TrySetResult(); }
    }
}
