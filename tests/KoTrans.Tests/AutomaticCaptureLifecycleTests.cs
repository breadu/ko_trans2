using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class AutomaticCaptureLifecycleTests
{
    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready()) {
            if (clock.Elapsed > TimeSpan.FromSeconds(4)) throw new Exception("Automatic capture regression timed out");
            await Task.Delay(5);
        }
    }
    private static ImageFrame Frame(byte value) => new(100, 50, 400, Enumerable.Repeat(value, 20000).ToArray());
    internal static async Task Run()
    {
        await PausedCaptureDiscardsQueuedFrames();
        await OldCaptureFailurePreservesManualRequest();
        await PausePreservesAcceptedTranslation();
        Console.WriteLine("PASS: automatic capture queue invalidation, resume, manual priority and accepted AI preservation (Mock only).");
    }
    private static async Task PausedCaptureDiscardsQueuedFrames()
    {
        int state = 1, captures = 0;
        var ocr = new HeldOcr(); var updates = new ConcurrentQueue<PipelineUpdate>();
        var diagnostics = new DebugTelemetry();
        await using var pipeline = new TranslationPipeline(ocr, null, _ => {
            Interlocked.Increment(ref captures);
            return Volatile.Read(ref state) is var s && s == 0
                ? Task.FromException<ImageFrame>(new CaptureUnavailableException("Synthetic wait", CaptureWaitReason.NotForeground))
                : Task.FromResult(Frame((byte)(s * 60)));
        }, new("en", "ADV", false), new(), new(), "", translationEnabled: false,
            interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero, diagnostics: diagnostics);
        pipeline.Updated += updates.Enqueue;
        pipeline.Start(true);
        try {
            await ocr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Volatile.Write(ref state, 2);
            int start = Volatile.Read(ref captures);
            await Until(() => Volatile.Read(ref captures) >= start + 6); // Queue the stable second frame behind held OCR.
            Volatile.Write(ref state, 0);
            await Until(() => diagnostics.Snapshot().Values.CaptureWait == CaptureWaitReason.NotForeground);
            ocr.Release.TrySetResult();
            await ocr.Returned.Task.WaitAsync(TimeSpan.FromSeconds(4));
            start = Volatile.Read(ref captures);
            await Until(() => Volatile.Read(ref captures) >= start + 8);
            if (ocr.Calls != 1 || updates.Any(u => !string.IsNullOrEmpty(u.Original)))
                throw new Exception("Queued frame was OCRed/published after capture paused");
            Volatile.Write(ref state, 3);
            await Until(() => updates.Any(u => u.Original == "source 180"));
            if (updates.Any(u => u.Original is "source 60" or "source 120"))
                throw new Exception("An obsolete capture survived resumption");
        }
        finally { ocr.Release.TrySetResult(); }
    }
    private static async Task OldCaptureFailurePreservesManualRequest()
    {
        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int captures = 0;
        var ocr = new HeldOcr(); var updates = new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(ocr, null, async token => {
            if (Interlocked.Increment(ref captures) == 1) {
                captureEntered.TrySetResult();
                await releaseCapture.Task.WaitAsync(token);
                captureFailed.TrySetResult();
                throw new CaptureUnavailableException("Obsolete capture", CaptureWaitReason.NotForeground);
            }
            await Task.Delay(Timeout.Infinite, token);
            return Frame(0);
        }, new("en", "ADV", false), new(), new(), "", translationEnabled: false,
            interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero);
        pipeline.Updated += updates.Enqueue;
        pipeline.Start(true);
        try {
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await pipeline.RequestManualAsync(image: Frame(180));
            await ocr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            releaseCapture.TrySetResult();
            await captureFailed.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await Until(() => Volatile.Read(ref captures) >= 2); // Catch handler has completed.
            if (ocr.Token.IsCancellationRequested) throw new Exception("Older capture failure canceled newer manual OCR");
            ocr.Release.TrySetResult();
            await Until(() => updates.Any(u => u.Original == "source 180"));
            if (updates.Any(u => u.Message.Contains("Obsolete capture", StringComparison.Ordinal)))
                throw new Exception("Older capture failure overwrote current manual status");
        }
        finally { releaseCapture.TrySetResult(); ocr.Release.TrySetResult(); }
    }
    private static async Task PausePreservesAcceptedTranslation()
    {
        int paused = 0;
        var ocr = new HeldOcr(); ocr.Release.TrySetResult();
        var ai = new HeldAi(); var diagnostics = new DebugTelemetry();
        var updates = new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(ocr, ai, _ => Volatile.Read(ref paused) == 0
            ? Task.FromResult(Frame(180))
            : Task.FromException<ImageFrame>(new CaptureUnavailableException("Synthetic wait", CaptureWaitReason.HiddenOrMinimized)),
            new("en", "ADV", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(10),
            settle: TimeSpan.Zero, diagnostics: diagnostics);
        pipeline.Updated += updates.Enqueue; pipeline.Start(true);
        try {
            await ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Volatile.Write(ref paused, 1);
            await Until(() => diagnostics.Snapshot().Values.CaptureWait == CaptureWaitReason.HiddenOrMinimized);
            if (ai.Token.IsCancellationRequested) throw new Exception("Capture pause canceled an accepted AI request");
            ai.Release.TrySetResult();
            await Until(() => updates.Any(u => u.Translation == "accepted translation"));
            Volatile.Write(ref paused, 0);
            await Until(() => diagnostics.Snapshot().Values.CaptureWait == CaptureWaitReason.None);
            if (ai.Calls != 1) throw new Exception("Capture resume resent an accepted translation");
        }
        finally { ai.Release.TrySetResult(); }
    }
    private sealed class HeldAi : ITranslationService
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal int Calls;
        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); Token = token; Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new("accepted translation", TimeSpan.Zero);
        }
    }
    private sealed class HeldOcr : IOcrService
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal CancellationToken Token;
        public OcrState State => OcrState.Ready;
        public OcrCapabilities Capabilities => OcrCapabilities.Basic;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            if (Interlocked.Increment(ref Calls) == 1) {
                Token = token; Entered.TrySetResult();
                await Release.Task; // Model can finish late even after cancellation.
                Returned.TrySetResult();
            }
            return new([new("source " + frame.Pixels.Span[0], 1, new(10, 10, 80, 30))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
