using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class MuramasaTests
{
    private static int count;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    private static async Task Until(Func<bool> condition, string message) {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 3500) throw new Exception(message); await Task.Delay(5); }
    }
    private static ImageFrame Frame(int tick, byte text = 1, bool inside = false) {
        var data = new byte[400 * 300 * 4];
        // Source changes are visible glyph-sized edits, not a sub-threshold blue metadata byte.
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) {
            int p = y * 1600 + x * 4;
            data[p] = text; data[p + 1] = data[p + 2] = (byte)(text * 50);
        }
        for (int y = inside ? 200 : 20; y < (inside ? 205 : 40); y++)
            for (int x = 100; x < 180; x++) for (int c = 0; c < 3; c++) data[y * 1600 + x * 4 + c] = (byte)(tick % 2 == 0 ? 80 : 180);
        return new(400, 300, 1600, data);
    }
    private sealed class Ocr : IOcrService {
        public int Calls; public readonly ConcurrentQueue<byte> Seen = new();
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t) {
            Interlocked.Increment(ref Calls); Seen.Enqueue(f.Pixels.Span[0]);
            return Task.FromResult(new OcrResult(f.Pixels.Span[0] == 0 ? [] : [new("line" + f.Pixels.Span[0], 1, new(0, 0, 400, 300))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService {
        public readonly ConcurrentQueue<string> Calls = new();
        public int Canceled; public int Delay = 45; public bool Fail;
        public async Task<TranslationResult> TranslateAsync(TranslationRequest r, CancellationToken t) {
            Calls.Enqueue(r.Text);
            try { await Task.Delay(Delay, t); } catch (OperationCanceledException) { Interlocked.Increment(ref Canceled); throw; }
            if (Fail) throw new TranslationException("Synthetic provider failure");
            return new("T:" + r.Text, TimeSpan.Zero, 0);
        }
    }
    internal static async Task Run() {
        var clock = Stopwatch.StartNew();
        var ocr = new Ocr(); var ai = new Ai(); int tick = 0;
        var updates = new ConcurrentQueue<PipelineUpdate>();
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref tick))), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(35))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            await Until(() => updates.Any(u => u.Translation == "T:line1"), "Animated startup starved OCR/translation");
            Check(ocr.Calls > 0, "Fallback did not perform OCR");
            Check(ai.Calls.Count == 1, "Animated startup duplicated AI");
        }
        ocr = new(); ai = new() { Delay = 120 }; tick = 0; bool animate = false;
        updates = new();
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(animate ? Interlocked.Increment(ref tick) : 0, inside:true)), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(30))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            await Until(() => updates.Any(u => u.Translation == "T:line1"), "Initial translation missing");
            int start = updates.Count; animate = true; await Task.Delay(230);
            Check(updates.Skip(start).All(u => u.Translation is null or "T:line1"), "Pixel change or repeated OCR cleared readable subtitle");
            Check(ai.Calls.Count == 1 && ai.Canceled == 0, "Inside animation duplicated/canceled AI");
            Check(updates.Skip(start).All(u => u.State != PipelineState.Recognizing), "Duplicate OCR left the UI recognizing after completed translation");
            Check(updates.Last().State == PipelineState.Watching, "Completed repeated text did not return to next-dialogue waiting");
        }
        // A changed OCR source must supersede a slow translation even if that provider ignores cancellation.
        var late = new LateAi(); ocr = new(); tick = 0; byte text = 1; updates = new();
        await using (var pipeline = new TranslationPipeline(ocr, late, _ => Task.FromResult(Frame(Interlocked.Increment(ref tick), Volatile.Read(ref text))), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(30))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            try {
                await late.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); Volatile.Write(ref text, (byte)2);
                await Until(() => updates.Any(u => u.Translation == "T:line2"), "New dialogue waited indefinitely for older AI");
            late.Release.TrySetResult(); await Task.Delay(60);
            Check(!updates.Any(u => u.Translation == "T:line1"), "Late old AI overwrote new dialogue");
            } finally { late.Release.TrySetResult(); }
        }
        // Repeated game input during a paid request must not restart that request.
        ocr = new(); ai = new() { Delay = 180 }; updates = new();
        var stable = Frame(0);
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(stable), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(30))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            await Until(() => ai.Calls.Count == 1, "Trigger test AI did not start");
            for (int i = 0; i < 5; i++) { await pipeline.RequestTriggeredAsync(); await Task.Delay(10); }
            Check(updates.Last().State == PipelineState.Translating, "Pending AI was replaced by duplicate OCR/waiting state");
            await Until(() => updates.Any(u => u.Translation == "T:line1"), "Repeated trigger starved AI");
            Check(ai.Calls.Count == 1 && ai.Canceled == 0, "Same-text game input duplicated/canceled paid AI");
        }
        // One OCR fluctuation must not cancel the valid in-flight source.
        ocr = new(); ai = new() { Delay = 220 }; text = 1; updates = new();
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(0, Volatile.Read(ref text))), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(45))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            await Until(() => ai.Calls.Count == 1, "Noise test AI did not start");
            Volatile.Write(ref text, (byte)2); await pipeline.RequestTriggeredAsync();
            await Until(() => ocr.Seen.Contains((byte)2), "Noise sample was not recognized");
            Volatile.Write(ref text, (byte)1); await pipeline.RequestTriggeredAsync();
            await Until(() => updates.Any(u => u.Translation == "T:line1"), "Transient OCR noise starved valid AI");
            Check(ai.Calls.Count == 1 && ai.Canceled == 0, "Transient OCR candidate canceled/resubmitted valid text");
        }
        // Empty OCR and temporary capture failure retain readable text and do not resend AI.
        ocr = new(); ai = new(); tick = 0; text = 1; updates = new(); bool unavailable = false;
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => unavailable ? Task.FromException<ImageFrame>(new CaptureUnavailableException("Synthetic target unavailable")) : Task.FromResult(Frame(Interlocked.Increment(ref tick), Volatile.Read(ref text))), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(25))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            await Until(() => updates.Any(u => u.Translation == "T:line1"), "Empty test initial result missing");
            int start = updates.Count; Volatile.Write(ref text, (byte)0);
            await Until(() => updates.Any(u => u.Message.Contains("문자를 찾지 못했습니다")), "Empty OCR was not inspected");
            unavailable = true; await Until(() => updates.Any(u => u.State == PipelineState.Error), "Capture error was not surfaced");
            unavailable = false; Volatile.Write(ref text, (byte)1); await Task.Delay(140);
            Check(updates.Skip(start).All(u => u.Translation is null or "T:line1"), "Empty/capture failure erased subtitle");
            Check(ai.Calls.Count == 1, "Capture recovery resent paid AI for same text");
        }
        // QA from repeated recognition links only the same source, including an in-flight translation.
        var recorder = new GameQaRecorder(); ocr = new(); ai = new() { Delay = 160 }; tick = 0;
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref tick))), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(25), recorder, GameQaProfile.From(new()))) {
            pipeline.Start(true);
            await Until(() => recorder.Snapshot?.Record.Translation.Status == "completed", "Repeated OCR lost matching QA translation");
            Check(recorder.Snapshot!.Record.Translation.Text == "T:line1" && recorder.Snapshot.Record.PipelineText == "line1", "QA paired unrelated text/translation");
        }
        // Four consecutive sources, each with two exact cursor/animation variants.
        ocr = new(); ai = new(); tick = 0; text = 1; updates = new();
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref tick), Volatile.Read(ref text), inside:true)), new("en","ADV",false), new(), new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(25))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            for (byte scene = 1; scene <= 4; scene++) {
                Volatile.Write(ref text, scene);
                await Until(() => updates.Any(u => u.Translation == "T:line" + scene), "Four-screen sequence lost a new source");
                await Task.Delay(90); int calls = ocr.Calls; await Task.Delay(150);
                Check(ocr.Calls == calls, "Verified cursor frames repeatedly re-entered OCR");
                Check(updates.Last().State == PipelineState.Watching, "A completed screen did not stay in next-input waiting");
            }
            Check(ai.Calls.Count == 4, "Four distinct sources generated duplicate AI");
        }
        // Errors remain errors during identical-frame reuse; only an explicit retry recovers them.
        // Cover rapid unsteady poses and poses held long enough for verified
        // reuse. Neither should depend on a redundant outside-ROI probe.
        foreach (bool settledPoses in new[] { false, true }) {
        ocr = new(); ai = new() { Fail = true }; tick = 0; updates = new();
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(Interlocked.Increment(ref tick)/(settledPoses?3:1), inside:true)), new("en","ADV",false), new() { Provider = TranslationProvider.Gemini }, new(), "test", TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(25))) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            await Until(() => updates.Any(u => u.State == PipelineState.Error), "Provider failure was not reported");
            var initialFailure = updates.First(u => u.State == PipelineState.Error);
            Check(initialFailure.Message == "Synthetic provider failure" && initialFailure.FailureArea == PipelineStatusArea.Translation
                && initialFailure.OverlayMessage == "AI 서비스 요청에 실패했습니다. 서버 상태를 확인하세요.",
                "Translation failure lost its short overlay notice or detailed diagnostics");
            if (settledPoses) await Until(() => updates.Count(u => u.State == PipelineState.Error) > 1, "Failed translation was not restored during frame reuse");
            await Task.Delay(170);
            Check(updates.Last().State == PipelineState.Error && ai.Calls.Count == 1, "Duplicate OCR hid or retried AI failure");
            Check(updates.Where(u => u.State == PipelineState.Error).All(u => u.Message == initialFailure.Message
                && u.OverlayMessage == initialFailure.OverlayMessage), "Frame reuse dropped the short translation error notice");
            ai.Fail = false; await pipeline.RequestManualAsync();
            await Until(() => updates.Any(u => u.Translation == "T:line1"), "Explicit retry did not recover AI error");
            Check(ai.Calls.Count == 2 && updates.Last().State == PipelineState.Watching, "Retry did not return to waiting");
            Check(updates.Last().OverlayMessage is null, "Successful retry retained the failed translation's short notice");
            ai.Fail = true; await pipeline.RequestManualAsync(true);
            await Until(() => ai.Calls.Count == 3 && updates.Last().State == PipelineState.Error, "Forced retranslation error not surfaced");
            await Task.Delay(130);
            Check(updates.Last().State == PipelineState.Error && ai.Calls.Count == 3, "Cached prior result concealed failed retranslation");
            ai.Fail = false; await pipeline.RequestManualAsync(true);
            await Until(() => ai.Calls.Count == 4 && updates.Last().State == PipelineState.Watching, "Retry after failed retranslation stayed stuck");
            Check(updates.Last(u => u.Translation is not null).Translation == "T:line1", "Retry lost the visible translation");
        }
        }
        // Re-enter frame observation synchronously from the completion callback, before CTS disposal.
        // Logical completion must already be Watching; a still-owned CTS is not a running AI.
        ocr = new(); ai = new(); updates = new();
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Frame(0)), new("en","ADV",false), new(), new(), "test", settle:TimeSpan.Zero)) {
            pipeline.Updated += updates.Enqueue;
            pipeline.Updated += u => {
                if (u.State == PipelineState.Watching && u.Translation == "T:line1")
                    pipeline.RequestTriggeredAsync().GetAwaiter().GetResult();
            };
            pipeline.Start(false); await pipeline.RequestManualAsync();
            await Until(() => updates.Any(u => u.Translation == "T:line1"), "Completion callback did not run");
            await Task.Delay(50);
            Check(updates.Last().State == PipelineState.Watching, "CTS cleanup race restored Translating after completion");
            Check(ocr.Calls == 1 && ai.Calls.Count == 1, "Completion callback needlessly restarted OCR or AI");
        }
        Console.WriteLine($"PASS: {count} Muramasa animation/startup/subtitle/stale assertions; {clock.ElapsedMilliseconds}ms. Synthetic frames + Mock AI only.");
    }
    private sealed class LateAi : ITranslationService {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranslationResult> TranslateAsync(TranslationRequest r, CancellationToken t) {
            if (r.Text == "line1") { Started.TrySetResult(); await Release.Task; }
            return new("T:" + r.Text, TimeSpan.Zero, 0);
        }
    }
}
