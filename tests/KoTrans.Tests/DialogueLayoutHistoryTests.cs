using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class DialogueLayoutHistoryTests
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private static ImageFrame Frame(byte value = 0, int width = 600) {
        var bytes = new byte[width * 160 * 4]; bytes[0] = value;
        return new(width, 160, width * 4, bytes);
    }
    private static OcrResult Seed() => new([new("合成の一行目です。", .99f, new(160, 25, 350, 28)),
        new("合成の二行目です。", .99f, new(160, 70, 160, 28))], TimeSpan.Zero);
    internal static void Run()
    {
        long now = 0; var history = new DialogueLayoutHistory(() => now); var frame = Frame();
        var options = new OcrOptions("ja", "ADV", false, ModelPresetId: "v6-medium-full");
        Check(history.Get(frame, options) is null, "History exists before clean recognition");
        history.Observe(frame, Seed(), options, true); var hint = history.Get(frame, options)!;
        Check(hint is not null && hint.Rows.Count == 2, "Clean two-row geometry was not retained");
        PixelRect[] detected = [new(110, 25, 400, 28), new(160, 70, 160, 28)];
        Check(DialogueRegionSelection.Select(detected, b => b).Count == 1, "Fixture no longer requires supplementary evidence");
        Check(DialogueRegionSelection.Select(detected, b => b, hint!.Supports).Count == 2, "Current detected second row did not use history");
        Check(DialogueRegionSelection.Select(detected.Take(1).ToArray(), b => b, hint.Supports).Count == 1, "History invented a missing detection");
        Check(!hint.Supports(new(230, 110, 60, 28), detected[0]), "Off-baseline background label used history");
        Check(!hint.Supports(detected[1], detected[0] with { Y = 100 }), "Moved current anchor accepted old layout");
        Check(!hint.Supports(detected[1] with { Height = 12 }, detected[0]), "Small label used body geometry");
        var supplementary = new OcrLine("現在の候補です。", .99f, detected[1]);
        foreach (var confidence in new[] { .6f, .99f }) {
            var lines = new List<OcrLine> { new("現在の本文です。", confidence, detected[0]), supplementary };
            DialogueRegionSelection.ConfirmHistoryRows(lines, new HashSet<OcrLine> { supplementary }, detected[0]);
            Check(lines.Contains(supplementary) == (confidence >= .85f), "History ignored current anchor confidence");
        }
        var orphan = new List<OcrLine> { supplementary };
        DialogueRegionSelection.ConfirmHistoryRows(orphan, new HashSet<OcrLine> { supplementary }, detected[0]);
        Check(orphan.Count == 0, "History supplied text without a current anchor");
        foreach (string preset in new[] { "default", "v6-medium-full", "v5-det-v6-medium-rec" }) {
            var shared = new DialogueLayoutHistory(() => now);
            var modelOptions = options with { ModelPresetId = preset };
            shared.Observe(frame, Seed(), modelOptions, true);
            Check(shared.Get(frame, modelOptions)?.Supports(detected[1], detected[0]) == true,
                "Shared layout supplement missing for " + preset);
        }
        Check(history.Get(frame, options, true) is null && history.Get(frame, options with { BypassOcrCaches = true }) is null,
            "Explicit fresh read depended on prior geometry");
        Check(history.Get(frame, options with { SearchArea = new(0, 0, 300, 140) }) is null, "ROI received full-frame coordinates");
        var roundTrip = JsonSerializer.Deserialize<OcrOptions>(JsonSerializer.Serialize(options with { DialogueHint = hint }))!;
        Check(roundTrip.DialogueHint!.Supports(detected[1], detected[0]), "QA/worker serialization lost hint geometry");
        DirectMlProtocol.ValidateInput(frame, roundTrip with { UseDirectMl = true });
        bool rejected = false;
        try { DirectMlProtocol.ValidateInput(frame, options with { DialogueHint = new(600, 160, [new(-1, 25, 350, 28), new(160, 70, 160, 28)]) }); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Malformed GPU history accepted");
        Check(JsonSerializer.Deserialize<OcrOptions>("{\"Language\":\"ja\",\"ReadingMode\":\"ADV\",\"Vertical\":false}")!.DialogueHint is null,
            "Old QA data acquired fabricated geometry");
        now = 14000; history.Observe(frame, Seed() with { DialogueHintUsed = true }, options, true); now = 15001;
        Check(history.Get(frame, options) is null, "Assisted result renewed its own evidence");
        history.Observe(frame, Seed(), options, true); now += 14000;
        history.Observe(frame, Seed() with { ReusedFromCache = true }, options, true); now += 1001;
        Check(history.Get(frame, options) is null, "Cached result extended geometry lifetime");
        history.Observe(frame, Seed(), options, true);
        Check(history.Get(Frame(width: 601), options) is null, "Capture resize retained history");
        history.Observe(frame, Seed(), options, true);
        Check(history.Get(frame, options with { ModelPresetId = "default" }) is null, "Changed OCR model retained history");
        history.Observe(frame, Seed(), options, true);
        Check(history.Get(frame, options with { ReadingMode = "NVL" }) is null, "Moving NVL layout inherited ADV history");
        history.Observe(frame, Seed(), options, true);
        history.Observe(frame, new([new("別の配置です。", .99f, new(10, 110, 350, 28))], TimeSpan.Zero), options, true);
        Check(history.Get(frame, options) is null, "Changed dialogue location retained old rows");
        history.Observe(frame, Seed(), options, true); history.Observe(frame, new([], TimeSpan.Zero), options, true);
        Check(history.Get(frame, options) is null, "Blank scene retained old layout");
        history.Observe(frame, Seed() with { Lines = Seed().Lines.Select(l => l with { Confidence = .6f }).ToArray() }, options, true);
        Check(history.Get(frame, options) is null, "Weak text seeded layout");
        history.Observe(frame, Seed(), options, false);
        Check(history.Get(frame, options) is null, "Partial search seeded layout");
        history.Observe(frame, Seed(), options, true); history.Reset();
        Check(history.Get(frame, options) is null && new DialogueLayoutHistory().Get(frame, options) is null, "Session/profile reset retained layout");
        Console.WriteLine($"PASS: {checks} bounded dialogue history assertions; geometry/synthetic input only.");
    }

    internal static async Task Pipeline()
    {
        var engine = new Engine(); var frame = Frame(); var qa = new GameQaRecorder();
        await using var pipeline = new TranslationPipeline(engine, null, _ => Task.FromResult(frame),
            new("ja", "ADV", false, ModelPresetId: "v6-medium-full"), new(), new(), "",
            interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero, gameQa: qa,
            qaProfile: GameQaProfile.From(new()), translationEnabled: false);
        pipeline.Start(false); await pipeline.RequestManualAsync();
        await Until(() => qa.Snapshot?.Record.OcrStatus == "completed");
        Check(engine.Seen.First().DialogueHint is null, "First request used history");
        frame = Frame(1); await pipeline.RequestTriggeredAsync();
        await Until(() => qa.Snapshot?.Frame.Pixels.Span[0] == 1 && qa.Snapshot.Record.OcrStatus == "completed");
        Check(engine.Seen.Last().DialogueHint is not null && qa.Snapshot!.Record.Options.DialogueHint is not null,
            "Automatic request or exact QA record omitted geometry");
        Check(qa.Snapshot!.Record.Ocr!.DialogueHintUsed && qa.Snapshot!.Record.Ocr!.Lines.Count == 2, "Supplement did not retain current second line");
        await pipeline.RequestManualAsync(force: true);
        await Until(() => engine.Seen.Count >= 3 && qa.Snapshot?.Record.OcrStatus == "completed");
        Check(engine.Seen.Last().DialogueHint is null && qa.Snapshot!.Record.Ocr!.Lines.Count == 1, "Manual fresh check reused previous positions");
        Console.WriteLine("PASS: dialogue history automatic/manual/QA pipeline; no AI or desktop.");
    }
    private static async Task Until(Func<bool> ready) { using var timeout = new CancellationTokenSource(4000); while (!ready()) await Task.Delay(5, timeout.Token); }
    private sealed class Engine : IOcrService
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<OcrOptions> Seen = new();
        public OcrState State => OcrState.Ready;
        public OcrCapabilities Capabilities => OcrCapabilities.Basic;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) {
            Seen.Enqueue(options);
            if (frame.Pixels.Span[0] == 0) return Task.FromResult(Seed());
            PixelRect[] boxes = [new(110, 25, 400, 28), new(160, 70, 160, 28)];
            var rows = DialogueRegionSelection.Select(boxes, b => b, options.DialogueHint is { } h ? h.Supports : null);
            return Task.FromResult(new OcrResult(rows.Select((b, i) => new OcrLine("新しい合成文" + i, .99f, b)).ToArray(), TimeSpan.Zero)
                { DialogueHintUsed = rows.Count == 2 });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
