using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class CapturePauseTests
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready()) { if (clock.ElapsedMilliseconds > 4000) throw new Exception("Capture pause scenario timed out"); await Task.Delay(5); }
    }
    internal static async Task Run()
    {
        int state = 0, captures = 0;
        var frame = new ImageFrame(100, 50, 400, new byte[20000]);
        var telemetry = new DebugTelemetry(); var ocr = new Ocr(); var ai = new Ai();
        var updates = new ConcurrentQueue<PipelineUpdate>();
        await using (var pipeline = new TranslationPipeline(ocr, ai, _ => {
            Interlocked.Increment(ref captures);
            int selected = Volatile.Read(ref state);
            return selected == 0 ? Task.FromResult(frame) : Task.FromException<ImageFrame>(selected == 4
                ? new CaptureUnavailableException("Synthetic actual failure")
                : new CaptureUnavailableException("Synthetic waiting", (CaptureWaitReason)selected));
        }, new("ja", "ADV", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero, diagnostics: telemetry)) {
            pipeline.Updated += updates.Enqueue; pipeline.Start(true);
            await Until(() => updates.Any(u => u.Translation == "translated"));
            int calls = ocr.Calls; long cycle = telemetry.Snapshot().Current!.Id;
            foreach (int reason in new[] { 1, 2, 3 }) {
                Volatile.Write(ref state, reason);
                await Until(() => telemetry.Snapshot().Values.CaptureWait == (CaptureWaitReason)reason);
                int start = Volatile.Read(ref captures);
                await Until(() => Volatile.Read(ref captures) >= start + 6);
                var snap = telemetry.Snapshot();
                Check(snap.Values.CaptureStatus == DebugReason.Paused && snap.Values.CaptureError == DebugReason.None && snap.Values.LastError == DebugReason.None, "Expected waiting became an error");
                Check(snap.Events.Count(e => e.Code == DebugCode.CAPTURE_PAUSED && e.Values.CaptureWait == (CaptureWaitReason)reason) == 1, "Polling flooded pause events");
                Check(!snap.Events.Any(e => e.Code == DebugCode.CAPTURE_FAILED) && !updates.Any(u => u.State == PipelineState.Error), "Expected waiting emitted a failure");
                Check(ocr.Calls == calls && ai.Calls == 1 && snap.Current!.Id == cycle, "Waiting performed OCR/AI or created a cycle");
                string expectedNotice = (CaptureWaitReason)reason switch {
                    CaptureWaitReason.NotForeground => "게임 창을 앞으로 가져오세요. 활성화되면 인식을 재개합니다.",
                    CaptureWaitReason.HiddenOrMinimized => "게임 창이 최소화되어 있습니다. 창을 복원하세요.",
                    _ => "KO Trans 2 창이 게임 화면을 가리고 있습니다. 창을 옮기거나 최소화하세요."
                };
                Check(updates.Any(u => u.FailureArea == PipelineStatusArea.Capture && u.Important
                    && u.Message.Contains("Synthetic waiting") && u.OverlayMessage == expectedNotice),
                    "Capture wait lost either its short overlay notice or diagnostic detail in the pipeline");
            }
            Volatile.Write(ref state, 0);
            await Until(() => telemetry.Snapshot().Events.Any(e => e.Code == DebugCode.CAPTURE_RESUMED));
            int resumedCaptures = Volatile.Read(ref captures);
            await Until(() => Volatile.Read(ref captures) >= resumedCaptures + 8);
            Check(telemetry.Snapshot().Values.CaptureWait == CaptureWaitReason.None && ocr.Calls == calls && ai.Calls == 1, "Resuming lost the cached frame/result");
            Volatile.Write(ref state, 4);
            await Until(() => updates.Any(u => u.State == PipelineState.Error));
            Check(updates.Any(u => u.State == PipelineState.Error && u.Message == "Synthetic actual failure"
                && u.OverlayMessage == "게임 화면을 확인하지 못했습니다. 대상 창과 인식 영역을 확인하세요."),
                "Actual capture failure did not publish separate overlay and diagnostic messages");
            Check(telemetry.Snapshot().Events.Any(e => e.Code == DebugCode.CAPTURE_FAILED) && telemetry.Snapshot().Values.CaptureWait == CaptureWaitReason.None, "Unclassified capture error was hidden");
            Volatile.Write(ref state, 1);
            await Until(() => telemetry.Snapshot().Values.CaptureStatus == DebugReason.Paused);
            Check(telemetry.Snapshot().Values.CaptureError == DebugReason.None && telemetry.Snapshot().Values.LastError == DebugReason.CaptureUnavailable, "Expected pause erased historical error or retained active error");
            Check(updates.All(u => u.Translation is null or "translated"), "Waiting cleared subtitles");
        }
        // Normal stop must not manufacture CAPTURE_FAILED from the canceled capture.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        telemetry = new();
        await using (var pipeline = new TranslationPipeline(new Ocr(), null, async token => {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return frame;
        }, new("ja", "ADV", false), new(), new(), "", diagnostics: telemetry, translationEnabled: false)) {
            pipeline.Start(true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await pipeline.DisposeAsync();
            Check(!telemetry.Snapshot().Events.Any(e => e.Code == DebugCode.CAPTURE_FAILED), "Normal stop was reported as capture failure");
            telemetry.CapturePaused(pipeline.DiagnosticSession, CaptureWaitReason.Occluded);
            Check(telemetry.Snapshot().Values.CaptureStatus == DebugReason.Stopped, "Late pause reactivated stopped session");
        }
        Check(!new CaptureUnavailableException("Synthetic", (CaptureWaitReason)99).IsExpectedWait, "Unknown reason hid an actual failure");
        Console.WriteLine($"PASS: {checks} capture pause/resume/error assertions (Mock capture/OCR/AI only).");
    }
    private sealed class Ocr : IOcrService
    {
        public int Calls;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t) {
            Interlocked.Increment(ref Calls); return Task.FromResult(new OcrResult([new("synthetic source", 1, new(0, 0, 100, 50))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        public int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest r, CancellationToken t) {
            Interlocked.Increment(ref Calls); return Task.FromResult(new TranslationResult("translated", TimeSpan.Zero));
        }
    }
}
