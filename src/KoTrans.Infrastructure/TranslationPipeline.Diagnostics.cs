using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Ocr;
namespace KoTrans.Infrastructure;

public sealed partial class TranslationPipeline
{
    private readonly DebugTelemetry? telemetry;
    private long diagnosticSession;
    public long DiagnosticSession => diagnosticSession;
    private bool reportedFallback;
    // Called under the pipeline lock after cancellation/version checks. Never
    // compare cached answers or append the already translated NVL prefix here.
    private void DiagnosticAiResponse(string source, string result)
    {
        if (telemetry is null) return;
        string normalized = result.Replace("\r\n", "\n").Trim();
        static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        telemetry.AiResponse(diagnosticSession, Hash(source), Hash(normalized), source.Length, normalized.Length,
            normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);
    }
    private readonly OcrResultCache ocrResultCache = new();
    private void PublishMarkerDiagnostics()
    {
        if(ocr is ILearnedMarkerFrameGate markerGate)telemetry?.MarkerLearning(diagnosticSession,markerGate.LearningStatus);
    }
    private async Task<OcrResult> RecognizeMeasuredAsync(ImageFrame frame, OcrOptions options, CancellationToken token, DebugReason kind, bool bypassCache = false)
    {
        long begin = telemetry is null ? 0 : Stopwatch.GetTimestamp();
        void OnMiss() {
            Interlocked.Increment(ref ocrCount);
            telemetry?.OcrCacheMissed(diagnosticSession,ocrResultCache.LastMissReason);
            telemetry?.OcrStarted(diagnosticSession, kind);
        }
        try {
            var effective = telemetry is null ? options : options with { CollectMarkerDiagnostics = true };
            var result = await (ocr is MarkerLearningOcrService learning
                ? learning.RecognizeCachedAsync(frame,effective,token,ocrResultCache,bypassCache,OnMiss)
                : ocrResultCache.RecognizeAsync(ocr,frame,effective,token,bypassCache,OnMiss)).ConfigureAwait(false);
            PublishMarkerDiagnostics();
            if(result.ReusedFromCache)telemetry?.OcrCacheReused(diagnosticSession,result,kind);
            else telemetry?.OcrCompleted(diagnosticSession, result, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            if (telemetry is not null && result.Execution.IsFallback && !reportedFallback) {
                reportedFallback = true; telemetry.Record(diagnosticSession, DebugCode.GPU_FALLBACK, DebugReason.OcrFailure);
            }
            return result;
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            telemetry?.Record(diagnosticSession, DebugCode.OCR_TEXT_REJECTED,
                ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.OcrFailure,
                v => v with { LastError = ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.OcrFailure }); throw;
        }
    }
    private void ObserveGate(FrameDecision decision, bool scheduled = false)
    {
        if (telemetry is null) return;
        var reason = decision switch { FrameDecision.Unchanged => DebugReason.Unchanged, FrameDecision.Stabilizing => DebugReason.Stabilizing,
            FrameDecision.Ready => DebugReason.Ready, _ => DebugReason.Probe };
        telemetry.Record(diagnosticSession, decision == FrameDecision.Unchanged ? DebugCode.OCR_SKIPPED : DebugCode.FRAME_CHANGED,
            frames.ProbeOutside ? DebugReason.OutsideRoiProbe : reason,
            v => v with { Decision = reason, GateReason = frames.ProbeOutside ? DebugReason.OutsideRoiProbe : frames.HasRoi && decision != FrameDecision.Unchanged ? DebugReason.RoiChange : reason,
                FinalFrameAction=scheduled ? DebugFrameAction.OcrQueued : decision==FrameDecision.Unchanged ? DebugFrameAction.Watching : DebugFrameAction.CheckingStability,
                ReuseReason=DebugReason.None,
                OcrScheduled = scheduled, GateScope = frames.HasRoi ? "ROI" : "Full-frame" });
    }
    private void DiagnosticTranslation(Job job, DebugCode code, DebugReason reason, double? ms = null, Exception? failure = null)
        => telemetry?.Record(diagnosticSession, code, reason, job.Version != Version ? null : v => v with { TranslationState = reason,
            TranslationMs = ms ?? v.TranslationMs, LastError = code is DebugCode.TRANSLATION_FAILED or DebugCode.TRANSLATION_CANCELLED ? reason : v.LastError,
            TranslationFailures = v.TranslationFailures + (code == DebugCode.TRANSLATION_FAILED ? 1 : 0),
            LastOllamaFailure = failure is OllamaServerException ollama ? ollama.Kind : v.LastOllamaFailure,
            LastOllamaFailureStage = failure is OllamaServerException staged ? staged.Stage : v.LastOllamaFailureStage,
            LastOllamaHttpStatus = failure is OllamaServerException http ? http.Status : v.LastOllamaHttpStatus }, job.DiagnosticCycle);
}
