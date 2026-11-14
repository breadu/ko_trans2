using KoTrans.Core;
using KoTrans.Ocr;
using KoTrans.Infrastructure;
using System.Diagnostics;

internal static class JapaneseAdvNoiseTests
{
    public static void Run()
    {
        int n = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); n++; }
        var options = new OcrOptions("ja", "ADV", false);
        OcrLine first = new("今日は", .99f, new(0, 146, 99, 46));
        OcrLine rest = new("静かな図書館で本を読みます。", .97f, new(109, 145, 743, 47));
        OcrLine badge = new("Badge", .82f, new(129, 47, 78, 88));
        var arranged = LayoutProcessor.Arrange([badge, first, rest], options, 852, 192);
        Check(arranged.Count == 1 && arranged[0].Text == first.Text + rest.Text,
            "Tall detached Latin label bridged into the Japanese dialogue row");
        var speck = new OcrLine("1", .61f, new(458,121,15,17));
        Check(LayoutProcessor.Arrange([first, rest, speck], options, 852, 192).Single().Text == first.Text + rest.Text,
            "Recognizing every detector box introduced a weak numeric speck above the middle of dialogue");
        Check(LayoutProcessor.Arrange([first, rest, speck with {Confidence=.99f}], options, 852, 192)
            .Any(l=>l.Text=="1"), "Confident independent number was removed");
        Check(LayoutProcessor.Arrange([first, rest, speck with {Bounds=new(10,150,15,17)}], options, 852, 192)
            .Any(l=>l.Text.Contains('1')), "Inline number was removed");
        OcrLine body = new("明日は公園を散歩しましょう。", .99f, new(0, 144, 584, 47));
        OcrLine middle = new("ab", .51f, new(201, 63, 80, 74));
        OcrLine upper = new("Q", .91f, new(200, 3, 44, 44));
        arranged = LayoutProcessor.Arrange([upper, middle, body], options, 852, 192);
        Check(arranged.Count == 1 && arranged[0].Text == body.Text,
            "Tall decoration joined a second remote label to the dialogue");
        var inline = badge with { Bounds = new(600, 120, 100, 88) };
        Check(LayoutProcessor.Arrange([body, inline], options, 852, 210)[0].Text == body.Text + inline.Text,
            "Inline Latin text must survive mixed-height recognition");
        var english = new OcrLine("Hello!", .99f, new(0, 200, 180, 47));
        Check(LayoutProcessor.Arrange([body, english], options, 852, 300).Count == 2,
            "Aligned English dialogue row was removed");
        var dots = new OcrLine("……", .99f, new(0, 200, 60, 25));
        Check(LayoutProcessor.Arrange([body, dots], options, 852, 300).Count == 2,
            "Short punctuation row was removed");
        Check(LayoutProcessor.Arrange([badge, first, rest], options with { ReadingMode = "NVL" }, 852, 192).Count == 2,
            "ADV noise isolation changed NVL text");
        Check(LayoutProcessor.Arrange([badge, first, rest], options with { Language = "en" }, 852, 192).Count == 2,
            "Japanese noise isolation changed English layout");
        Check(LayoutProcessor.Arrange([badge, first, rest], options with { SearchArea = new(0, 0, 852, 192) }, 852, 192).Count == 1,
            "Full-coordinate ROI bypassed label isolation");
        var frame = Frame(); var changed = Frame(bodyChanged: true);
        var result = new OcrResult([body], TimeSpan.Zero);
        var labelOnly = new OcrResult([middle], TimeSpan.Zero);
        var tracker = new JapaneseAdvRecovery();
        Check(tracker.Retry(changed, labelOnly) is null, "First frame invented a dialogue anchor");
        tracker.Observe(frame, result);
        var area = tracker.Retry(changed, labelOnly);
        Check(area is { X: 0, Width: 852 } && area.Value.Y < 144 && area.Value.Y + area.Value.Height == 300,
            "Fixed label did not recover the full-width, top-down dialogue region");
        Check(tracker.Retry(frame, labelOnly) is null, "Unchanged dialogue caused a retry");
        Check(tracker.Retry(Frame(true, true), labelOnly) is null, "Moving label was treated as fixed noise");
        Check(tracker.Retry(changed, new([middle with { Bounds = new(200, 145, 80, 47) }], TimeSpan.Zero)) is null,
            "English text inside the dialogue was rejected");
        Check(tracker.Retry(changed, new([body, english], TimeSpan.Zero)) is null,
            "Stable first line plus growing second line was rejected");
        var lowerPixels = new byte[852 * 4 * 300]; lowerPixels[220 * 852 * 4 + 100 * 4] = 80;
        Check(tracker.Retry(new(852, 300, 852 * 4, lowerPixels), labelOnly) == area,
            "An unchanged first line prevented recovery of new rows below it");
        Check(tracker.Retry(changed, new([body with { Text = "明日" }], TimeSpan.Zero)) is null,
            "Short Japanese dialogue was rejected");
        var padded = new byte[(852 * 4 + 8) * 300];
        for (int y = 0; y < 300; y++) for (int x = 0; x < 852; x++) padded[y * (852 * 4 + 8) + x * 4 + 3] = 255;
        padded[160 * (852 * 4 + 8) + 100 * 4] = 80;
        Check(tracker.Retry(new(852, 300, 852 * 4 + 8, padded, FramePixelFormat.Bgra32), labelOnly) == area,
            "Stride/alpha changed static RGB evidence");
        Check(tracker.Retry(new(400, 300, 1600, new byte[480000]), labelOnly) is null
            && tracker.Retry(changed, labelOnly) is null, "Resize retained stale evidence");
        tracker.Observe(frame, result); tracker.Reset();
        Check(tracker.Retry(changed, labelOnly) is null, "Session reset retained an old frame");
        Check(!JapaneseAdvRecovery.Supports(options with { Vertical = true })
            && !JapaneseAdvRecovery.Supports(options with { ReadingMode = "NVL" })
            && !JapaneseAdvRecovery.Supports(options with { Language = "en" }), "Recovery changed unrelated modes");
        Console.WriteLine($"Japanese ADV noise: {n} assertions passed");
    }

    private static ImageFrame Frame(bool bodyChanged = false, bool labelChanged = false)
    {
        var pixels = new byte[852 * 4 * 300];
        if (bodyChanged) pixels[160 * 852 * 4 + 100 * 4] = 80;
        if (labelChanged) pixels[80 * 852 * 4 + 220 * 4] = 80;
        return new(852, 300, 852 * 4, pixels);
    }

    public static async Task Pipeline()
    {
        int n = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); n++; }
        foreach (var outcome in new[] { "body", "empty", "error" }) {
            var fake = new FakeOcr(outcome); var ai = new FakeAi(); var qa = new GameQaRecorder();
            var diagnostics = new DebugTelemetry(); var firstFrame = Frame(); var nextFrame = Frame(true);
            await using var pipeline = new TranslationPipeline(fake, ai, _ => Task.FromResult(firstFrame),
                new("ja", "ADV", false), new(), new(), "synthetic", settle: TimeSpan.Zero,
                gameQa: qa, qaProfile: GameQaProfile.From(new()), diagnostics: diagnostics);
            pipeline.Start(false);
            await pipeline.RequestManualAsync(image: firstFrame);
            await Until(() => Volatile.Read(ref ai.Calls) == 1);
            await pipeline.RequestManualAsync(image: nextFrame);
            await Until(() => fake.Retries == 1 && (outcome == "error" ? qa.Snapshot?.Record.OcrStatus == "failed"
                : qa.Snapshot?.Record.OcrStatus == "completed"
                    && (outcome == "empty" ? qa.Snapshot.Record.PipelineText == "" : Volatile.Read(ref ai.Calls) == 2)));
            Check(fake.Retries == 1, "Stationary external label failed to request focused recovery");
            if (outcome == "body") {
                Check(ai.Last == "明日は公園へ行きます。\n友達と本を読みます。", "Recovery lost the later dialogue row or sent a label to AI");
                Check(qa.Snapshot!.Record.Options.SearchArea is { Width: 852 } && qa.Snapshot.Frame.Width == 852
                    && qa.Snapshot.Record.Input.Crop is null, "QA did not retain the actual full input and focused options");
                Check(diagnostics.Snapshot().Values.LogicalOcrCalls == 3 && diagnostics.Snapshot().Current!.FullRetries == 1,
                    "Recovery call or retry count missing from diagnostics");
            } else if (outcome == "empty") {
                Check(ai.Calls == 1 && qa.Snapshot!.Record.PipelineText == "", "Empty recovery translated noise or replayed old text");
            } else {
                Check(ai.Calls == 1 && qa.Snapshot!.Record.Options.SearchArea is not null
                    && qa.Snapshot.Record.Ocr is null, "Failed recovery sent a known stationary label to AI or recorded success");
            }
        }
        Console.WriteLine($"Japanese ADV recovery pipeline: {n} assertions passed (synthetic/Mock)");
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 5000) throw new Exception("Japanese ADV pipeline timeout"); await Task.Delay(5); }
    }
    private sealed class FakeOcr(string outcome) : IOcrService
    {
        public int Retries;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken cancellation) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken cancellation)
        {
            OcrLine[] lines;
            if (options.SearchArea is not null) {
                Retries++;
                if (outcome == "error") throw new IOException("Synthetic recovery failure");
                lines = outcome == "empty" ? [] : [new("明日は公園へ行きます。", .99f, new(0,144,584,47)),
                    new("友達と本を読みます。", .99f, new(0,200,500,47))];
            } else lines = frame.Pixels.Span[160 * 852 * 4 + 100 * 4] == 0
                ? [new("今日は図書館で勉強します。", .99f, new(0,144,584,47))]
                : [new("ab", .51f, new(201,63,80,74))];
            return Task.FromResult(new OcrResult(lines, TimeSpan.Zero) { Candidates = lines });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeAi : ITranslationService
    {
        public int Calls; public string Last = "";
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellation)
        { Last = request.Text; Interlocked.Increment(ref Calls); return Task.FromResult(new TranslationResult("합성 번역", TimeSpan.Zero, 0)); }
    }
}
