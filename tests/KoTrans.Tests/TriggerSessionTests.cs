using System.Collections.Concurrent;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class TriggerSessionTests
{
    internal static async Task Run()
    {
        int assertions = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
        async Task Until(Func<bool> condition) {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!condition()) await Task.Delay(5, timeout.Token);
        }
        foreach (bool automatic in new[] { false, true }) {
            int state = 0, captures = 0; var ai = new Ai(); var ocr = new Ocr();
            var updates = new ConcurrentQueue<PipelineUpdate>();
            await using var pipe = new TranslationPipeline(ocr, ai, _ => {
                Interlocked.Increment(ref captures); return Task.FromResult(Frame(Volatile.Read(ref state)));
            }, new("en", "NVL", false), new(), new(), "test", interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.FromMilliseconds(100));
            pipe.Updated += updates.Enqueue; pipe.Start(automatic);
            if (!automatic) {
                await Task.Delay(40); Check(captures == 0, "Idle trigger mode captured without input");
                await pipe.RequestTriggeredAsync();
            }
            await Until(() => captures >= 2);
            Volatile.Write(ref state, 1); await Task.Delay(30);
            Volatile.Write(ref state, 2); await Task.Delay(30);
            Check(ai.Texts.IsEmpty && !updates.Any(u => u.Original is not null), "An effect/typewriter candidate reached AI or the visible source");
            Volatile.Write(ref state, 3);
            await Until(() => updates.Any(u => u.Translation == "T:Complete dialogue."));
            Check(ai.Texts.SequenceEqual(new[] { "Complete dialogue." }), "Unstable candidates created paid requests");
            int settled = captures; await Task.Delay(60);
            Check(automatic ? captures > settled : captures <= settled + 1, "Trigger session kept watching after completion or automatic mode stopped");
            if (!automatic) {
                await pipe.RequestTriggeredAsync();
                await Task.Delay(160); // Old text outlasts the settle interval before the effect starts.
                Volatile.Write(ref state, 0); await Task.Delay(30);
                Check(ai.Texts.Count == 1, "Old text or a blank effect repeated translation");
            }
            Volatile.Write(ref state, 4);
            if (!automatic) {
                await Task.Delay(40); Check(ai.Texts.Count == 1, "A new screen started OCR without another trigger");
                for (int i = 0; i < 8; i++) await pipe.RequestTriggeredAsync();
            }
            await Until(() => updates.Any(u => u.Translation == "T:Next dialogue."));
            Check(ai.Texts.Count == 2, "Burst inputs duplicated translation");
            pipe.Stop(); await pipe.DisposeAsync(); settled = captures;
            await pipe.RequestTriggeredAsync(); await Task.Delay(30);
            Check(captures == settled, "Stopped trigger restarted capture");
        }
        {
            int state = 0, captures = 0; var ai = new Ai(); var updates = new ConcurrentQueue<PipelineUpdate>();
            await using var pipe = new TranslationPipeline(new Ocr(), ai, _ => {
                Interlocked.Increment(ref captures); return Task.FromResult(Frame(Volatile.Read(ref state)));
            }, new("en", "NVL", false), new(), new(), "test", interval: TimeSpan.FromMilliseconds(10),
                settle: TimeSpan.FromMilliseconds(30), triggerWindow: TimeSpan.FromMilliseconds(140));
            pipe.Updated += updates.Enqueue; pipe.Start(false); await pipe.RequestTriggeredAsync();
            await Until(() => updates.Any(u => u.Important && u.Message.Contains("다음 입력")));
            int idle = captures; await Task.Delay(40);
            Check(captures == idle && ai.Texts.IsEmpty && !updates.Last().IsBusy, "Blank input session retried forever or called AI");
            Volatile.Write(ref state, 3); await pipe.RequestTriggeredAsync();
            await Until(() => ai.Texts.Count == 1);
            Check(ai.Texts.Single() == "Complete dialogue.", "Session could not recover after timeout");
        }
        {
            int captures = 0; var updates = new ConcurrentQueue<PipelineUpdate>(); var engine = new HeldOcr();
            await using var pipe = new TranslationPipeline(engine, new Ai(), _ => {
                Interlocked.Increment(ref captures); return Task.FromResult(Frame(3));
            }, new("en", "NVL", false), new(), new(), "test", interval: TimeSpan.FromMilliseconds(10),
                settle: TimeSpan.FromMilliseconds(30), triggerWindow: TimeSpan.FromMilliseconds(140));
            pipe.Updated += updates.Enqueue; pipe.Start(false); await pipe.RequestTriggeredAsync();
            await Until(() => engine.Started); await Until(() => engine.Canceled);
            await Until(() => updates.Any(u => u.Important && u.Message.Contains("다음 입력")));
            int idle = captures; await Task.Delay(40);
            Check(captures == idle && !updates.Any(u => u.Original is not null), "Slow OCR kept the trigger monitor alive or published after expiry");
        }
        foreach (bool failOld in new[] { false, true }) {
            int state = 3; var ai = new Ai(); var engine = new FirstHeldOcr { FailFirst = failOld };
            var updates = new ConcurrentQueue<PipelineUpdate>();
            await using var pipe = new TranslationPipeline(engine, ai, _ => Task.FromResult(Frame(Volatile.Read(ref state))),
                new("en", "NVL", false), new(), new(), "test", interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.FromMilliseconds(40));
            pipe.Updated += updates.Enqueue; pipe.Start(false); await pipe.RequestTriggeredAsync(); await Until(() => engine.Started);
            Volatile.Write(ref state, 4); await pipe.RequestTriggeredAsync(); engine.Release.SetResult();
            await Until(() => ai.Texts.Count > 0);
            Check(ai.Texts.SequenceEqual(new[] { "Next dialogue." }), "A late previous-input OCR was accepted after a newer trigger");
            Check(!updates.Any(u=>u.State==PipelineState.Error),"An obsolete OCR error was displayed over a new input session");
        }
        // Exercise production timing too: fast test overrides must not hide a typewriter
        // frame changing during the shorter quiet wait, before the next monitor tick.
        foreach (bool automatic in new[] { false, true }) {
            int state = 1; var ai = new Ai(); var diagnostics = new DebugTelemetry();
            var updates = new ConcurrentQueue<PipelineUpdate>();
            await using var pipe = new TranslationPipeline(new Ocr(), ai,
                _ => Task.FromResult(Frame(Volatile.Read(ref state))),
                new("en", "NVL", false), new(), new(), "test", diagnostics: diagnostics);
            pipe.Updated += updates.Enqueue; pipe.Start(automatic);
            Check(diagnostics.Snapshot().Values.SettleMs == 500, "Production text quiet wait is not 500 ms");
            if (!automatic) await pipe.RequestTriggeredAsync();
            await Until(() => diagnostics.Snapshot().Events.Any(e => e.Reason == DebugReason.CandidateQuietWait));
            await Task.Delay(250);
            Check(ai.Texts.IsEmpty && !updates.Any(u => u.Original is not null), "Default quiet wait published an unfinished candidate early");
            Volatile.Write(ref state, 3);
            await Until(() => updates.Any(u => u.Translation == "T:Complete dialogue."));
            Check(diagnostics.Snapshot().Events.Any(e => e.Reason == DebugReason.CandidateSuperseded), "Default monitor did not reject the changed candidate during its quiet wait");
            await Task.Delay(600);
            Check(ai.Texts.SequenceEqual(new[] { "Complete dialogue." }), "Shorter default wait translated a partial sentence or repeated the stable sentence");
        }
        Console.WriteLine($"PASS: {assertions} trigger/automatic shared-stabilization assertions (synthetic frames, Mock AI).");
    }
    private static ImageFrame Frame(int value) => new(120, 60, 480, Enumerable.Repeat((byte)(value * 40), 28800).ToArray());
    private sealed class Ocr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            string text = frame.Pixels.Span[0] switch { 40 => "Com", 80 => "Complete", 120 => "Complete dialogue.", 160 => "Next dialogue.", _ => "" };
            return Task.FromResult(new OcrResult(text.Length == 0 ? [] : [new(text, 1, new(10, 10, 100, 30))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        internal readonly ConcurrentQueue<string> Texts = new();
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Texts.Enqueue(request.Text); return Task.FromResult(new TranslationResult("T:" + request.Text, TimeSpan.Zero, 0)); }
    }
    private sealed class HeldOcr : IOcrService
    {
        internal volatile bool Started, Canceled;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            Started = true;
            try { await Task.Delay(Timeout.Infinite, token); return new([], TimeSpan.Zero); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FirstHeldOcr : IOcrService
    {
        internal volatile bool Started;
        internal bool FailFirst;
        private int calls;
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1) {
                Started = true; await Release.Task;
                if (FailFirst) throw new OcrInitializationException("Obsolete synthetic OCR failure");
            }
            return await new Ocr().RecognizeAsync(frame, options, token);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
