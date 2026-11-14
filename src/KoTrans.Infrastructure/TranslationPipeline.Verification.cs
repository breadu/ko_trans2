using System.Diagnostics;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public sealed partial class TranslationPipeline
{
    // One delayed, independent read per ordinary NVL acceptance. A correction
    // does not arm another check. There is no polling OCR or automatic AI retry.
    private static readonly TimeSpan SourceVerificationDelay = TimeSpan.FromSeconds(2);
    private long sourceVerificationAt, sourceVerificationVersion;

    // Called under sync, after a successful capture and before any reuse gate.
    private bool QueueSourceVerification(ImageFrame frame, long startedAt, double captureMs)
    {
        if (!watchContinuously || sourceVerificationAt == 0 || sourceVerificationVersion != Version
            || Stopwatch.GetElapsedTime(sourceVerificationAt) < SourceVerificationDelay
            || current is not null || jobs.Reader.TryPeek(out _)
            || translationState != PipelineState.Watching || recognizedText != translationText
            || recognizedOriginal != translationOriginal) return false;
        sourceVerificationAt = 0; // Bounded even if capture later pauses or OCR fails.
        telemetry?.Candidate(diagnosticSession);
        telemetry?.Record(diagnosticSession, DebugCode.OCR_SOURCE_VERIFICATION, DebugReason.SourceVerification,
            v => v with { SourceVerificationRequests = v.SourceVerificationRequests + 1,
                FinalFrameAction = DebugFrameAction.OcrQueued, OcrScheduled = true, GateReason = DebugReason.SourceVerification,
                ReuseReason = DebugReason.None, GateScope = "Full-frame" });
        jobs.Writer.TryWrite(new(Version, frame, null, false, false, captureMs, startedAt,
            Observation: visualRevision, FullSearch: true, CaptureEpoch: captureEpoch, VerifyAcceptedSource: true));
        return true;
    }

    private void ObserveSourceVerification(Job job, string text)
    {
        lock (sync) {
            if (!job.VerifyAcceptedSource || !IsCurrentOcrJob(job)) return;
            bool matches = text == translationText;
            telemetry?.Record(diagnosticSession, DebugCode.OCR_SOURCE_VERIFIED, DebugReason.SourceVerification,
                v => v with { SourceVerificationCompleted = v.SourceVerificationCompleted + 1,
                    SourceVerificationMismatches = v.SourceVerificationMismatches + (matches ? 0 : 1),
                    SourceVerificationMatchesAccepted = matches,
                    SourceVerificationExpectedCharacters = translationText?.Length,
                    SourceVerificationResultCharacters = text.Length });
        }
    }
}
