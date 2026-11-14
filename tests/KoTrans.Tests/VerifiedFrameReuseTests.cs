using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class VerifiedFrameReuseTests
{
    private static int assertions;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); assertions++; }
    internal static async Task Run()
    {
        Coverage();
        await RestoreAcceptedSubtitle();
        await KnownFrameDeadline();
        foreach (var mode in new[] { "low-multiline", "jitter", "overlap", "vertical" }) await Cycles(mode);
        await LimitedAdv();
        await ReflowAndPageLifetime();
        await FreshSessions();
        Console.WriteLine($"PASS: {assertions} verified-frame cycle/coverage/source/page/blank/size assertions. Synthetic frames and Mock OCR only.");
    }
    private sealed class RestoreAi : ITranslationService
    {
        public int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new TranslationResult("answer:" + request.Text, TimeSpan.Zero)); }
    }
    private static async Task RestoreAcceptedSubtitle()
    {
        foreach (string mode in new[] { "ADV", "NVL" }) {
            var engine = new Ocr("exact"); var ai = new RestoreAi(); var context = new TranslationSession();
            var frame = Frame(0); var seen = new ConcurrentQueue<PipelineUpdate>();
            await using var pipe = new TranslationPipeline(engine, ai, _ => Task.FromResult(Volatile.Read(ref frame)),
                new("ja", mode, false), new(), context, "", settle: TimeSpan.Zero);
            pipe.Updated += seen.Enqueue; pipe.Start(false);
            await pipe.RequestTriggeredAsync();
            await Until(() => seen.Any(u => u.Translation == "answer:source1"));
            Volatile.Write(ref frame, Frame(0, 2));
            await pipe.RequestTriggeredAsync();
            await Until(() => seen.Any(u => u.Translation == "answer:source2"));
            // Simulate a subscriber that missed/recreated its display after completion.
            // A subsequent same-source update must repair the whole pair without AI.
            seen.Clear(); await pipe.RequestTriggeredAsync();
            await Until(() => seen.Any(u => u.State == PipelineState.Watching && u.Original == "source2"));
            Check(seen.Any(u => u.Original == "source2" && u.Translation == "answer:source2"), mode + ": reuse did not restore the current accepted translation");
            Check(ai.Calls == 2, mode + ": repairing subtitle delivery requested AI again");
        }
    }
    private static void Coverage()
    {
        var before = Frame(0); var area = new PixelRect(40, 120, 260, 70);
        Check(TranslationPipeline.OutsideSearchUnchanged(before, Edit(before, 80, 140), area), "A scanned edit lost outside coverage");
        Check(!TranslationPipeline.OutsideSearchUnchanged(before, Edit(before, 20, 20), area), "Unscanned new text acquired full coverage");
        Check(!TranslationPipeline.OutsideSearchUnchanged(before, Resize(before), area), "Capture size inherited old coverage");
        Check(!TranslationPipeline.OutsideSearchUnchanged(before, before, new(-1, 0, 40, 40)), "Invalid search coverage accepted");
    }
    private static async Task KnownFrameDeadline()
    {
        int captures = 0; var engine = new Ocr("exact");
        await using var pipeline = new TranslationPipeline(engine, null, _ => Task.FromResult(Frame(Interlocked.Increment(ref captures) % 3)),
            new("ja", "ADV", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero, translationEnabled: false);
        pipeline.Start(true); await Until(() => engine.Calls >= 3);
        await Task.Delay(50); int learned = engine.Calls; await Task.Delay(2200);
        Check(engine.Calls == learned, "Periodic Verify re-ran OCR on an exactly verified full frame");
    }
    private static async Task Cycles(string mode)
    {
        int captures = 0, source = 1; bool resized = false, busy = false; var diagnostics = new DebugTelemetry(); var engine = new Ocr(mode); var seen = new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(engine, null, _ => {
            var f = Frame(Interlocked.Increment(ref captures) % 12, Volatile.Read(ref source), mode == "overlap" ? 277 : 300);
            return Task.FromResult(Volatile.Read(ref resized) ? Resize(f) : f);
        }, new("ja", "ADV", mode == "vertical"), new(), new(), "", interval: TimeSpan.FromMilliseconds(5), settle: TimeSpan.Zero, translationEnabled: false, diagnostics: diagnostics) { SuppressRepeatedAnimations = false };
        pipeline.Updated += seen.Enqueue; pipeline.ActivityChanged += (_, value) => Volatile.Write(ref busy, value); pipeline.Start(true);
        await Until(() => seen.Any(u => u.Original?.StartsWith("source1") == true));
        await Until(() => engine.Phases.Count == 12 && !Volatile.Read(ref busy)); int learned = engine.Calls; int target = captures + 48;
        await Until(() => captures >= target);
        Check(engine.Calls == learned, $"{mode}: verified multi-state cycle kept invoking OCR ({learned}->{engine.Calls}; phases={engine.Phases.Count})");
        foreach (int id in new[] { 2, 1, 3, 4 }) {
            int calls = engine.Calls, updates = seen.Count; long hits = diagnostics.Snapshot().Values.OcrResultCacheHits; Volatile.Write(ref source, id);
            await Until(() => seen.Skip(updates).Any(u => u.Original?.StartsWith("source" + id) == true));
            Check(engine.Calls > calls || id == 1 && diagnostics.Snapshot().Values.OcrResultCacheHits > hits, mode + ": changed source was neither recognized nor exactly restored");
        }
        int beforeSize = engine.Calls; Volatile.Write(ref resized, true);
        await Until(() => engine.Calls > beforeSize);
        Check(true, mode + ": resized input was examined");
        Volatile.Write(ref source, 0); await Until(() => engine.Empty > 0);
        Check(engine.Empty > 0, mode + ": blank image was ignored");
        Check(seen.All(u => u.TranslationRequests == 0), mode + ": source-only mode called AI");
    }
    private static async Task ReflowAndPageLifetime()
    {
        int source = 1; var diagnostics = new DebugTelemetry(); var engine = new ReflowOcr(); var seen = new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(engine, null, _ => Task.FromResult(Frame(0, Volatile.Read(ref source))),
            new("ja", "NVL", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero, translationEnabled: false, diagnostics: diagnostics);
        pipeline.Updated += seen.Enqueue; pipeline.Start(true);
        await Until(() => seen.Any(u => u.Original == "あいうえお。"));
        Volatile.Write(ref source, 2);
        await Until(() => seen.Any(u => u.Original?.Contains('\n') == true));
        int calls = engine.Calls, updates = seen.Count; long hits = diagnostics.Snapshot().Values.OcrResultCacheHits; Volatile.Write(ref source, 1);
        await Until(() => seen.Skip(updates).Any(u => u.Original == "あいうえお。"));
        Check(engine.Calls > calls || diagnostics.Snapshot().Values.OcrResultCacheHits > hits, "Reflow skipped recognition and exact result restoration");
        // The Mock's call counter advances before the pipeline accepts the blank
        // result (including any ROI retry). Hold the blank page until that event.
        int beforeBlank=seen.Count;Volatile.Write(ref source, 0);
        await Until(() => seen.Skip(beforeBlank).Any(u => u.Message.StartsWith("문자를 찾지 못했습니다.",StringComparison.Ordinal)));
        calls = engine.Calls; hits = diagnostics.Snapshot().Values.OcrResultCacheHits; updates = seen.Count; Volatile.Write(ref source, 1);
        await Until(() => seen.Skip(updates).Any(u => u.Original == "あいうえお。"));
        Check(engine.Calls > calls || diagnostics.Snapshot().Values.OcrResultCacheHits > hits, "Page boundary did not re-evaluate the current full-frame result");
    }
    private static async Task FreshSessions()
    {
        var engine = new Ocr("exact");
        foreach (string language in new[] { "ja", "en" }) {
            int before = engine.Calls; var seen = new ConcurrentQueue<PipelineUpdate>();
            await using var pipeline = new TranslationPipeline(engine, null, _ => Task.FromResult(Frame(0)),
                new(language, "ADV", false), new(), new(), "", settle: TimeSpan.Zero, translationEnabled: false);
            pipeline.Updated += seen.Enqueue; pipeline.Start(true);
            await Until(() => seen.Any(u => u.Original == "source1"));
            Check(engine.Calls > before, "A new language/profile pipeline inherited the prior session cache");
        }
    }
    private static async Task LimitedAdv()
    {
        int phase = 0; bool outside = false; var engine = new AdvOcr(); var seen = new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(engine, null, _ => {
            var f = Frame(Interlocked.Increment(ref phase) % 3, markerX: 200);
            return Task.FromResult(Volatile.Read(ref outside) ? Edit(f, 40, 30) : f);
        }, new("en", "ADV", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero, translationEnabled: false);
        pipeline.Updated += seen.Enqueue; pipeline.Start(true);
        await Until(() => seen.Any(u => u.Original == "Initial complete dialogue."));
        Volatile.Write(ref outside, true);
        await Until(() => engine.Local > 0);
        await Until(() => seen.Any(u => u.Original?.Contains("Outside new line.") == true));
        Check(engine.FullWithOutside > 0, "Unverified ADV outside pixels were cached and starved full reacquisition");
    }
    private static async Task Until(Func<bool> ready)
    { var w = Stopwatch.StartNew(); while (!ready()) { if (w.ElapsedMilliseconds > 10000) throw new Exception("Verified-frame synthetic condition timed out"); await Task.Delay(2); } }
    private static ImageFrame Frame(int phase, int source = 1, int markerX = 300)
    {
        var p = new byte[400 * 300 * 4]; Paint(p, 1600, 50, 140, 8, 8, (byte)(source * 60));
        Paint(p, 1600, markerX + phase * 6, 140, 5, 5, 255); return new(400, 300, 1600, p);
    }
    private static ImageFrame Edit(ImageFrame f, int x, int y)
    { var p = f.Pixels.ToArray(); Paint(p, f.Stride, x, y, 8, 8, 150); return new(f.Width, f.Height, f.Stride, p); }
    private static ImageFrame Resize(ImageFrame f)
    { var p = new byte[480 * 300 * 4]; for (int y = 0; y < f.Height; y++) f.Pixels.Span.Slice(y * f.Stride, f.Width * 4).CopyTo(p.AsSpan(y * 1920)); return new(480, 300, 1920, p); }
    private static void Paint(byte[] p, int stride, int x, int y, int w, int h, byte v)
    { for (int j = y; j < y + h; j++) for (int i = x; i < x + w; i++) { int q = j * stride + i * 4; p[q] = p[q + 1] = p[q + 2] = v; } }
    private sealed class Ocr(string mode) : IOcrService
    {
        public int Calls, Empty; public readonly ConcurrentDictionary<int, byte> Phases = new(); public OcrCapabilities Capabilities => OcrCapabilities.Basic; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t)
        {
            int call = Interlocked.Increment(ref Calls), id = f.Pixels.Span[140 * f.Stride + 50 * 4] / 60;
            if (id == 0) { Interlocked.Increment(ref Empty); return Task.FromResult(new OcrResult([], TimeSpan.Zero)); }
            int markerX = mode == "overlap" ? 277 : 300;
            for (int x = markerX; x < markerX + 72; x++) if (f.Pixels.Span[140 * f.Stride + x * 4] == 255) { Phases.TryAdd((x - markerX) / 6, 0); break; }
            var box = mode == "vertical" ? new PixelRect(160, 40, 20, 160) : new(40, 130, 240, 20);
            if (mode == "jitter") box = box with { X = box.X + call % 2 };
            var lines = new List<OcrLine> { new("source" + id, mode == "low-multiline" ? .79f : 1, box, mode == "vertical") };
            if (mode == "low-multiline" || id == 3) lines.Add(new("added line", .99f, new(40, 180, 240, 20)));
            return Task.FromResult(new OcrResult(lines, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ReflowOcr : IOcrService
    {
        public int Calls, Empty; public OcrCapabilities Capabilities => new(true, true, false, false); public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t)
        {
            Interlocked.Increment(ref Calls); int id = f.Pixels.Span[140 * f.Stride + 50 * 4] / 60;
            if (id == 0) { Interlocked.Increment(ref Empty); return Task.FromResult(new OcrResult([], TimeSpan.Zero)); }
            OcrLine[] lines = id == 1 ? [new("あいうえお。", 1, new(80,130,200,20))]
                : [new("あいう",1,new(80,130,90,20)),new("えお。",1,new(80,160,100,20))];
            return Task.FromResult(new OcrResult(lines, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class AdvOcr : IOcrService
    {
        public int Local, FullWithOutside; public OcrCapabilities Capabilities => OcrCapabilities.Paddle; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t)
        {
            bool changed = f.Pixels.Span[30 * f.Stride + 40 * 4] == 150;
            if (o.SearchArea is not null) Interlocked.Increment(ref Local);
            if (changed && o.SearchArea is null) Interlocked.Increment(ref FullWithOutside);
            var lines = new List<OcrLine> { new("Initial complete dialogue.", 1, new(80, 130, 170, 20)) };
            if (changed && o.SearchArea is null) lines.Add(new("Outside new line.", 1, new(40, 30, 180, 20)));
            return Task.FromResult(new OcrResult(lines, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
