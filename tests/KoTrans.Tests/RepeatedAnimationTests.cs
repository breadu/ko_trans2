using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class RepeatedAnimationTests
{
    private static int assertions;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
    internal static async Task Run()
    {
        GuardBoundaries();
        foreach (int delay in new[] { 15, 150 }) {
            var before = await Measure(false, delay); var after = await Measure(true, delay);
            Check(after.Ocr < before.Ocr * .75, "Animation suppression did not materially reduce repeated OCR");
            Check(before.Translations == 2 && after.Translations == 2, "Decoration duplicated translation or hid new text");
            Console.WriteLine($"Animation A/B: simulatedOCR={delay}ms, OCR {before.Ocr}->{after.Ocr}; changed-source latency {before.NewTextMs}->{after.NewTextMs}ms; AI {before.Translations}->{after.Translations}. Synthetic timings, not hardware benchmarks.");
        }
        await HiddenCharacterIsRechecked();
        Console.WriteLine($"PASS: {assertions} generic repeated-animation safety/performance assertions. Synthetic frames and Mock OCR/AI only.");
    }

    private static void GuardBoundaries()
    {
        foreach (var (x, y, vertical) in new[] { (300, 140, false), (360, 275, false), (10, 40, false), (190, 220, true) }) {
            var time = new Clock(); var guard = new RepeatedAnimationGuard(time); var result = Result(1, vertical);
            guard.Observe(Frame(0, markerX: x, markerY: y), result, "source1");
            Check(guard.Evaluate(Frame(1, markerX: x, markerY: y)) == AnimationFrameDecision.Unknown, "Unconfirmed pixels were ignored");
            guard.Observe(Frame(1, markerX: x, markerY: y), result, "source1");
            Check(guard.Evaluate(Frame(2, markerX: x, markerY: y)) == AnimationFrameDecision.Unknown, "One repeat was trusted too early");
            guard.Observe(Frame(2, markerX: x, markerY: y), result, "source1");
            Check(guard.Evaluate(Frame(3, markerX: x, markerY: y)) == AnimationFrameDecision.Reuse, "Generic marker was not learned");
            var edited = Edit(Frame(3, markerX: x, markerY: y), vertical ? 165 : 50, 140);
            Check(guard.Evaluate(edited) == AnimationFrameDecision.Unknown, "A changed letter was ignored");
            Check(guard.Evaluate(Edit(Frame(3, markerX: x, markerY: y), 80, 220)) == AnimationFrameDecision.Unknown, "A new line/page was ignored");
            time.Milliseconds = 2001;
            Check(guard.Evaluate(Frame(3, markerX: x, markerY: y)) == AnimationFrameDecision.Verify, "Periodic full verification missing");
            guard.Observe(Frame(3, markerX: x, markerY: y), Result(2, vertical), "source2");
            Check(guard.Evaluate(Frame(4, markerX: x, markerY: y)) == AnimationFrameDecision.Unknown, "New text inherited the previous marker mask");
        }
        var g = Learn();
        Check(g.Evaluate(Edit(Frame(3), 280, 140)) == AnimationFrameDecision.Unknown, "End punctuation was treated as a decoration");
        Check(g.Evaluate(Edit(Frame(3), 100, 30)) == AnimationFrameDecision.Unknown, "A second separated region was ignored");
        g.Observe(Frame(3), new([], TimeSpan.Zero), "");
        Check(g.Evaluate(Frame(4)) == AnimationFrameDecision.Unknown, "Empty OCR retained a learned mask");
        g = Learn(); g.Observe(Frame(3), new([new("source1", .5f, new(40,130,240,20))], TimeSpan.Zero), "source1");
        Check(g.Evaluate(Frame(4)) == AnimationFrameDecision.Unknown, "Low-confidence OCR enabled suppression");
        g = Learn(); Check(g.Evaluate(new(100, 100, 400, new byte[40000])) == AnimationFrameDecision.Unknown, "Resize retained the old mask");
        g = Learn(); g.Reset(); Check(g.Evaluate(Frame(4)) == AnimationFrameDecision.Unknown, "Reset retained learning");
        g = Learn(); g.Observe(Frame(3), new([new("source1", 1, new(40,110,240,20))], TimeSpan.Zero), "source1");
        Check(g.Evaluate(Frame(4)) == AnimationFrameDecision.Unknown, "Reflow retained the old text geometry");
    }
    private static RepeatedAnimationGuard Learn()
    { var guard = new RepeatedAnimationGuard(); for (int i = 0; i < 3; i++) guard.Observe(Frame(i), Result(1), "source1"); return guard; }

    private sealed record Measurement(int Ocr, int Translations, long NewTextMs);
    private static async Task<Measurement> Measure(bool enabled, int delay)
    {
        int captures = 0, source = 1; var engine = new Ocr(delay); var ai = new Ai(); var seen = new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(engine, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref captures) % 12, Volatile.Read(ref source))),
            new("en", "ADV", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(30), settle: TimeSpan.Zero) { SuppressRepeatedAnimations = enabled };
        pipeline.Updated += seen.Enqueue; pipeline.Start(true);
        await Until(() => seen.Any(u => u.Translation == "translated source1"), "Animated startup lost the first source");
        int target = captures + 65;
        await Until(() => Volatile.Read(ref captures) >= target, "Animation comparison did not progress");
        int repeats = engine.Calls; var elapsed = Stopwatch.StartNew(); Volatile.Write(ref source, 2);
        await Until(() => seen.Any(u => u.Translation == "translated source2"), "Decoration mask swallowed a changed source");
        long latency = elapsed.ElapsedMilliseconds;
        Check(seen.Where(u => u.State == PipelineState.Watching && u.Translation is not null).All(u => u.Translation is "translated source1" or "translated source2"), "Unknown subtitle appeared");
        int normalTranslations = ai.Calls;
        int oldCalls = engine.Calls; await pipeline.RequestManualAsync(force: true, image: Frame(0, 2));
        await Until(() => engine.Calls > oldCalls, "Explicit manual OCR was suppressed");
        // Finish the explicit retry before counting normal translations.
        await Until(() => seen.Count(u => u.Translation == "translated source2") >= 2, "Manual OCR did not complete");
        pipeline.Stop();
        return new(repeats, normalTranslations, latency);
    }

    private static async Task HiddenCharacterIsRechecked()
    {
        bool hidden = false; int tick = 0; var engine = new Ocr(10); var seen = new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(engine, null, _ => {
            var frame = Frame(Interlocked.Increment(ref tick) % 12);
            return Task.FromResult(Volatile.Read(ref hidden) ? Edit(frame, 310, 140) : frame);
        }, new("en", "ADV", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(30), settle: TimeSpan.Zero, translationEnabled: false);
        pipeline.Updated += seen.Enqueue; pipeline.Start(true);
        await Until(() => engine.Calls >= 5, "Animation learning did not run");
        Volatile.Write(ref hidden, true);
        await Until(() => seen.Any(u => u.Original == "source1 + new character"), "Character inside the ambiguous marker area was never rechecked");
        Check(seen.All(u => u.TranslationRequests == 0), "OCR-only animation verification called AI");
    }

    private static async Task Until(Func<bool> ready, string message)
    { var timer = Stopwatch.StartNew(); while (!ready()) { if (timer.ElapsedMilliseconds > 6000) throw new Exception(message); await Task.Delay(5); } }
    private static OcrResult Result(int source, bool vertical = false) => new([new("source" + source, 1, vertical ? new(160,40,20,160) : new(40,130,240,20), vertical)], TimeSpan.Zero);
    private static ImageFrame Frame(int phase, int source = 1, int markerX = 300, int markerY = 140)
    {
        var pixels = new byte[400 * 300 * 4];
        Paint(pixels, 50, 140, 4, 4, (byte)(source * 60));
        Paint(pixels, markerX + phase * 2, markerY, 5, 5, 255);
        return new(400,300,1600,pixels);
    }
    private static ImageFrame Edit(ImageFrame frame, int x, int y)
    { var pixels = frame.Pixels.ToArray(); Paint(pixels, x,y,4,4,150); return new(frame.Width,frame.Height,frame.Stride,pixels); }
    private static void Paint(byte[] pixels, int x, int y, int width, int height, byte value)
    { for (int row=y;row<y+height;row++) for(int col=x;col<x+width;col++) { int p=row*1600+col*4; pixels[p]=pixels[p+1]=pixels[p+2]=value; } }
    private sealed class Clock : TimeProvider { public long Milliseconds; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Milliseconds; }
    private sealed class Ocr(int delay) : IOcrService
    {
        public int Calls; public OcrCapabilities Capabilities => OcrCapabilities.Basic; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) {
            Interlocked.Increment(ref Calls); await Task.Delay(delay,token);
            int source = frame.Pixels.Span[140*1600+50*4] / 60;
            bool extra = frame.Pixels.Span[140*1600+310*4] == 150;
            return extra ? new([new("source1 + new character",1,new(40,130,280,20))],TimeSpan.Zero) : Result(source);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        public int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new TranslationResult("translated " + request.Text,TimeSpan.Zero,0)); }
    }
}
