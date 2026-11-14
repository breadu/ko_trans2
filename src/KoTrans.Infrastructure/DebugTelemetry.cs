using System.Collections.Generic;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// Only codes, fixed identifiers and numeric measurements cross this boundary. Never
// pass source text, provider output, exception messages, window titles or paths.
public enum DebugCode { SESSION_STARTED, SESSION_START_FAILED, SESSION_STOPPED, INPUT_TRIGGERED, FRAME_CHANGED, WAIT_STABLE, OCR_STARTED, OCR_COMPLETED,
    OCR_SKIPPED, OCR_FRAME_REUSED, OCR_FULL_RETRY, OCR_TEXT_REJECTED, OCR_TEXT_ACCEPTED, TRANSLATION_REQUESTED,
    TRANSLATION_CACHE_HIT, TRANSLATION_PARTIAL, TRANSLATION_COMPLETED, TRANSLATION_FAILED, TRANSLATION_CANCELLED,
    STALE_RESULT_DISCARDED, OVERLAY_UPDATED, CAPTURE_COMPLETED, CAPTURE_FAILED, GPU_FALLBACK, CYCLE_STARTED, CYCLE_COMPLETED, CYCLE_MERGED,
    CAPTURE_PAUSED, CAPTURE_RESUMED, OCR_RESULT_CACHE_HIT, OCR_SOURCE_VERIFICATION, OCR_SOURCE_VERIFIED }
public enum DebugReason { None, Unchanged, Stabilizing, Ready, Probe, RepeatedAnimation, VerifiedFrame, RoiChange,
    OutsideRoiProbe, TriggerWaiting, TriggerReceived, TriggerExpired, TextCandidateChanged, CandidateQuietWait,
    CandidateSuperseded, EmptyText, TooLong, SameSource, AdvContinuation, NvlPage, NvlAppend, Accepted,
    Standard, Manual, Roi, NvlIncremental, NvlFullRetry, RoiFullRetry, AdvFullRetry, AdvRecovery,
    Cache, Streaming, Completed, Cancelled, CaptureUnavailable, OcrFailure, TranslationFailure, Stopped,
    Connected, Missing, Ambiguous, Excluded, ExclusionFailed, NotMeasured, Displayed, Hidden, Rejected, Paused, LearnedMarker, ExactOcrCache, RepeatedTextConfirmed, SourceVerification, StaticLabelRecovery }
public enum DebugFrameAction { NotMeasured, Watching, CheckingStability, OcrQueued, Recognizing, OcrCompleted,
    ConfirmingText, Accepted, Reused, WaitingForTrigger, CaptureUnavailable, Stopped, CapturePaused }
public enum DebugAiSourceRelation { NotMeasured, First, SameAsPrevious, DifferentFromPrevious }
public enum DebugAiResultRelation { NotMeasured, First, SameSourceSameResult, SameSourceDifferentResult, DifferentSourceSameResult, DifferentSourceDifferentResult }
public sealed record DebugValues
{
    public string CaptureMode { get; init; } = "N/A";
    public string RecognitionMode { get; init; } = "N/A";
    public string ReadingMode { get; init; } = "N/A";
    public DebugReason Target { get; init; } = DebugReason.NotMeasured;
    public DebugReason CaptureStatus { get; init; } = DebugReason.NotMeasured;
    public DebugReason Exclusion { get; init; } = DebugReason.NotMeasured;
    public DebugReason CaptureError { get; init; } = DebugReason.None;
    public CaptureWaitReason CaptureWait { get; init; } = CaptureWaitReason.None;
    public string CapturePath { get; init; } = "N/A";
    public int? ScreenCaptureWidth { get; init; }
    public int? ScreenCaptureHeight { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public DebugReason Decision { get; init; } = DebugReason.NotMeasured;
    public DebugFrameAction FinalFrameAction { get; init; } = DebugFrameAction.NotMeasured;
    public bool OcrInProgress { get; init; }
    public DebugReason GateReason { get; init; } = DebugReason.NotMeasured;
    public bool OcrScheduled { get; init; }
    public string GateScope { get; init; } = "N/A";
    public double? StableMs { get; init; }
    public double? SettleMs { get; init; }
    public DebugReason Trigger { get; init; } = DebugReason.NotMeasured;
    public DebugReason ReuseReason { get; init; } = DebugReason.None;
    public string Engine { get; init; } = "N/A";
    public string OcrModel { get; init; } = "N/A";
    public string RequestedBackend { get; init; } = "N/A";
    public string ActualBackend { get; init; } = "N/A";
    public bool Fallback { get; init; }
    public int? GpuAttempts { get; init; }
    public int? CpuAttempts { get; init; }
    public double? OcrMs { get; init; }
    public double? DetectionMs { get; init; }
    public double? RecognitionMs { get; init; }
    public double? PostprocessMs { get; init; }
    public int? DetectedRegions { get; init; }
    public int? AcceptedLines { get; init; }
    public int? MarkerObservations { get; init; }
    public IReadOnlyList<OcrMarkerObservation>? MarkerDiagnostics { get; init; }
    public MarkerLearningMode MarkerLearningMode {get;init;}
    public int LearnedMarkerPatterns {get;init;}
    public int LearningMarkerCandidates {get;init;}
    public int LearningMarkerPages {get;init;}
    public int LearningMarkerAnimatedPages {get;init;}
    public int LearnedMarkerExcluded {get;init;}
    public int LearnedMarkerWouldExclude {get;init;}
    public bool MarkerLearningStorageError {get;init;}
    public long LearnedMarkerExcludedTotal {get;init;}
    public long LearnedMarkerExcludingOcrCalls {get;init;}
    public long LearnedMarkerSkippedFrames {get;init;}
    public long LearnedMarkerRecheckFrames {get;init;}
    public long LearnedMarkerExtraBodyChecks {get;init;}
    public long LearnedMarkerBodyCacheHits {get;init;}
    public long LearnedMarkerFastSamples {get;init;}
    public long LearnedMarkerFastCycles {get;init;}
    public long LearnedMarkerFramePreparations {get;init;}
    public long LearnedMarkerFramePreparationFailures {get;init;}
    public long LearnedMarkerRecoveredCycles {get;init;}
    public MarkerRecoveryFailure LearnedMarkerRecoveryFailure {get;init;}
    public int LearnedMarkerFrameNoisePixels {get;init;}
    public long LearningMarkerValidatedCycles {get;init;}
    public long LearningMarkerBodyChangeDiscards {get;init;}
    public long LearningMarkerExpiredWatches {get;init;}
    public MarkerFrameDecision LearnedMarkerFrameDecision {get;init;}
    public MarkerFrameFailure LearnedMarkerFrameFailure {get;init;}
    public DebugReason Search { get; init; } = DebugReason.NotMeasured;
    public string Provider { get; init; } = "N/A";
    public string TranslationModel { get; init; } = "N/A";
    public string TranslationBackend { get; init; } = "N/A";
    public DebugReason TranslationState { get; init; } = DebugReason.NotMeasured;
    public double? TranslationMs { get; init; }
    public long TranslationFailures { get; init; }
    public OllamaFailureKind LastOllamaFailure { get; init; }
    public OllamaFailureStage LastOllamaFailureStage { get; init; }
    public int? LastOllamaHttpStatus { get; init; }
    public long LogicalOcrCalls { get; init; }
    public long SourceVerificationRequests { get; init; }
    public long SourceVerificationCompleted { get; init; }
    public long SourceVerificationMismatches { get; init; }
    public bool? SourceVerificationMatchesAccepted { get; init; }
    public int? SourceVerificationExpectedCharacters { get; init; }
    public int? SourceVerificationResultCharacters { get; init; }
    public long OcrResultCacheHits { get; init; }
    public OcrCacheMissReason OcrResultCacheLastMiss {get;init;}
    public OcrCacheMissCounts OcrResultCacheMisses {get;init;}=new();
    public bool RecognitionCacheMeasured {get;init;}
    public long RecognitionInferences {get;init;}
    public long RecognitionCacheHits {get;init;}
    public OcrCacheMissReason RecognitionCacheLastMiss {get;init;}
    public OcrCacheMissCounts RecognitionCacheMisses {get;init;}=new();
    public long FrameReuses { get; init; }
    public long AnimationReuses { get; init; }
    public long AiRequests { get; init; }
    public int? NvlPlannedBlocks { get; init; }
    public bool NvlShortTailCombined { get; init; }
    public bool NvlPrefixReopened { get; init; }
    public int? LastAiSourceCharacters { get; init; }
    public DebugAiSourceRelation LastAiSourceRelation { get; init; } = DebugAiSourceRelation.NotMeasured;
    public int? LastAiResultSourceCharacters { get; init; }
    public int? LastAiResultCharacters { get; init; }
    public int? LastAiResultLines { get; init; }
    public DebugAiResultRelation LastAiResultRelation { get; init; }
    public long AiRepeatedResultForChangedSource { get; init; }
    public long TranslationCacheHits { get; init; }
    public int? OverlayExpectedSourceCharacters { get; init; }
    public int? OverlaySourceCharacters { get; init; }
    public int? OverlayExpectedTranslationCharacters { get; init; }
    public int? OverlayTranslationCharacters { get; init; }
    public bool? OverlaySourceMatchesUpdate { get; init; }
    public bool? OverlayTranslationMatchesUpdate { get; init; }
    public long OverlayContentMismatches { get; init; }
    public DebugReason Overlay { get; init; } = DebugReason.NotMeasured;
    public bool? OverlayWindowVisible { get; init; }
    public string DisplayMode { get; init; } = "N/A";
    public long HttpAttempts { get; init; }
    public int TargetChanges { get; init; }
    public DebugReason LastError { get; init; } = DebugReason.None;
}
public sealed record DebugCycle(long Session, long Id, DateTimeOffset Started, string Status = "Provisional",
    int OcrCalls = 0, int AiRequests = 0, int Reuses = 0, int FullRetries = 0, int CacheHits = 0, int OcrCacheHits = 0)
{
    public OcrCacheMissCounts OcrCacheMisses {get;init;}=new();
    public long RecognitionInferences {get;init;}
    public long RecognitionCacheHits {get;init;}
    public OcrCacheMissCounts RecognitionCacheMisses {get;init;}=new();
}
public sealed record DebugEvent(DateTimeOffset Time, long Session, DebugCode Code, DebugReason Reason, DebugValues Values, DebugCycle? Cycle)
{ public string Display => $"{Time.ToLocalTime():HH:mm:ss.fff} {Code} / {Reason}"; }
public sealed record DebugSnapshot(DateTimeOffset Time, TimeSpan Duration, long Session, DebugValues Values,
    DebugCycle? Current, DebugCycle? Previous, IReadOnlyList<DebugCycle> Cycles, IReadOnlyList<DebugEvent> Events);

public sealed class DebugTelemetry
{
    private readonly object sync = new();
    private readonly Func<DateTimeOffset> now;
    private readonly DateTimeOffset started;
    private readonly List<DebugEvent> events = [];
    private readonly List<DebugCycle> cycles = [];
    private DebugValues values = new();
    private DebugCycle? current, confirmed;
    private long session, sequence;
    private string? acceptedFingerprint;
    private string? previousAiFingerprint;
    private string? previousResultSourceFingerprint, previousResultFingerprint;
    private DateTimeOffset? quietObserved;
    private bool sessionStopped;
    private DebugReason pendingError;
    private DateTimeOffset? sessionStarted;
    public DebugTelemetry(Func<DateTimeOffset>? clock = null) { now = clock ?? (() => DateTimeOffset.UtcNow); started = now(); }
    public static void EnableAttempts(IOcrService service) { try { service.CollectAttemptDiagnostics = true; } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
    private void Safe(Action action) { try { lock (sync) action(); } catch (Exception ex) when (ex is not OutOfMemoryException) { /* Telemetry never fails the product. */ } }
    // Model IDs are optional configuration, never accepted as arbitrary report text.
    public static string Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '+')
        && !value.Contains(':') /* local paths and URL schemes are not report identifiers */ ? value : "N/A";
    public long BeginSession(OcrOptions ocr, TranslationOptions translation, double settleMs)
    {
        long result = 0;
        Safe(() => {
            EndPendingCore(); session++; current = confirmed = null; acceptedFingerprint = previousAiFingerprint = null; quietObserved = null; sessionStopped = false;
            previousResultSourceFingerprint = previousResultFingerprint = null;
            sessionStarted = now(); pendingError = DebugReason.None;
            values = new DebugValues { CaptureMode = values.CaptureMode, Exclusion = values.Exclusion, Target = values.Target, TargetChanges = values.TargetChanges,
                OverlayWindowVisible = values.OverlayWindowVisible, DisplayMode = values.DisplayMode,
                Engine = Label(ocr.EngineId), OcrModel = Label(ocr.ModelPresetId),
                RequestedBackend = ocr.UseDirectMl ? "DirectML" : "CPU", ActualBackend = "N/A", Fallback = false,
                Provider = translation.Provider.ToString(), TranslationModel = translation.Provider == TranslationProvider.HyMtLocal ? translation.LocalModel.ToString() : Label(translation.Model),
                SettleMs = settleMs, RecognitionMode = values.RecognitionMode, ReadingMode = ocr.ReadingMode, TranslationBackend = values.TranslationBackend,
                StableMs = null, TranslationState = DebugReason.NotMeasured, LastError = DebugReason.None };
            Add(DebugCode.SESSION_STARTED, DebugReason.None); result = session;
        }); return result;
    }
    private void Add(DebugCode code, DebugReason reason, DebugCycle? eventCycle = null)
    {
        var time = now(); Trim(time);
        // Repeated polling states update values/counters, without evicting useful events.
        if (events.LastOrDefault() is { } last && last.Session == session && last.Code == code && last.Reason == reason
            && code is DebugCode.OCR_SKIPPED or DebugCode.OCR_FRAME_REUSED or DebugCode.WAIT_STABLE) return;
        events.Add(new(time, session, code, reason, values, eventCycle ?? current));
        if (events.Count > 50) events.RemoveAt(0);
    }
    private void Trim(DateTimeOffset time) { events.RemoveAll(e => time - e.Time > TimeSpan.FromMinutes(3)); }
    public void Record(long owner, DebugCode code, DebugReason reason, Func<DebugValues, DebugValues>? update = null, long cycle = 0) => Safe(() => {
        if (owner != 0 && owner != session) {
            if (code == DebugCode.STALE_RESULT_DISCARDED) { Trim(now()); events.Add(new(now(), owner, code, reason, new(), cycles.LastOrDefault(c => c.Session == owner))); if (events.Count > 50) events.RemoveAt(0); }
            return;
        }
        if (update is not null && !sessionStopped) values = update(values);
        if (!sessionStopped) {
            if (code==DebugCode.CAPTURE_FAILED) values=values with {FinalFrameAction=DebugFrameAction.CaptureUnavailable,ReuseReason=DebugReason.None,CaptureWait=CaptureWaitReason.None};
            else if (code==DebugCode.WAIT_STABLE) values=values with {FinalFrameAction=DebugFrameAction.ConfirmingText};
            else if (code==DebugCode.OCR_TEXT_REJECTED && reason is DebugReason.OcrFailure or DebugReason.Cancelled)
                values=values with {OcrInProgress=false,FinalFrameAction=DebugFrameAction.Watching};
            else if (code==DebugCode.OCR_SKIPPED && reason is DebugReason.TriggerWaiting or DebugReason.TriggerExpired)
                values=values with {FinalFrameAction=DebugFrameAction.WaitingForTrigger,ReuseReason=DebugReason.None};
        }
        if (code == DebugCode.OCR_TEXT_REJECTED && reason is DebugReason.OcrFailure or DebugReason.Cancelled) pendingError = reason;
        if (code == DebugCode.WAIT_STABLE) quietObserved = now();
        else if (code == DebugCode.OCR_TEXT_REJECTED) FreezeQuiet();
        Add(code, reason, cycle == 0 ? null : cycles.FirstOrDefault(c => c.Id == cycle));
    });
    // Compare actual subtitle control contents with this delivered update only.
    // Keep lengths/equality, never source/translation strings or hashes.
    public void OverlayApplied(long owner, bool visible, string? expectedSource, string? expectedTranslation,
        string actualSource, string actualTranslation) => Safe(() => {
        if (owner != session || sessionStopped) return;
        bool? sourceMatches = expectedSource is null ? null : actualSource == expectedSource;
        bool? translationMatches = expectedTranslation is null ? null : actualTranslation == expectedTranslation;
        values = values with {
            Overlay = visible ? DebugReason.Displayed : DebugReason.Hidden,
            OverlayExpectedSourceCharacters = expectedSource?.Length, OverlaySourceCharacters = actualSource.Length,
            OverlayExpectedTranslationCharacters = expectedTranslation?.Length, OverlayTranslationCharacters = actualTranslation.Length,
            OverlaySourceMatchesUpdate = sourceMatches, OverlayTranslationMatchesUpdate = translationMatches,
            OverlayContentMismatches = values.OverlayContentMismatches + (sourceMatches == false || translationMatches == false ? 1 : 0)
        };
        Add(DebugCode.OVERLAY_UPDATED, values.Overlay);
    });
    public void SetContext(Func<DebugValues, DebugValues> update) => Safe(() => values = update(values));
    public void MarkerLearning(long owner,MarkerLearningStatus status)=>Safe(()=> {
        if(owner!=session || sessionStopped)return;
        values=values with {MarkerLearningMode=status.Mode,LearnedMarkerPatterns=status.Confirmed,LearningMarkerCandidates=status.Candidates,
            LearningMarkerPages=status.BestPages,LearningMarkerAnimatedPages=status.AnimatedPages,LearnedMarkerExcluded=status.Excluded,
            LearnedMarkerWouldExclude=status.WouldExclude,MarkerLearningStorageError=status.StorageError,
            LearnedMarkerExcludedTotal=status.ExcludedTotal,LearnedMarkerExcludingOcrCalls=status.ExcludingOcrCalls,
            LearnedMarkerSkippedFrames=status.SkippedFrames,LearnedMarkerRecheckFrames=status.RecheckFrames,
            LearnedMarkerExtraBodyChecks=status.ExtraBodyChecks,LearnedMarkerBodyCacheHits=status.BodyCheckCacheHits,LearnedMarkerFastSamples=status.FastSamples,LearnedMarkerFastCycles=status.FastCycles,
            LearnedMarkerFramePreparations=status.FramePreparations,LearnedMarkerFramePreparationFailures=status.FramePreparationFailures,
            LearnedMarkerRecoveredCycles=status.RecoveredCycles,
            LearnedMarkerRecoveryFailure=status.RecoveryFailure,
            LearnedMarkerFrameNoisePixels=status.FrameNoisePixels,
            LearningMarkerValidatedCycles=status.ValidatedCycles,LearningMarkerBodyChangeDiscards=status.BodyChangeDiscards,
            LearningMarkerExpiredWatches=status.ExpiredWatches,
            LearnedMarkerFrameDecision=status.FrameDecision,LearnedMarkerFrameFailure=status.FrameFailure};
    });
    public void CapturePaused(long owner, CaptureWaitReason reason) => Safe(() => {
        if (owner != session || sessionStopped || reason is not (CaptureWaitReason.NotForeground or CaptureWaitReason.HiddenOrMinimized or CaptureWaitReason.Occluded or CaptureWaitReason.ScalingTransition or CaptureWaitReason.WindowBusy)) return;
        bool changed = values.CaptureStatus != DebugReason.Paused || values.CaptureWait != reason;
        values = values with { CaptureStatus = DebugReason.Paused, CaptureError = DebugReason.None, CaptureWait = reason,
            FinalFrameAction = DebugFrameAction.CapturePaused, ReuseReason = DebugReason.None, OcrScheduled = false };
        // A paused poll is not an OCR cycle or an error. Keep previous genuine errors as history.
        if (changed) Add(DebugCode.CAPTURE_PAUSED, DebugReason.Paused);
    });
    public void CaptureCompleted(long owner, int width, int height) => Safe(() => {
        if (owner != session || sessionStopped) return;
        bool resumed = values.CaptureStatus == DebugReason.Paused;
        bool changed = values.Width != width || values.Height != height || values.CaptureStatus != DebugReason.Completed;
        values = values with { Width = width, Height = height, CaptureStatus = DebugReason.Completed, CaptureError = DebugReason.None,
            CaptureWait = CaptureWaitReason.None, FinalFrameAction = values.FinalFrameAction == DebugFrameAction.CapturePaused ? DebugFrameAction.Watching : values.FinalFrameAction };
        if (changed) Add(resumed ? DebugCode.CAPTURE_RESUMED : DebugCode.CAPTURE_COMPLETED, DebugReason.Completed);
    });
    private void SaveCycle(DebugCycle cycle)
    {
        int i = cycles.FindIndex(c => c.Id == cycle.Id);
        if (i < 0) cycles.Add(cycle); else cycles[i] = cycle;
        if (cycles.Count > 50) cycles.RemoveAt(0);
        if (current?.Id == cycle.Id) current = cycle;
        if (confirmed?.Id == cycle.Id) confirmed = cycle;
    }
    private void CandidateCore()
    {
        if (current is { Status: "Provisional" }) return;
        pendingError = DebugReason.None;
        current = new(session, ++sequence, now()); SaveCycle(current); Add(DebugCode.CYCLE_STARTED, DebugReason.TextCandidateChanged);
    }
    public void Candidate(long owner) => Safe(() => { if (owner == session) CandidateCore(); });
    public void OcrCacheMissed(long owner,OcrCacheMissReason reason)=>Safe(()=> {
        if(owner!=session || sessionStopped)return;
        CandidateCore();current=current! with {OcrCacheMisses=current!.OcrCacheMisses.Count(reason)};SaveCycle(current);
        values=values with {OcrResultCacheLastMiss=reason,OcrResultCacheMisses=values.OcrResultCacheMisses.Count(reason)};
    });
    public void OcrStarted(long owner, DebugReason kind) => Safe(() => {
        if (owner != session) return;
        if (!sessionStopped || current is null) CandidateCore();
        FreezeQuiet();
        current = current! with { OcrCalls = current!.OcrCalls + 1, FullRetries = current.FullRetries + (kind is DebugReason.NvlFullRetry or DebugReason.RoiFullRetry or DebugReason.AdvFullRetry or DebugReason.AdvRecovery or DebugReason.StaticLabelRecovery ? 1 : 0) };
        SaveCycle(current); values = values with { LogicalOcrCalls = values.LogicalOcrCalls + 1, Search = kind, OcrScheduled = false,
            OcrInProgress=!sessionStopped,FinalFrameAction=sessionStopped ? DebugFrameAction.Stopped : DebugFrameAction.Recognizing,ReuseReason=DebugReason.None };
        if (kind is DebugReason.NvlFullRetry or DebugReason.RoiFullRetry or DebugReason.AdvFullRetry or DebugReason.AdvRecovery or DebugReason.StaticLabelRecovery) Add(DebugCode.OCR_FULL_RETRY, kind);
        Add(DebugCode.OCR_STARTED, kind);
    });
    public void OcrCompleted(long owner, OcrResult result, double ms) => Safe(()=> {
        if(owner!=session)return;
        if(current is not null && result.RecognitionCache is {} measurement) {
            current=current with {RecognitionInferences=current.RecognitionInferences+measurement.Inferences,
                RecognitionCacheHits=current.RecognitionCacheHits+measurement.Hits,
                RecognitionCacheMisses=current.RecognitionCacheMisses.Add(measurement.Misses)};SaveCycle(current);
        }
        Record(owner, DebugCode.OCR_COMPLETED, DebugReason.Completed, v => v with {
        OcrInProgress=false, FinalFrameAction=DebugFrameAction.OcrCompleted,
        ActualBackend = result.Execution.Backend is "CPU" or "DirectML" ? result.Execution.Backend : "N/A", Fallback = result.Execution.IsFallback,
        GpuAttempts = result.Attempts?.Gpu, CpuAttempts = result.Attempts?.Cpu,
        RecognitionCacheMeasured=result.RecognitionCache is not null,
        RecognitionInferences=v.RecognitionInferences+(result.RecognitionCache?.Inferences??0),
        RecognitionCacheHits=v.RecognitionCacheHits+(result.RecognitionCache?.Hits??0),
        RecognitionCacheLastMiss=result.RecognitionCache is {LastMiss:not OcrCacheMissReason.None} stats ? stats.LastMiss : v.RecognitionCacheLastMiss,
        RecognitionCacheMisses=result.RecognitionCache is {} measurements ? v.RecognitionCacheMisses.Add(measurements.Misses) : v.RecognitionCacheMisses,
        OcrMs = ms, DetectionMs = result.DetectionTime.TotalMilliseconds, RecognitionMs = result.RecognitionTime.TotalMilliseconds,
        PostprocessMs = result.PostprocessTime.TotalMilliseconds, DetectedRegions = result.DetectedRegions, AcceptedLines = result.Lines.Count,
        MarkerObservations=result.MarkerDiagnostics is null ? null : result.MarkerObservations,
        MarkerDiagnostics=result.MarkerDiagnostics is null ? null : Array.AsReadOnly(result.MarkerDiagnostics.TakeLast(OcrMarkerDiagnostics.Limit).ToArray()) });
    });
    private void MergeCore(DebugReason reason)
    {
        if (confirmed is null || current is null || current.Id == confirmed.Id) return;
        var pending = current; cycles.RemoveAll(c => c.Id == pending.Id);
        current = confirmed with { OcrCalls = confirmed.OcrCalls + pending.OcrCalls, FullRetries = confirmed.FullRetries + pending.FullRetries,
            Reuses = confirmed.Reuses + pending.Reuses, AiRequests = confirmed.AiRequests + pending.AiRequests, CacheHits = confirmed.CacheHits + pending.CacheHits,
            OcrCacheHits = confirmed.OcrCacheHits + pending.OcrCacheHits,
            OcrCacheMisses=confirmed.OcrCacheMisses.Add(pending.OcrCacheMisses),
            RecognitionInferences=confirmed.RecognitionInferences+pending.RecognitionInferences,
            RecognitionCacheHits=confirmed.RecognitionCacheHits+pending.RecognitionCacheHits,
            RecognitionCacheMisses=confirmed.RecognitionCacheMisses.Add(pending.RecognitionCacheMisses) };
        confirmed = current; SaveCycle(current); Add(DebugCode.CYCLE_MERGED, reason);
    }
    public void SameSource(long owner) => Safe(() => { if (owner == session) MergeCore(DebugReason.SameSource); });
    public void OcrCacheReused(long owner, OcrResult result, DebugReason search) => Safe(() => {
        if(owner != session || sessionStopped)return;
        CandidateCore();
        current = current! with { OcrCacheHits = current!.OcrCacheHits + 1 };
        SaveCycle(current);
        values = values with { OcrResultCacheHits = values.OcrResultCacheHits + 1, Search = search,
            OcrInProgress = false, OcrScheduled = false, FinalFrameAction = DebugFrameAction.Reused,
            ReuseReason = DebugReason.ExactOcrCache, GateScope = "Exact-OCR-cache",
            ActualBackend = result.Execution.Backend is "CPU" or "DirectML" ? result.Execution.Backend : "N/A",
            Fallback = result.Execution.IsFallback, GpuAttempts = 0, CpuAttempts = 0,
            OcrMs = 0, DetectionMs = 0, RecognitionMs = 0, PostprocessMs = 0,
            DetectedRegions = result.DetectedRegions, AcceptedLines = result.Lines.Count,
            MarkerObservations = result.MarkerDiagnostics is null ? null : result.MarkerObservations,
            MarkerDiagnostics = result.MarkerDiagnostics is null ? null : Array.AsReadOnly(result.MarkerDiagnostics.TakeLast(OcrMarkerDiagnostics.Limit).ToArray()) };
        Add(DebugCode.OCR_RESULT_CACHE_HIT, DebugReason.ExactOcrCache);
    });
    public long Accept(long owner, bool same, bool continuation, bool pageBreak, bool nvl, string? fingerprint = null, bool repeatedText = false)
    {
        long result = 0;
        Safe(() => {
            if (owner != session) return; CandidateCore();
            FreezeQuiet();
            bool fingerprintSame = fingerprint is not null && fingerprint == acceptedFingerprint;
            if (!pageBreak && (same || fingerprintSame || continuation)) MergeCore(continuation ? DebugReason.AdvContinuation : DebugReason.SameSource);
            else if (confirmed is not null && confirmed.Id != current!.Id) { SaveCycle(confirmed with { Status = "Completed" }); Add(DebugCode.CYCLE_COMPLETED, DebugReason.Accepted, confirmed); }
            current = current! with { Status = "Accepted" }; confirmed = current; SaveCycle(current);
            values=values with {FinalFrameAction=DebugFrameAction.Accepted,OcrInProgress=false};
            acceptedFingerprint = fingerprint;
            Add(DebugCode.OCR_TEXT_ACCEPTED, repeatedText ? DebugReason.RepeatedTextConfirmed : pageBreak ? DebugReason.NvlPage : nvl ? DebugReason.NvlAppend : DebugReason.Accepted); result = current.Id;
        }); return result;
    }
    public void Reuse(long owner, DebugReason reason) => Safe(() => {
        if (owner != session || sessionStopped) return;
        values = values with { FrameReuses = values.FrameReuses + (reason == DebugReason.VerifiedFrame ? 1 : 0),
            AnimationReuses = values.AnimationReuses + (reason == DebugReason.RepeatedAnimation ? 1 : 0), ReuseReason = reason, OcrScheduled = false,
            FinalFrameAction=DebugFrameAction.Reused,
            GateReason = reason, GateScope = reason == DebugReason.VerifiedFrame ? "Full-frame-cache" : reason==DebugReason.LearnedMarker ? "Learned-marker" : "Animation-guard" };
        if (current is not null) { current = current with { Reuses = current.Reuses + 1 }; SaveCycle(current); }
        Add(DebugCode.OCR_FRAME_REUSED, reason);
    });
    public void Ai(long owner, long cycle, bool cache, string? sourceFingerprint=null,int? sourceCharacters=null) => Safe(() => {
        if (owner != session) return;
        if(!cache && sourceFingerprint is not null) {
            values=values with {LastAiSourceCharacters=sourceCharacters,LastAiSourceRelation=previousAiFingerprint is null ? DebugAiSourceRelation.First
                : previousAiFingerprint==sourceFingerprint ? DebugAiSourceRelation.SameAsPrevious : DebugAiSourceRelation.DifferentFromPrevious};
            previousAiFingerprint=sourceFingerprint;
        }
        var found = cycles.FirstOrDefault(c => c.Id == cycle);
        if (found is not null) SaveCycle(found with { AiRequests = found.AiRequests + (cache ? 0 : 1), CacheHits = found.CacheHits + (cache ? 1 : 0) });
        values = values with { AiRequests = values.AiRequests + (cache ? 0 : 1), TranslationCacheHits = values.TranslationCacheHits + (cache ? 1 : 0),
            TranslationState = sessionStopped ? DebugReason.Stopped : cache ? DebugReason.Cache : DebugReason.Ready };
        Add(cache ? DebugCode.TRANSLATION_CACHE_HIT : DebugCode.TRANSLATION_REQUESTED, cache ? DebugReason.Cache : DebugReason.None, cycles.FirstOrDefault(c => c.Id == cycle));
    });
    // Successful, still-current provider responses only, before NVL prefix composition.
    // Hashes remain private in RAM; reports expose counts and equality categories only.
    public void AiResponse(long owner, string sourceFingerprint, string resultFingerprint, int sourceCharacters, int resultCharacters, int resultLines) => Safe(() => {
        if (owner != session || sessionStopped) return;
        bool sameSource = sourceFingerprint == previousResultSourceFingerprint, sameResult = resultFingerprint == previousResultFingerprint;
        var relation = previousResultFingerprint is null ? DebugAiResultRelation.First
            : sameSource ? sameResult ? DebugAiResultRelation.SameSourceSameResult : DebugAiResultRelation.SameSourceDifferentResult
            : sameResult ? DebugAiResultRelation.DifferentSourceSameResult : DebugAiResultRelation.DifferentSourceDifferentResult;
        values = values with { LastAiResultSourceCharacters = sourceCharacters, LastAiResultCharacters = resultCharacters, LastAiResultLines = resultLines,
            LastAiResultRelation = relation, AiRepeatedResultForChangedSource = values.AiRepeatedResultForChangedSource + (relation == DebugAiResultRelation.DifferentSourceSameResult ? 1 : 0) };
        previousResultSourceFingerprint = sourceFingerprint; previousResultFingerprint = resultFingerprint;
    });
    private void EndPendingCore() { if (current is { Status: "Provisional" }) { current = current with { Status = pendingError == DebugReason.OcrFailure ? "Failed" : pendingError == DebugReason.Cancelled ? "Cancelled" : "Unconfirmed" }; SaveCycle(current); Add(DebugCode.CYCLE_COMPLETED, DebugReason.Rejected); } }
    public void EndPending(long owner) => Safe(() => { if (owner == session) EndPendingCore(); });
    public void Stop(long owner) => Safe(() => { if (owner != session) return; EndPendingCore();
        FreezeQuiet(); sessionStopped = true; values = values with { CaptureStatus = DebugReason.Stopped, Trigger = DebugReason.Stopped, Overlay = DebugReason.Stopped, TranslationState = DebugReason.Stopped,
            FinalFrameAction=DebugFrameAction.Stopped,OcrInProgress=false,OcrScheduled=false,ReuseReason=DebugReason.None,CaptureWait=CaptureWaitReason.None }; Add(DebugCode.SESSION_STOPPED, DebugReason.Stopped); });
    private double? QuietElapsed(DateTimeOffset time) => quietObserved is { } since
        ? Math.Min(values.SettleMs ?? double.MaxValue, (values.StableMs ?? 0) + Math.Max(0, (time - since).TotalMilliseconds)) : values.StableMs;
    private void FreezeQuiet() { values = values with { StableMs = QuietElapsed(now()) }; quietObserved = null; }
    public DebugSnapshot Snapshot()
    {
        lock (sync) {
            var time = now(); Trim(time);
            return new(time, time - (sessionStarted ?? started), session, values with { StableMs = QuietElapsed(time) }, current,
                cycles.LastOrDefault(c => c.Session == session && c.Id != current?.Id), Array.AsReadOnly(cycles.ToArray()), Array.AsReadOnly(events.AsEnumerable().Reverse().ToArray()));
        }
    }
}
