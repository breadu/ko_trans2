using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using KoTrans.Core;
using KoTrans.Ocr;

namespace KoTrans.Infrastructure;

// One monitor and one serial OCR worker, with independently cancelable AI requests.
// Channel capacity is one: obsolete frames never form a backlog; only changed OCR text supersedes AI.
public sealed partial class TranslationPipeline : IAsyncDisposable
{
    // Keep a complete default monitor interval (400 ms) inside the text quiet wait.
    public static TimeSpan DefaultSettle { get; } = TimeSpan.FromMilliseconds(500);

    private sealed record Job(long Version, ImageFrame? Frame, string? Text, bool Manual, bool Force, double CaptureMs, long Started, bool Probe = false, long Observation = 0, bool FullSearch = false, bool Nvl = false, bool PageBreak = false, long TriggerEpoch = 0, long DiagnosticCycle = 0, long CaptureEpoch = 0, bool VerifyAcceptedSource = false);
    private readonly IOcrService ocr;
    private readonly ITranslationService? ai;
    private readonly bool translationEnabled;
    private readonly Func<CancellationToken, Task<ImageFrame>> capture;
    private readonly Func<long, string, CancellationToken, Task>? onOcrAccepted;
    private readonly TranslationOptions options;
    private readonly OcrOptions ocrOptions;
    private readonly TranslationSession context;
    private readonly string prompt;
    private readonly IReadOnlyList<CharacterEntry> characters;
    private readonly GameQaRecorder? gameQa;
    private readonly GameQaProfile? qaProfile;
    private readonly TimeSpan interval, settle, triggerWindow;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Job> jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly SemaphoreSlim monitorWake = new(0, 1);
    private bool watchContinuously, triggerArmed;
    private long triggerSince;
    private void FinishTrigger(Job job)
    {
        if (!watchContinuously && job.TriggerEpoch != 0 && job.TriggerEpoch == inputEpoch)
            triggerArmed = false;
    }
    // Called under sync. A later input supersedes unaccepted OCR, not an already accepted AI request.
    private bool IsCurrentOcrJob(Job job) => job.Version == Version && (job.TriggerEpoch == 0 || job.TriggerEpoch == inputEpoch)
        && (job.Manual || job.Frame is null || job.CaptureEpoch == captureEpoch);
    private readonly object sync = new();
    private readonly RealtimeFrameGate frames = new();
    private readonly RepeatedAnimationGuard animations = new();
    internal bool SuppressRepeatedAnimations { get; init; } = true;
    private readonly OcrRegionTracker ocrRegion = new();
    private readonly AdvTextRecovery advRecovery = new();
    private readonly NvlOcrTracker nvlOcr = new();
    private readonly NvlTranslationPage nvlPage = new();
    private readonly RepeatedOcrCandidate repeatedCandidate = new();
    private long inputEpoch;
    private long captureEpoch;
    private Job? activeOcrJob;
    private readonly TextDuplicateFilter duplicate = new();
    private CancellationTokenSource? current, translationCancellation;
    private readonly List<Task> translations = [];
    private string? translationText, translationFailure, translationOverlayFailure;
    // Stable visual source is separate from the canonical source/cache key.
    private string? translationOriginal, recognizedOriginal;
    private PipelineState translationState = PipelineState.Stopped;
    // Bounded fingerprints only: no additional captured images are retained.
    private const int VerifiedFrameCapacity = 32;
    private readonly HashSet<string> verifiedFrames = [];
    private readonly Queue<string> verifiedOrder = new();
    private long visualRevision, candidateSince;
    private Guid translationQaId;
    private Task? worker, monitor, disposal;
    private int manualOperations;
    private readonly TaskCompletionSource manualDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImageFrame? recognizedFrame;
    private Job? settlingJob;
    private long markerVerifiedAt,markerVerifiedVersion,markerVerifiedTrigger,markerVerifiedRevision;
    private bool recognizedFrameHasFullCoverage;
    private string? recognizedText;
    public bool TryGetRecognized(ImageFrame frame, out string text)
    { lock (sync) { text = recognizedText ?? ""; return text.Length > 0 && recognizedFrame is not null && StudyFrame.SamePixels(recognizedFrame, frame); } }
    private long version;
    private bool stopped, started, unstable, probePending, fullProbePending;
    private int unstableTicks;
    private int ocrCount, aiCount, httpAttempts, cacheHits;
    private string? clipboardText;
    public bool IsRunning => started && !stopped;
    public bool IsTranslating { get { lock (sync) return IsRunning && translationState == PipelineState.Translating; } }
    public long Version => Interlocked.Read(ref version);
    public event Action<PipelineUpdate>? Updated;
    public event Action<long, bool>? ActivityChanged;
    // Read under sync; canceled or superseded work must not keep the indicator running.
    private bool IsBusy => !stopped && (triggerArmed && (recognizedText is null || recognizedText != translationText) || current is { IsCancellationRequested: false }
        || translationState == PipelineState.Translating
        || jobs.Reader.TryPeek(out var queued) && IsCurrentOcrJob(queued));
    private void PublishActivity()
    {
        lock (sync) { if (!stopped) ActivityChanged?.Invoke(Version, IsBusy); }
    }
    public TranslationPipeline(IOcrService ocr, ITranslationService? ai, Func<CancellationToken, Task<ImageFrame>> capture,
        OcrOptions ocrOptions, TranslationOptions options, TranslationSession context, string prompt,
        TimeSpan? interval = null, TimeSpan? settle = null, GameQaRecorder? gameQa = null, GameQaProfile? qaProfile = null, bool translationEnabled = true, TimeSpan? triggerWindow = null,
        Func<long, string, CancellationToken, Task>? onOcrAccepted = null, DebugTelemetry? diagnostics = null, IReadOnlyList<CharacterEntry>? characters = null)
    {
        this.ocr = ocr; this.ai = ai; this.capture = capture; this.ocrOptions = ocrOptions; this.options = options with { };
        this.translationEnabled = translationEnabled;
        this.characters = characters?.ToArray() ?? [];
        this.onOcrAccepted = onOcrAccepted;
        telemetry = diagnostics;
        if (diagnostics is not null) DebugTelemetry.EnableAttempts(ocr);
        if (translationEnabled) ArgumentNullException.ThrowIfNull(ai);
        this.gameQa = gameQa; this.qaProfile = qaProfile;
        this.context = context; this.prompt = prompt;
        this.interval = interval ?? TimeSpan.FromMilliseconds(400); this.settle = settle ?? DefaultSettle;
        this.triggerWindow = triggerWindow ?? TimeSpan.FromSeconds(12);
        if (this.triggerWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(triggerWindow));
    }
    public void Start(bool watch, string? initialText = null)
    {
        if (started || stopped) throw new InvalidOperationException("Create a new pipeline to restart.");
        diagnosticSession = telemetry?.BeginSession(ocrOptions, options, settle.TotalMilliseconds) ?? 0;
        PublishMarkerDiagnostics();
        started = true; Emit(Version, PipelineState.Starting, translationEnabled ? "번역 준비 중…" : "원문 인식 준비 중…");
        // Queue an already accepted subtitle before watching can enqueue a new frame.
        // Used when enabling translation after an OCR-only session; no OCR/API force retry.
        if (!string.IsNullOrWhiteSpace(initialText)) SubmitClipboard(initialText, manual: true);
        worker = Task.Run(WorkAsync);
        watchContinuously = watch;
        if (!watch) telemetry?.Record(diagnosticSession, DebugCode.OCR_SKIPPED, DebugReason.TriggerWaiting,
            v => v with { Trigger = DebugReason.TriggerWaiting });
        monitor = Task.Run(MonitorAsync);
        Emit(Version, PipelineState.Watching, watch ? (translationEnabled ? "새 대사 대기 중…" : "새 원문 대기 중…") : "입력 대기 중");
    }
    private static string Fingerprint(ImageFrame frame) =>
        $"{frame.Width}:{frame.Height}:{frame.Stride}:{frame.PixelFormat}:{Convert.ToHexString(SHA256.HashData(frame.Pixels.Span))}";
    // Called under sync. Recognition/profile/language belong to this pipeline instance.
    private bool IsVerifiedFrame(ImageFrame frame) => current is null && recognizedText is not null && recognizedText == translationText && recognizedOriginal == translationOriginal
        && verifiedFrames.Count > 0 && verifiedFrames.Contains(Fingerprint(frame));
    private void RememberVerifiedFrame(ImageFrame? frame, OcrResult? recognized, bool fullCoverage)
    {
        if (frame is null) return;
        if (SuppressRepeatedAnimations && recognized is not null) animations.Observe(frame, recognized, translationOriginal ?? translationText ?? "");
        if (!fullCoverage) return; // A partial OCR cannot certify unseen pixels.
        var key = Fingerprint(frame);
        if (!verifiedFrames.Add(key)) return;
        verifiedOrder.Enqueue(key);
        if (verifiedOrder.Count > VerifiedFrameCapacity) verifiedFrames.Remove(verifiedOrder.Dequeue());
    }
    // A complete previous observation plus an actually scanned rectangle can certify
    // this frame only when every outside pixel still matches. Reuse the existing
    // recognizedFrame; no additional screenshot/history images are retained.
    internal static bool OutsideSearchUnchanged(ImageFrame before, ImageFrame after, PixelRect area)
    {
        if (before.Width != after.Width || before.Height != after.Height || before.PixelFormat != after.PixelFormat
            || !area.IsValid || area.X < 0 || area.Y < 0 || area.X + (long)area.Width > after.Width || area.Y + (long)area.Height > after.Height)
            return false;
        for (int y = 0; y < after.Height; y++) {
            var a = before.Pixels.Span.Slice(y * before.Stride, before.Width * 4);
            var b = after.Pixels.Span.Slice(y * after.Stride, after.Width * 4);
            if (y >= area.Y && y < area.Y + area.Height) {
                if (!a[..(area.X * 4)].SequenceEqual(b[..(area.X * 4)])
                    || !a[((area.X + area.Width) * 4)..].SequenceEqual(b[((area.X + area.Width) * 4)..])) return false;
            }
            else if (!a.SequenceEqual(b)) return false;
        }
        return true;
    }
    private void RestoreKnownState(long request, string text, string notice = "", PipelineStatusArea recoveredAreas = PipelineStatusArea.None)
    {
        // A duplicate recognition must neither leave Recognizing latched nor pretend an AI error succeeded.
        if (translationText != text) return;
        if (translationState == PipelineState.Error && translationFailure is not null)
            Emit(request, PipelineState.Error, translationFailure, recognizedText == text ? text : null, failureArea: PipelineStatusArea.Translation, recoveredAreas: recoveredAreas, overlayMessage: translationOverlayFailure);
        else if (translationState == PipelineState.Translating)
            Emit(request, PipelineState.Translating, notice + "AI 번역 중…", recognizedText == text ? text : null, recoveredAreas: recoveredAreas);
        else if (translationState == PipelineState.Watching) {
            if (!translationEnabled)
                Emit(request, PipelineState.Watching, notice + "원문 인식 완료 · 다음 입력 대기", recognizedText == text ? text : null, recoveredAreas: recoveredAreas);
            else if (context.TryLast(text, out var cached))
                // Restore the pair only while OCR still matches the accepted source.
                // A blank/new candidate must not re-publish an older completed answer.
                Emit(request, PipelineState.Watching, notice + "번역 완료 · 다음 입력 대기", recognizedText == text ? text : null,
                    recognizedText == text ? cached : null, recoveredAreas: recoveredAreas);
        }
    }
    private long Invalidate()
    {
        lock (sync) { current?.Cancel(); translationCancellation?.Cancel(); nvlPage.Reset(); animations.Reset(); repeatedCandidate.Reset(); translationText = translationOriginal = translationFailure = translationOverlayFailure = null; translationState = PipelineState.Stopped; return Interlocked.Increment(ref version); }
    }
    private async Task<ImageFrame> CaptureAsync(CancellationToken token)
    {
        await captureGate.WaitAsync(token).ConfigureAwait(false);
        try {
            var frame = await capture(token).ConfigureAwait(false);
            telemetry?.CaptureCompleted(diagnosticSession, frame.Width, frame.Height);
            return frame;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (CaptureUnavailableException ex) when (ex.IsExpectedWait) {
            telemetry?.CapturePaused(diagnosticSession, ex.WaitReason); throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            telemetry?.Record(diagnosticSession, DebugCode.CAPTURE_FAILED, ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.CaptureUnavailable,
                v => v with { CaptureStatus = ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.CaptureUnavailable,
                    CaptureError = ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.CaptureUnavailable,
                    LastError = ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.CaptureUnavailable,
                    CaptureWait = CaptureWaitReason.None }); throw;
        }
        finally { captureGate.Release(); }
    }
    public Task RequestManualAsync(bool force = false, ImageFrame? image = null) => RequestImageAsync(true, force, image);
    // Input arms the same frame/text stabilization path as automatic watching. Idle trigger
    // mode does not capture; repeated inputs extend one session instead of creating workers.
    public Task RequestTriggeredAsync()
    {
        lock (sync) {
            if (!IsRunning) return Task.CompletedTask;
            inputEpoch++;
            repeatedCandidate.Reset();
            telemetry?.Record(diagnosticSession, DebugCode.INPUT_TRIGGERED, DebugReason.TriggerReceived, v => v with { Trigger = DebugReason.TriggerReceived });
            if (!watchContinuously) {
                triggerArmed = true; triggerSince = Stopwatch.GetTimestamp();
                frames.Reset(); animations.Reset(); unstable = probePending = fullProbePending = false; unstableTicks = 0;
                if (monitorWake.CurrentCount == 0) monitorWake.Release();
                Emit(Version, PipelineState.Watching, "입력 감지 · 문장 안정화 확인 중…");
                PublishActivity();
            }
        }
        return Task.CompletedTask;
    }
    private async Task RequestImageAsync(bool manual, bool force, ImageFrame? image)
    {
        long request;
        lock (sync) { if (!IsRunning) return; manualOperations++; inputEpoch++; if (manual) triggerArmed = false; request = manual ? Invalidate() : Version; }
        var begin = Stopwatch.GetTimestamp(); var clock = Stopwatch.StartNew();
        try
        {
            var frame = image ?? await CaptureAsync(lifetime.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (!IsRunning || request != Version) return;
                if (!manual && IsVerifiedFrame(frame)) { telemetry?.Reuse(diagnosticSession, DebugReason.VerifiedFrame); RestoreKnownState(request, recognizedText!, recoveredAreas: PipelineStatusArea.Capture); return; }
                if (manual) { frames.AcceptManual(frame); unstable = probePending = fullProbePending = false; } else visualRevision++;
                jobs.Writer.TryWrite(new(request, frame, null, manual, force, clock.Elapsed.TotalMilliseconds, begin, Observation: visualRevision, CaptureEpoch: captureEpoch));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ReportCaptureState(request, ex); }
        finally { lock (sync) { if (--manualOperations == 0 && stopped) manualDrained.TrySetResult(); } }
    }
    public void SubmitClipboard(string text, bool manual = false, bool force = false)
    {
        text = TranslationPrompt.Normalize(text);
        lock (sync)
        {
            if (!IsRunning || (!manual && text == clipboardText)) return;
            gameQa?.Clear(); clipboardText = text;
            // Even rejected/empty NEW text supersedes the previous request. Invalidate
            // before reporting it so a provider ignoring cancellation cannot publish A over B.
            var request = Invalidate();
            if (text.Length == 0)
            { Emit(request, PipelineState.Watching, "클립보드가 비어 있습니다. 게임 대사를 복사하세요.", "", "", important: true, failureArea: PipelineStatusArea.Input); return; }
            if (text.Length > 16000)
            { Emit(request, PipelineState.Error, translationEnabled ? "클립보드는 16,000자 이하로 번역하세요." : "클립보드 원문은 16,000자 이하로 복사하세요.", "", "", failureArea: PipelineStatusArea.Input); return; }
            jobs.Writer.TryWrite(new(request, null, text, manual, force, 0, Stopwatch.GetTimestamp()));
        }
    }
    private async Task MonitorAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                bool inspect;
                lock (sync) {
                    if (triggerArmed && Stopwatch.GetElapsedTime(triggerSince) > triggerWindow) {
                        telemetry?.EndPending(diagnosticSession);
                        telemetry?.Record(diagnosticSession, DebugCode.OCR_SKIPPED, DebugReason.TriggerExpired, v => v with { Trigger = DebugReason.TriggerWaiting });
                        triggerArmed = false; inputEpoch++; current?.Cancel();
                        if (translationText is not null && recognizedText == translationText)
                            RestoreKnownState(Version, translationText);
                        else Emit(Version, PipelineState.Watching, "새 대사를 찾지 못했습니다. 다음 입력에 다시 시도합니다.", important: true, failureArea: PipelineStatusArea.Input);
                        PublishActivity();
                    }
                    inspect = watchContinuously || triggerArmed;
                }
                if (!inspect) { await monitorWake.WaitAsync(lifetime.Token).ConfigureAwait(false); continue; }
                var observedVersion = Version;
                long observedEpoch;
                lock (sync) observedEpoch = inputEpoch;
                try
                {
                    var begin = Stopwatch.GetTimestamp(); var clock = Stopwatch.StartNew();
                    var frame = await CaptureAsync(lifetime.Token).ConfigureAwait(false);
                    lock (sync)
                    {
                        // A manual request arriving while capture was in flight takes priority.
                        if (observedVersion == Version && (watchContinuously || triggerArmed && observedEpoch == inputEpoch)
                            && !QueueSourceVerification(frame, begin, clock.Elapsed.TotalMilliseconds))
                        {
                            var markerDecision=MarkerFrameDecision.Unavailable;
                            bool waitingForCandidate=settlingJob is {} waiting && ReferenceEquals(waiting.Frame,recognizedFrame)
                                && IsCurrentOcrJob(waiting) && current is {IsCancellationRequested:false};
                            bool sourceAccepted=recognizedText is not null && recognizedText==translationText && recognizedOriginal==translationOriginal;
                            if((waitingForCandidate || current is null && !jobs.Reader.TryPeek(out _) && sourceAccepted)
                                && recognizedFrame is not null && recognizedText is not null && ocr is ILearnedMarkerFrameGate markerGate) {
                                markerDecision=markerGate.EvaluateMarkerFrame(recognizedFrame,frame);PublishMarkerDiagnostics();
                            }
                            if(markerDecision is MarkerFrameDecision.Reused or MarkerFrameDecision.Unchanged) {
                                unstable=probePending=fullProbePending=false;unstableTicks=0;
                                if(markerDecision==MarkerFrameDecision.Reused)telemetry?.Reuse(diagnosticSession,DebugReason.LearnedMarker);
                                else telemetry?.Record(diagnosticSession,DebugCode.OCR_SKIPPED,DebugReason.Unchanged,
                                    v=>v with {FinalFrameAction=DebugFrameAction.Reused,OcrScheduled=false,ReuseReason=DebugReason.LearnedMarker,GateScope="Learned-marker"});
                                markerVerifiedAt=begin;markerVerifiedVersion=Version;markerVerifiedTrigger=watchContinuously?0:observedEpoch;
                                markerVerifiedRevision=visualRevision;
                                // A known decoration may leave the text quiet
                                // clock running, but cannot publish a candidate.
                                if(!waitingForCandidate && sourceAccepted)RestoreKnownState(Version,recognizedText!, recoveredAreas: PipelineStatusArea.Capture);
                            } else {
                            var animation = SuppressRepeatedAnimations && recognizedText is not null && recognizedText == translationText && recognizedOriginal == translationOriginal
                                ? animations.Evaluate(frame) : AnimationFrameDecision.Unknown;
                            if (animation == AnimationFrameDecision.Reuse) {
                                telemetry?.Reuse(diagnosticSession, DebugReason.RepeatedAnimation);
                                // Decoration-only pixels do not invalidate the text quiet interval or enqueue OCR.
                                if (current is null && !jobs.Reader.TryPeek(out _)) RestoreKnownState(Version, recognizedText!, recoveredAreas: PipelineStatusArea.Capture);
                            }
                            else {
                                var decision = animation == AnimationFrameDecision.Verify ? FrameDecision.Probe : frames.Evaluate(frame);
                                ObserveGate(decision);
                                if (animation == AnimationFrameDecision.Verify) fullProbePending = true;
                                if (decision != FrameDecision.Unchanged) visualRevision++;
                                if (decision == FrameDecision.Probe && frames.ProbeOutside) fullProbePending=true;
                                // Pixels moving is not proof that the dialogue changed. Only accepted OCR text
                                // supersedes paid work. Capacity-one queue always keeps the newest candidate.
                                void Queue(bool probe) {
                                    telemetry?.Candidate(diagnosticSession); ObserveGate(decision, true);
                                    probePending = false;
                                    jobs.Writer.TryWrite(new(Version, frame, null, false, false, clock.Elapsed.TotalMilliseconds, begin, probe, visualRevision, fullProbePending, TriggerEpoch: watchContinuously ? 0 : observedEpoch, CaptureEpoch: captureEpoch));
                                    fullProbePending=false;
                                }
                                // Exact full-frame fingerprints were actually OCR-verified as this source.
                                // A periodic decoration check still applies to every unseen fingerprint.
                                if (decision != FrameDecision.Unchanged && IsVerifiedFrame(frame)
                                    && (watchContinuously || Stopwatch.GetElapsedTime(triggerSince) >= settle))
                                {
                                    // Cursor/animation returned to a frame already recognized as the current text.
                                    frames.AcceptManual(frame); unstable = probePending = fullProbePending = false; unstableTicks = 0;
                                    telemetry?.Reuse(diagnosticSession, DebugReason.VerifiedFrame);
                                    RestoreKnownState(Version, recognizedText!, recoveredAreas: PipelineStatusArea.Capture);
                                    // The previous dialogue may still be visible before a delayed effect.
                                    // A duplicate is not completion of this input's new-dialogue search.
                                }
                                else if (decision == FrameDecision.Probe || probePending)
                                {
                                    probePending = true;
                                    if (current is null && !jobs.Reader.TryPeek(out _)) Queue(true);
                                }
                                else if (decision == FrameDecision.Stabilizing)
                                {
                                    if (!unstable && translationText is null) Emit(Version, PipelineState.Watching, "새 문장 확인 중…");
                                    unstable = true;
                                    // At startup there is no text ROI yet. Animated scenery must not starve OCR.
                                    if (++unstableTicks >= 3) {
                                        unstableTicks = 0;
                                        if (current is null && !jobs.Reader.TryPeek(out _)) Queue(true);
                                        else probePending = true;
                                    }
                                }
                                else if (decision == FrameDecision.Ready)
                                {
                                    unstable = false; unstableTicks = 0; Queue(false);
                                }
                                else { unstable = false; unstableTicks = 0; }
                            }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lock (sync) {
                        // An older capture must not cancel newer manual OCR or overwrite its status.
                        if (observedVersion == Version && observedEpoch == inputEpoch && !stopped) {
                            // Retire unaccepted automatic frames, including queued work. Accepted
                            // AI/results and manual/clipboard requests retain their own lifetime.
                            captureEpoch++; repeatedCandidate.Reset();
                            if (activeOcrJob is { Manual: false, Frame: not null }) current?.Cancel();
                            frames.Reset(); animations.Reset(); unstable = probePending = fullProbePending = false; unstableTicks = 0;
                            markerVerifiedAt = 0;
                            (ocr as ILearnedMarkerFrameGate)?.ResetMarkerFrames();
                            PublishMarkerDiagnostics();
                            ReportCaptureState(observedVersion, ex);
                            PublishActivity();
                        }
                    }
                }
                await Task.Delay(interval, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task WorkAsync()
    {
        try
        {
            await foreach (var queued in jobs.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                var job = queued;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (sync) {
                    if (!IsCurrentOcrJob(job)) { telemetry?.Record(diagnosticSession, DebugCode.STALE_RESULT_DISCARDED, DebugReason.CandidateSuperseded); continue; }
                    // A more recent captured frame was fully verified against
                    // this candidate. Drop only earlier automatic image work;
                    // manual/forced input and another trigger keep priority.
                    if(!job.Manual && !job.Force && job.Text is null && job.Started<=markerVerifiedAt
                        && job.Version==markerVerifiedVersion && job.TriggerEpoch==markerVerifiedTrigger) {
                        telemetry?.Record(diagnosticSession,DebugCode.OCR_SKIPPED,DebugReason.LearnedMarker);PublishActivity();continue;
                    }
                    current = cancellation; activeOcrJob = job;
                }
                PublishActivity();
                Guid qaId = Guid.Empty;
                try
                {
                    OcrResult? recognized = null; string text = job.Text ?? "", original = text;
                    bool fullCoverage = false, repeatedText = false;
                    long repeatedInputEpoch = -1;
                    if (job.Frame is not null)
                    {
                        if (job.Manual || translationText is null) Emit(job.Version, PipelineState.Recognizing, "문자 인식 중…", recoveredAreas: PipelineStatusArea.Capture);
                        bool adv = ocr.Capabilities.AdvRecovery && AdvTextRecovery.Supports(ocrOptions);
                        bool nvl = NvlOcrTracker.Supports(ocrOptions, ocr.Capabilities);
                        if (job.Manual) nvlOcr.Reset();
                        // A fresh read must not restore prior marker-cycle rows.
                        if (job.VerifyAcceptedSource && ocr is ILearnedMarkerFrameGate verificationGate) verificationGate.ResetMarkerFrames();
                        var nvlRegion = nvl ? nvlOcr.Select(job.Frame, job.Manual || job.FullSearch) : null;
                        job = job with { Nvl = ocrOptions.ReadingMode == "NVL" && !ocrOptions.Vertical };
                        var search = adv ? advRecovery.Select(job.Frame, job.Manual || job.FullSearch) : null;
                        if (nvlRegion is not null) search = nvlRegion.Area;
                        var runOptions = search is null ? ocrOptions : ocrOptions with { SearchArea=search, KeepLocalParagraph=true };
                        var region = adv || nvl || !ocr.Capabilities.Roi || !ocr.Capabilities.TrackedRoi ? null : ocrRegion.Select(job.Frame, job.Manual || job.FullSearch);
                        // Marker watches/reference crops use capture coordinates.
                        // Keep that coordinate space through the learning wrapper;
                        // native Paddle still limits detection to the same ROI.
                        bool markerRoi = region is not null && GameMarkerGeometry.Supported(ocrOptions)
                            && ocr is ILearnedMarkerFrameGate { LearningStatus.Mode: not MarkerLearningMode.Off };
                        if(markerRoi)runOptions=ocrOptions with { SearchArea=region };
                        var input = !markerRoi && region is { } area ? OcrRegionTracker.Crop(job.Frame, area) : job.Frame;
                        lock (sync)
                        {
                            if (!IsCurrentOcrJob(job)) continue;
                            if (gameQa is not null && qaProfile is not null) qaId = gameQa.Begin(input, runOptions, qaProfile, job.CaptureMs, job.Frame.Width, job.Frame.Height, markerRoi ? null : region);
                        }
                        bool fullSearch = region is null && runOptions.SearchArea is null;
                        recognized = await RecognizeMeasuredAsync(input, runOptions, cancellation.Token, job.VerifyAcceptedSource ? DebugReason.SourceVerification : job.Manual ? DebugReason.Manual : nvlRegion is not null ? DebugReason.NvlIncremental : region is not null || search is not null ? DebugReason.Roi : DebugReason.Standard, job.Manual || job.Force || job.VerifyAcceptedSource).ConfigureAwait(false);
                        lock (sync) { if (!IsCurrentOcrJob(job)) continue; }
                        var inputResult = recognized;
                        if (nvlRegion is not null) {
                            if (NvlOcrTracker.NeedsFullSearch(nvlRegion, recognized, job.Frame)) {
                                nvlOcr.RejectRegion(nvlRegion);
                                var attempt = recognized;
                                cancellation.Token.ThrowIfCancellationRequested();
                                runOptions = ocrOptions;
                                fullSearch |= runOptions.SearchArea is null;
                                lock (sync) {
                                    if (!IsCurrentOcrJob(job)) continue;
                                    if (gameQa is not null && qaProfile is not null) qaId = gameQa.Begin(job.Frame, runOptions, qaProfile, job.CaptureMs);
                                }
                                recognized = await RecognizeMeasuredAsync(job.Frame, runOptions, cancellation.Token, DebugReason.NvlFullRetry, job.Manual || job.Force).ConfigureAwait(false);
                                inputResult = recognized;
                                recognized = recognized with { InferenceTime = recognized.InferenceTime + attempt.InferenceTime,
                                    ModelLoadTime = recognized.ModelLoadTime + attempt.ModelLoadTime, DetectionTime = recognized.DetectionTime + attempt.DetectionTime,
                                    PreprocessTime = recognized.PreprocessTime + attempt.PreprocessTime, DetectorInferenceTime = recognized.DetectorInferenceTime + attempt.DetectorInferenceTime,
                                    DetectionPostprocessTime = recognized.DetectionPostprocessTime + attempt.DetectionPostprocessTime, MergeTime = recognized.MergeTime + attempt.MergeTime,
                                    RecognitionTime = recognized.RecognitionTime + attempt.RecognitionTime, PostprocessTime = recognized.PostprocessTime + attempt.PostprocessTime };
                                nvlRegion = null;
                            }
                            else recognized = NvlOcrTracker.Merge(nvlRegion, recognized, job.Frame, ocrOptions);
                        }
                        if (region is { } crop)
                        {
                            if (OcrRegionTracker.NeedsFullSearch(recognized, crop, markerRoi))
                            {
                                var attempt = recognized;
                                cancellation.Token.ThrowIfCancellationRequested();
                                lock (sync)
                                {
                                    if (!IsCurrentOcrJob(job)) continue;
                                    if (gameQa is not null && qaProfile is not null) qaId = gameQa.Begin(job.Frame, ocrOptions, qaProfile, job.CaptureMs);
                                }
                                fullSearch |= ocrOptions.SearchArea is null;
                                runOptions=ocrOptions;
                                recognized = await RecognizeMeasuredAsync(job.Frame, ocrOptions, cancellation.Token, DebugReason.RoiFullRetry, job.Manual || job.Force).ConfigureAwait(false);
                                inputResult = recognized; // Exact final attempt, not summed ROI/fallback timing.
                                recognized = recognized with { InferenceTime = recognized.InferenceTime + attempt.InferenceTime,
                                    ModelLoadTime = recognized.ModelLoadTime + attempt.ModelLoadTime, DetectionTime = recognized.DetectionTime + attempt.DetectionTime,
                                    PreprocessTime = recognized.PreprocessTime + attempt.PreprocessTime, DetectorInferenceTime = recognized.DetectorInferenceTime + attempt.DetectorInferenceTime,
                                    DetectionPostprocessTime = recognized.DetectionPostprocessTime + attempt.DetectionPostprocessTime, MergeTime = recognized.MergeTime + attempt.MergeTime,
                                    RecognitionTime = recognized.RecognitionTime + attempt.RecognitionTime, PostprocessTime = recognized.PostprocessTime + attempt.PostprocessTime };
                                region = null;
                            }
                            else if(!markerRoi)recognized = OcrRegionTracker.Restore(recognized, crop, job.Frame, ocrOptions);
                        }
                        if (adv) {
                            if (search is { } local && AdvTextRecovery.Clipped(recognized,local)) {
                                runOptions=ocrOptions; fullSearch |= runOptions.SearchArea is null;
                                recognized=await RecognizeMeasuredAsync(job.Frame,runOptions,cancellation.Token,DebugReason.AdvFullRetry, job.Manual || job.Force).ConfigureAwait(false); inputResult=recognized; search=null;
                            }
                            var refined=AdvTextRecovery.IncludeNearby(job.Frame,recognized,recognized,search);
                            if (refined.Text!=recognized.Text) recognized=refined;
                            else if (advRecovery.Retry(job.Frame,recognized,inputEpoch) is { } retryArea) {
                                var retryOptions=ocrOptions with { SearchArea=retryArea, DetectionLongSide=1280, KeepLocalParagraph=true };
                                try {
                                    var retry=await RecognizeMeasuredAsync(job.Frame,retryOptions,cancellation.Token,DebugReason.AdvRecovery, job.Manual || job.Force).ConfigureAwait(false);
                                    var improved=AdvTextRecovery.IncludeNearby(job.Frame,recognized,retry,retryArea);
                                    if(improved.Text!=recognized.Text) { recognized=improved; inputResult=retry;runOptions=retryOptions;search=retryArea; }
                                }
                                catch(Exception ex) when(ex is not (OperationCanceledException or OutOfMemoryException)) {
                                    // Optional recovery must not discard the already completed normal OCR.
                                }
                            }
                            advRecovery.Observe(job.Frame,recognized,search is null);
                        }
                        if (nvl) recognized=NvlOcrTracker.ArrangeRows(recognized,ocrOptions);
                        if (nvl && !job.Manual && !job.FullSearch) recognized = nvlOcr.Stabilize(job.Frame,recognized);
                        cancellation.Token.ThrowIfCancellationRequested(); text = TranslationPrompt.Normalize(recognized.Text);
                        telemetry?.SetContext(v => v with { AcceptedLines = recognized.Lines.Count });
                        original = ocrOptions.ReadingMode == "NVL" && !ocrOptions.Vertical ? NvlText.DisplayLines(recognized.Lines) : text;
                        if (job.Nvl) text = NvlText.Canonical(text, ocrOptions.Language);
                        ObserveSourceVerification(job, text);
                        if (text.Length == 0) {
                            telemetry?.Record(diagnosticSession, DebugCode.OCR_TEXT_REJECTED, DebugReason.EmptyText);
                            lock (sync) {
                                if (!IsCurrentOcrJob(job)) continue;
                                animations.Reset(); repeatedCandidate.Reset(); recognizedFrameHasFullCoverage = false;
                                if (nvl) { nvlOcr.Observe(job.Frame, recognized); recognizedText = recognizedOriginal = null; verifiedFrames.Clear(); verifiedOrder.Clear(); }
                                frames.Observe(recognized, job.Frame, fullSearch);
                                gameQa?.Complete(qaId, inputResult, text, false, runOptions);
                            }
                            lock (sync) {
                                if (!IsCurrentOcrJob(job)) continue;
                                if (job.Manual) Emit(job.Version, PipelineState.Watching, "대사를 찾지 못했습니다. 인식 영역과 언어를 확인하세요.",
                                    result: recognized, job: job, important: true, failureArea: PipelineStatusArea.Ocr, recoveredAreas: PipelineStatusArea.Ocr | PipelineStatusArea.Capture);
                                else if (translationText is not null) RestoreKnownState(job.Version, translationText, "문자를 찾지 못했습니다. ", PipelineStatusArea.Ocr | PipelineStatusArea.Capture);
                                else Emit(job.Version, PipelineState.Watching,
                                    translationEnabled ? "문자를 찾지 못했습니다. 이전 번역은 유지합니다." : "문자를 찾지 못했습니다. 이전 원문은 유지합니다.",
                                    result: recognized, job: job, recoveredAreas: PipelineStatusArea.Ocr | PipelineStatusArea.Capture);
                            }
                            continue;
                        }
                        lock (sync) {
                            if (!IsCurrentOcrJob(job)) continue;
                            if (recognizedText != text || recognizedOriginal != original) {
                                candidateSince = Stopwatch.GetTimestamp();
                            }
                            if (nvl) nvlOcr.Observe(job.Frame, recognized);
                            ocrRegion.Observe(job.Frame, recognized, region is not null);
                            var searched = !markerRoi && region is { } cropArea && runOptions.SearchArea is { } nested
                                ? nested with { X = nested.X + cropArea.X, Y = nested.Y + cropArea.Y } : region ?? runOptions.SearchArea;
                            fullCoverage = fullSearch || recognizedFrameHasFullCoverage && recognizedFrame is not null
                                && searched is { } scanned && OutsideSearchUnchanged(recognizedFrame, job.Frame, scanned);
                            // Only complete observations of this capture area are evidence.
                            // Partial recognition must not certify unseen text or a new size.
                            if (!fullCoverage || recognizedFrame is { } previousFrame
                                && (previousFrame.Width != job.Frame.Width || previousFrame.Height != job.Frame.Height))
                                repeatedCandidate.Reset();
                            if (job.Nvl && !job.Manual && fullCoverage) {
                                repeatedText = repeatedCandidate.Observe(text, job.Started, settle);
                                repeatedInputEpoch = inputEpoch;
                            }
                            frames.Observe(recognized, job.Frame, fullCoverage);
                            recognizedFrame = job.Frame; recognizedFrameHasFullCoverage = fullCoverage; recognizedText = text; recognizedOriginal = original;
                            // Frame verification depends on fresh OCR, not on a
                            // translation already being accepted or completed.
                            if(ocr is ILearnedMarkerFrameGate markerGate) {
                                markerGate.ObserveAcceptedFrame(job.Frame,recognized,fullCoverage);PublishMarkerDiagnostics();
                            }
                            gameQa?.Complete(qaId, inputResult, text, translationEnabled, runOptions, nvlRegion?.Prefix);
                            if (translationText == text) {
                                telemetry?.SameSource(diagnosticSession);
                                if (translationOriginal == original) RememberVerifiedFrame(job.Frame, recognized, fullCoverage);
                                if (translationState == PipelineState.Translating) translationQaId = qaId;
                                else if (translationState == PipelineState.Watching && !translationEnabled) { /* QA remains not_requested. */ }
                                else if (translationState == PipelineState.Watching && context.TryLast(text, out var cachedQa)) gameQa?.Translated(qaId, cachedQa, 0);
                                else gameQa?.Finish(qaId, "failed");
                            }
                        }
                    }
                    if (text.Length > 16000) { telemetry?.Record(diagnosticSession, DebugCode.OCR_TEXT_REJECTED, DebugReason.TooLong); throw new TranslationException("원문이 너무 깁니다. 캡처 영역을 줄여 주세요."); }
                    lock (sync) {
                        if (!IsCurrentOcrJob(job)) continue;
                        if (!job.Manual && job.TriggerEpoch == 0 && translationText == text && translationOriginal == original && !(job.Nvl && nvlOcr.PageBreakPending)) { RestoreKnownState(job.Version, text, recoveredAreas: AcceptedSourceAreas(recognized)); continue; }
                        // Publish only while this capture is still current. A pause can
                        // retire image work without changing the accepted AI version.
                        Emit(job.Version, PipelineState.Recognizing, "인식 완료 · 문장 확인 중", result: recognized, job: job,
                            recoveredAreas: PipelineStatusArea.Input | (recognized is null ? PipelineStatusArea.None : PipelineStatusArea.Ocr | PipelineStatusArea.Capture));
                    }
                    // Candidate text remains internal until the quiet/confirmation interval passes.
                    // TranslateAsync publishes the accepted source before requesting any AI response.
                    bool confirmedByRepeat = false;
                    if (!job.Manual) {
                        var remaining = settle - Stopwatch.GetElapsedTime(candidateSince);
                        if (job.TriggerEpoch != 0) {
                            var inputRemaining = settle - Stopwatch.GetElapsedTime(triggerSince);
                            if (inputRemaining > remaining) remaining = inputRemaining;
                        }
                        lock (sync) {
                            // Confirm the current observation, never resurrect an earlier OCR
                            // result. New input/capture retirement still takes precedence.
                            confirmedByRepeat = remaining > TimeSpan.Zero && repeatedText && repeatedInputEpoch == inputEpoch
                                && IsCurrentOcrJob(job) && job.Observation == visualRevision
                                && (job.TriggerEpoch == 0 || Stopwatch.GetElapsedTime(triggerSince) >= settle);
                        }
                        if (confirmedByRepeat) remaining = TimeSpan.Zero;
                        if (remaining > TimeSpan.Zero) {
                            lock(sync) {if(IsCurrentOcrJob(job) && job.Frame is not null)settlingJob=job;}
                            telemetry?.Record(diagnosticSession, DebugCode.WAIT_STABLE, DebugReason.CandidateQuietWait,
                                v => v with { GateReason = DebugReason.CandidateQuietWait, OcrScheduled = false,
                                    StableMs = Math.Max(0, settle.TotalMilliseconds - remaining.TotalMilliseconds), SettleMs = settle.TotalMilliseconds });
                            await Task.Delay(remaining, cancellation.Token).ConfigureAwait(false);
                            // If pixels changed during the quiet wait, verify the text again first.
                            // Repeated identical OCR does not restart the text quiet clock.
                            lock (sync) {
                                bool markerVerified=ReferenceEquals(settlingJob,job) && markerVerifiedAt>=job.Started
                                    && markerVerifiedVersion==job.Version && markerVerifiedTrigger==job.TriggerEpoch && markerVerifiedRevision==visualRevision;
                                if (job.Observation != visualRevision && !markerVerified || !IsCurrentOcrJob(job)) { telemetry?.Record(diagnosticSession, DebugCode.OCR_TEXT_REJECTED, DebugReason.CandidateSuperseded); probePending = true; continue; }
                            }
                        }
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock (sync) {
                        if (!IsCurrentOcrJob(job)) continue;
                        if (confirmedByRepeat && (job.Observation != visualRevision || repeatedInputEpoch != inputEpoch)) {
                            telemetry?.Record(diagnosticSession, DebugCode.OCR_TEXT_REJECTED, DebugReason.CandidateSuperseded);
                            probePending = true; continue;
                        }
                        if (!job.Manual && translationText == text && !(job.Nvl && nvlOcr.PageBreakPending)) {
                            // A stable reflow only updates the visual original. Do not cancel or
                            // resend a translation whose logical source is unchanged.
                            if (translationOriginal != original) { verifiedFrames.Clear(); verifiedOrder.Clear(); }
                            translationOriginal = original; RememberVerifiedFrame(job.Frame, recognized, fullCoverage);
                            RestoreKnownState(job.Version, text, recoveredAreas: AcceptedSourceAreas(recognized)); continue;
                        }
                        // A single noisy OCR sample is only a candidate. Supersede the old source
                        // after the candidate survives the text quiet/confirmation interval.
                        repeatedCandidate.Reset();
                        if (!job.Manual) job = job with { Version = Interlocked.Increment(ref version) };
                        if (job.Nvl) job = job with { PageBreak = nvlOcr.ConsumePageBreak() };
                        if (telemetry is not null) job = job with { DiagnosticCycle = telemetry.Accept(diagnosticSession, translationText == text,
                            !job.Nvl && translationText is { Length: > 0 } previousText && text.StartsWith(previousText, StringComparison.Ordinal), job.PageBreak, job.Nvl,
                            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))), confirmedByRepeat) };
                        translationCancellation?.Cancel();
                        var translationToken = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        if (translationText != text || translationOriginal != original || job.PageBreak) { verifiedFrames.Clear(); verifiedOrder.Clear(); }
                        translationCancellation = translationToken; translationText = text; translationOriginal = original; translationFailure = translationOverlayFailure = null; translationState = PipelineState.Translating; translationQaId = qaId;
                        sourceVerificationAt = watchContinuously && translationEnabled && job.Nvl && job.Frame is not null && !job.Manual && !job.VerifyAcceptedSource
                            ? Stopwatch.GetTimestamp() : 0;
                        sourceVerificationVersion = job.Version;
                        RememberVerifiedFrame(job.Frame, recognized, fullCoverage);
                        translations.RemoveAll(task => task.IsCompletedSuccessfully);
                        translations.Add(TranslateAsync(job with { Frame = null }, text, recognized, qaId, translationToken));
                        FinishTrigger(job);
                    }
                }
                catch (OperationCanceledException) { gameQa?.Finish(qaId, "canceled"); }
                catch (Exception ex) when (ex is not OutOfMemoryException) {
                    lock (sync) {
                        bool active = IsCurrentOcrJob(job);
                        gameQa?.Finish(qaId, active ? "failed" : "canceled");
                        if (active) { FinishTrigger(job); Fail(job.Version, ex); }
                    }
                }
                finally {
                    if (cancellation.IsCancellationRequested) gameQa?.Finish(qaId, "canceled");
                    lock (sync) { if (current == cancellation) {current = null;activeOcrJob=null;settlingJob=null;} PublishActivity(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task TranslateAsync(Job job, string text, OcrResult? recognized, Guid qaId, CancellationTokenSource cancellation)
    {
        try {
            if(!job.Nvl) telemetry?.SetContext(v=>v with {NvlPlannedBlocks=null,NvlShortTailCombined=false,NvlPrefixReopened=false});
            if (recognized is not null && onOcrAccepted is not null) {
                string source;
                lock (sync) { if (job.Version != Version) return; source = translationOriginal ?? text; }
                cancellation.Token.ThrowIfCancellationRequested();
                await onOcrAccepted(job.Version, source, cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            if (!translationEnabled) {
                cancellation.Token.ThrowIfCancellationRequested();
                lock (sync) {
                    if (job.Version != Version) return;
                    translationState = PipelineState.Watching;
                    Emit(job.Version, PipelineState.Watching, "원문 인식 완료 · 다음 입력 대기", text, "", recognized, job,
                        recoveredAreas: AcceptedSourceAreas(recognized));
                }
                return;
            }
            string translated; double apiMs = 0; bool freshTranslation = false, repeated, hasCached; string cached; IReadOnlyList<ConversationTurn> history;
            lock (sync) {
                if (job.Version != Version) return;
                repeated = duplicate.IsDuplicate(text);
                history = context.ForRequest(text, job.Force);
                var found = context.TryLast(text, out cached);
                hasCached = !job.Force && found;
            }
            if (job.Nvl) {
                (translated, apiMs, freshTranslation) = await TranslateNvlAsync(job, text, recognized, cancellation).ConfigureAwait(false);
            }
            else if (hasCached) { translated = cached; Interlocked.Increment(ref cacheHits); telemetry?.Ai(diagnosticSession, job.DiagnosticCycle, true); }
            else {
                Emit(job.Version, PipelineState.Translating, "AI 번역 중…", text, result: recognized, job: job, recoveredAreas: AcceptedSourceAreas(recognized));
                Interlocked.Increment(ref aiCount);
                telemetry?.Ai(diagnosticSession, job.DiagnosticCycle, false,
                    Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))),text.Length);
                var result = await ai!.TranslateAsync(new(text, prompt, history, options, Progress: partial => {
                    lock (sync) {
                        // Progress belongs only to the still-running request. Empty prefixes and
                        // callbacks after success/failure must never erase a readable subtitle.
                        if (translationCancellation == cancellation && translationState == PipelineState.Translating
                            && !cancellation.IsCancellationRequested && !string.IsNullOrWhiteSpace(partial))
                        {
                            DiagnosticTranslation(job, DebugCode.TRANSLATION_PARTIAL, DebugReason.Streaming);
                            Emit(job.Version, PipelineState.Translating, "번역 수신 중…",
                            recognized is null || recognizedText == text ? text : null, partial);
                        }
                    }
                }, SourceLanguage: ocrOptions.Language, Characters: characters), cancellation.Token).ConfigureAwait(false);
                if (ai is not ITranslationDiagnostics) Interlocked.Add(ref httpAttempts, result.HttpAttempts);
                translated = result.Text; apiMs = result.Elapsed.TotalMilliseconds;
                cancellation.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(translated)) throw new TranslationException("AI 번역 결과 없음 · 다시 시도하거나 모델 설정을 확인하세요.");
                freshTranslation = true;
                lock (sync) {
                    if (job.Version != Version) return;
                    DiagnosticAiResponse(text, translated);
                    context.Accept(text, translated, job.Force);
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            lock (sync) {
                if (job.Version != Version) return;
                translationState = PipelineState.Watching; gameQa?.Translated(translationQaId, translated, apiMs);
                DiagnosticTranslation(job, DebugCode.TRANSLATION_COMPLETED, DebugReason.Completed, apiMs);
                // A pending OCR candidate does not replace the accepted source. Retain the
                // existing source when publishing the earlier request's completed translation.
                Emit(job.Version, PipelineState.Watching, repeated && apiMs == 0 ? "이전 번역 재사용" : "번역 완료 · 다음 입력 대기",
                    recognized is null || recognizedText == text ? text : null, translated, recognized, job, apiMs,
                    recoveredAreas: freshTranslation ? PipelineStatusArea.Translation : AcceptedSourceAreas(recognized));
            }
        }
        catch (OperationCanceledException) {
            DiagnosticTranslation(job, DebugCode.TRANSLATION_CANCELLED, DebugReason.Cancelled);
            lock (sync) {
                gameQa?.Finish(qaId, "canceled");
                if (translationCancellation == cancellation) {
                    gameQa?.Finish(translationQaId, "canceled");
                    if (job.Version == Version) {
                        translationFailure = "번역이 취소되었습니다. 번역을 중지한 뒤 다시 시작하세요."; translationState = PipelineState.Error;
                        Emit(job.Version, PipelineState.Error, translationFailure, failureArea: PipelineStatusArea.Translation);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DiagnosticTranslation(job, DebugCode.TRANSLATION_FAILED, DebugReason.TranslationFailure, failure: ex); lock (sync) { gameQa?.Finish(qaId, "failed"); if (translationCancellation == cancellation) { translationFailure = FailureMessage(ex); translationOverlayFailure = TranslationOverlayMessage(ex); translationState = PipelineState.Error; gameQa?.Finish(translationQaId, "failed"); } } Fail(job.Version, ex, PipelineStatusArea.Translation); }
        finally {
            lock (sync) { if (translationCancellation == cancellation) translationCancellation = null; PublishActivity(); }
            cancellation.Dispose();
        }
    }
    private async Task<(string Text, double ApiMs, bool Fresh)> TranslateNvlAsync(Job job, string text, OcrResult? recognized, CancellationTokenSource cancellation)
    {
        const string instruction = "\nNVL incremental translation: previous original dialogue is read-only context. Translate ONLY the current source block. Do not repeat or revise the context. The current block may be incomplete: translate only what is present, never invent its continuation.";
        IReadOnlyList<NvlTranslationBlock> blocks; Guid page; string translated;
        bool supportsHistory = TranslationProviderCatalog.Get(options.Provider).SupportsContext
            && !(options.Provider == TranslationProvider.Ollama && options.SettingsForOllama().Format == OllamaRequestFormat.TranslateGemma);
        lock (sync) {
            if (job.Version != Version) throw new OperationCanceledException(cancellation.Token);
            string remainder=nvlPage.Begin(text,job.PageBreak);
            bool reopened=nvlPage.FixedSource.Length>0 && NvlTranslationPage.IsShortAlphanumericTail(remainder);
            if(reopened) { nvlPage.ReopenFixedBlock();remainder=text; }
            bool combined=reopened || NvlTranslationPage.HasAmbiguousTail(remainder);
            blocks=NvlTranslationLayout.Plan(text, recognized?.Lines ?? [], ocrOptions.Language, nvlPage.FixedSource.Length);page=nvlPage.Id;
            telemetry?.SetContext(v=>v with {NvlPlannedBlocks=blocks.Count,NvlShortTailCombined=combined,NvlPrefixReopened=reopened});
            if (nvlPage.RestoredFrom is { } previousPage) context.RestoreNvlPage(previousPage, page);
            translated = nvlPage.FixedTranslation;
            if (job.Manual) context.BeginNvlReplacement(page, text);
            if (!job.Force && context.TryLast(text, out var cached)) {
                if (nvlPage.FixedSource.Length == 0 && NvlText.TranslationComplete(text)) nvlPage.Commit(text, cached);
                context.AcceptNvlPage(page, text, cached);
                Interlocked.Increment(ref cacheHits);
                telemetry?.Ai(diagnosticSession, job.DiagnosticCycle, true);
                return (cached, 0, false);
            }
        }
        Emit(job.Version, PipelineState.Translating, "AI 번역 중…", text, result: recognized, job: job, recoveredAreas: AcceptedSourceAreas(recognized));
        double apiMs = 0;
        if (blocks.Count > 0) {
            IReadOnlyList<ConversationTurn> history;
            lock (sync) {
                if (job.Version != Version) throw new OperationCanceledException(cancellation.Token);
                history = supportsHistory ? context.ForNvlRequest(page, nvlPage.FixedSource, nvlPage.FixedTranslation) : [];
            }
            Interlocked.Increment(ref aiCount);
            string requestText=NvlTranslationBatch.Source(blocks);
            telemetry?.Ai(diagnosticSession, job.DiagnosticCycle, false,
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestText))),requestText.Length);
            // Keep the previous provisional translation visible until the replacement completes.
            // Streaming fragments must not replace a readable, unfinished block.
            var result = await ai!.TranslateAsync(new(requestText, prompt + instruction + "\n" + TranslationParagraphs.Instruction(blocks.Count), history, options,
                SourceLanguage: ocrOptions.Language, PreserveHistory: true, Characters: characters, ParagraphCount: blocks.Count), cancellation.Token).ConfigureAwait(false);
            if (ai is not ITranslationDiagnostics) Interlocked.Add(ref httpAttempts, result.HttpAttempts);
            cancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Text)) throw new TranslationException("AI 번역 결과 없음 · 다시 시도하거나 모델 설정을 확인하세요.");
            apiMs += result.Elapsed.TotalMilliseconds;
            lock (sync) {
                if (job.Version != Version || page != nvlPage.Id) throw new OperationCanceledException(cancellation.Token);
                DiagnosticAiResponse(requestText, result.Text);
                var paragraphs = NvlTranslationBatch.Split(result.Text, blocks.Count);
                translated = nvlPage.Compose(paragraphs is null ? result.Text : string.Join("\n", paragraphs));
                if (paragraphs is not null) {
                    for (int i = 0; i < blocks.Count && blocks[i].Complete; i++)
                        nvlPage.Commit(blocks[i].Source, paragraphs[i], paragraphBoundary: true);
                }
                else if (blocks.All(b => b.Complete)) {
                    // The whole completed batch can still be reused, but no source/translation sub-pair is guessed.
                    nvlPage.Commit(string.Concat(blocks.Select(b => b.Source)), result.Text.Trim(), paragraphBoundary: true);
                }
                // A format mismatch is displayed intact and is never retried or split into extra API requests.
            }
        }
        lock (sync) {
            if (job.Version != Version || page != nvlPage.Id) throw new OperationCanceledException(cancellation.Token);
            context.AcceptNvlPage(page, text, translated);
        }
        return (translated, apiMs, blocks.Count > 0);
    }
    private static PipelineStatusArea AcceptedSourceAreas(OcrResult? recognized) => PipelineStatusArea.Input
        | (recognized is null ? PipelineStatusArea.None : PipelineStatusArea.Ocr | PipelineStatusArea.Capture);
    private static string FailureMessage(Exception ex) => ex is TranslationException or CaptureUnavailableException or OcrInitializationException
        ? ex.Message : "처리 실패 · 모델 파일, 캡처 대상 또는 설정을 확인하고 다시 실행하세요.";
    private void Fail(long request, Exception ex, PipelineStatusArea area = PipelineStatusArea.Ocr) => Emit(request, PipelineState.Error, FailureMessage(ex),
        failureArea: area, overlayMessage: area == PipelineStatusArea.Ocr ? OcrOverlayMessage(ex)
            : area == PipelineStatusArea.Translation ? TranslationOverlayMessage(ex)
            : area == PipelineStatusArea.Capture ? "게임 화면을 확인하지 못했습니다. 대상 창과 인식 영역을 확인하세요." : null);
    private string TranslationOverlayMessage(Exception ex) => TranslationOverlayMessage(options.Provider, ex);
    internal static string TranslationOverlayMessage(TranslationProvider provider, Exception ex)
    {
        // Provider plus known messages emitted by our adapters: never infer an OOM from an arbitrary HTTP 500.
        string message = ex.Message;
        if (provider == TranslationProvider.Ollama) {
            if (message.StartsWith("Ollama 서버 주소 또는 모델을 찾을 수 없습니다.", StringComparison.Ordinal))
                return "Ollama 서버 주소나 설치된 모델을 확인하세요.";
            if (message.StartsWith("Ollama가 요청 형식을 거부했습니다.", StringComparison.Ordinal))
                return "Ollama가 요청을 처리하지 못했습니다. 모델의 요청 형식을 확인하세요.";
            if (message.StartsWith("Ollama가 메모리 부족 오류를 반환", StringComparison.Ordinal))
                return "Ollama의 메모리가 부족합니다. 작은 모델을 선택하거나 다른 프로그램을 종료하세요.";
            if (message.StartsWith("Ollama 서버와 모델 실행기 사이의 연결", StringComparison.Ordinal))
                return "Ollama 내부 연결이 끊겼습니다. 번역을 중지한 뒤 다시 시작하세요.";
            if (message.StartsWith("Ollama 모델 실행기의 응답", StringComparison.Ordinal))
                return "Ollama 모델이 응답하지 않았습니다. 번역을 중지한 뒤 다시 시작하세요.";
            if (message.StartsWith("Ollama 서버에 연결하지 못", StringComparison.Ordinal))
                return "Ollama에 연결하지 못했습니다. 서버 실행 상태를 확인하세요.";
            if (message.StartsWith("Ollama ", StringComparison.Ordinal) && message.Contains("제한 초과", StringComparison.Ordinal))
                return "Ollama 번역 시간이 초과되었습니다. 모델 실행 상태와 제한 시간을 확인하세요.";
            return "Ollama 서버에서 오류가 발생했습니다. 서버 상태를 확인하세요.";
        }
        if (provider == TranslationProvider.HyMtLocal) {
            // The runtime may recommend checking memory AND graphics drivers
            // even when neither is confirmed as the cause. Report low memory
            // only for an explicit OOM or a failed CPU RAM requirement.
            if (ex is OutOfMemoryException
                || message.Contains("메모리가 부족합니다.", StringComparison.Ordinal)
                || (message.Contains("CPU 실행에는 가용 RAM ", StringComparison.Ordinal)
                    && message.Contains("GiB 이상이 필요합니다.", StringComparison.Ordinal)))
                return "로컬 번역에 필요한 메모리가 부족합니다.";
            if (message.StartsWith("로컬 번역이 출력 한도", StringComparison.Ordinal))
                return "번역할 글이 너무 깁니다. 대사 영역을 줄여 주세요.";
            return "로컬 번역에 실패했습니다. 모델 상태를 확인하고 다시 시도하세요.";
        }
        if (provider == TranslationProvider.DeepL && message.StartsWith("DeepL API 번역 문자 사용 한도", StringComparison.Ordinal))
            return "DeepL 번역 사용량 한도에 도달했습니다.";
        if (message.StartsWith("API 키", StringComparison.Ordinal) || message.StartsWith("DeepL API 키", StringComparison.Ordinal))
            return "API 키와 모델 접근 권한을 확인하세요.";
        if (message.Contains("요청 한도", StringComparison.Ordinal) || message.Contains("요청 빈도 제한", StringComparison.Ordinal))
            return "요청 한도에 도달했습니다. 잠시 후 다시 시도하세요.";
        if (message.Contains("시간이 초과", StringComparison.Ordinal) || message.Contains("시간 초과", StringComparison.Ordinal))
            return "번역 응답 시간이 초과되었습니다. 서버 상태와 제한 시간을 확인하세요.";
        if (message.Contains("연결하지 못", StringComparison.Ordinal) || message.Contains("연결이 끊겼", StringComparison.Ordinal))
            return "AI 서버 연결이 끊겼습니다. 네트워크 상태를 확인하세요.";
        if (message.StartsWith("Gemini 응답이 중단", StringComparison.Ordinal))
            return "Gemini 번역이 중단되었습니다. 다시 시도하세요.";
        if (message.Contains("번역문을 반환하지", StringComparison.Ordinal) || message.Contains("번역 응답에 결과가 없", StringComparison.Ordinal))
            return "AI가 번역문을 반환하지 않았습니다. 모델 설정을 확인하세요.";
        if (message.Contains("응답 형식", StringComparison.Ordinal) || message.Contains("응답 문자 인코딩", StringComparison.Ordinal))
            return "AI 번역 결과를 읽지 못했습니다. 모델 설정을 확인하세요.";
        if (message.Contains("일시적으로 사용할 수 없", StringComparison.Ordinal))
            return "AI 서비스를 잠시 사용할 수 없습니다. 다시 시도하세요.";
        return "AI 서비스 요청에 실패했습니다. 서버 상태를 확인하세요.";
    }
    private static string OcrOverlayMessage(Exception ex)
    {
        if (ex is UnauthorizedAccessException || ex.Message.Contains("읽을 권한", StringComparison.Ordinal))
            return "OCR 모델 파일을 읽을 수 없습니다. 파일 접근 권한을 확인하세요.";
        if (ex is FileNotFoundException || ex.Message.Contains("찾을 수 없습니다", StringComparison.Ordinal) || ex.Message.Contains("모델이 없습니다", StringComparison.Ordinal))
            return "OCR 모델 파일을 찾을 수 없습니다. 모델 파일을 확인하세요.";
        if (ex.Message.Contains("SHA-256", StringComparison.Ordinal) || ex.Message.Contains("손상", StringComparison.Ordinal) || ex.Message.Contains("크기가 올바르지", StringComparison.Ordinal))
            return "OCR 모델 파일이 손상되었거나 올바르지 않습니다. 모델 파일을 다시 준비하세요.";
        if (ex.Message.Contains("너무 깁니다", StringComparison.Ordinal))
            return "인식한 글이 너무 깁니다. 대사 영역을 줄여 주세요.";
        return ex is OcrInitializationException || ex.Message.Contains("ONNX", StringComparison.Ordinal) || ex.Message.Contains("manifest", StringComparison.Ordinal)
            ? "OCR 모델을 불러오지 못했습니다. 모델 파일을 확인하세요."
            : "문자 인식에 실패했습니다. 인식 영역과 OCR 설정을 확인하세요.";
    }
    private void ReportCaptureState(long request, Exception ex)
    {
        if (ex is CaptureUnavailableException { IsExpectedWait: true })
            Emit(request, PipelineState.Watching, "캡처 일시 대기 · " + ex.Message, important: true, failureArea: PipelineStatusArea.Capture,
                overlayMessage: CaptureWaitOverlayMessage((CaptureUnavailableException)ex));
        else Fail(request, ex, PipelineStatusArea.Capture);
    }
    private static string CaptureWaitOverlayMessage(CaptureUnavailableException ex) => ex.WaitReason switch
    {
        CaptureWaitReason.NotForeground => "게임 창을 앞으로 가져오세요. 활성화되면 인식을 재개합니다.",
        CaptureWaitReason.HiddenOrMinimized => "게임 창이 최소화되어 있습니다. 창을 복원하세요.",
        CaptureWaitReason.WindowBusy => "게임 화면을 불러오는 중입니다…",
        CaptureWaitReason.ScalingTransition => "게임 화면이 바뀌었습니다. 잠시 후 다시 확인합니다.",
        CaptureWaitReason.Occluded => "KO Trans 2 창이 게임 화면을 가리고 있습니다. 창을 옮기거나 최소화하세요.",
        _ => "게임 화면을 확인할 수 없습니다. 게임 창을 확인하세요."
    };
    private void Emit(long request, PipelineState state, string message, string? Original = null, string? Translation = null, OcrResult? result = null, Job? job = null, double apiMs = 0, bool important = false,
        PipelineStatusArea failureArea = PipelineStatusArea.None, PipelineStatusArea recoveredAreas = PipelineStatusArea.None, string? overlayMessage = null)
    {
        lock (sync) {
            if (request != Version || stopped) return;
            if (Original is not null && Original == translationText) Original = translationOriginal ?? Original;
            Updated?.Invoke(new(request, state, message, Original, Translation, result, job?.CaptureMs ?? 0, apiMs,
                job is null ? 0 : Stopwatch.GetElapsedTime(job.Started).TotalMilliseconds, ocrCount, aiCount, (ai as ITranslationDiagnostics)?.HttpAttempts ?? httpAttempts, cacheHits)
                { IsBusy = IsBusy, Important = important, FailureArea = failureArea, RecoveredAreas = recoveredAreas, OverlayMessage = overlayMessage });
        }
    }
    public void Stop()
    {
        if (stopped) return;
        lock (sync) {
            if (stopped) return;
            Invalidate(); stopped = true; lifetime.Cancel(); jobs.Writer.TryComplete();
            (ocr as ILearnedMarkerFrameGate)?.ResetMarkerFrames();PublishMarkerDiagnostics();
            if (manualOperations == 0) manualDrained.TrySetResult();
        }
        telemetry?.Stop(diagnosticSession);
        Updated?.Invoke(new(Version, PipelineState.Stopped, translationEnabled ? "번역을 중지했습니다." : "원문 인식을 중지했습니다.", Translation: ""));
        ActivityChanged?.Invoke(Version, false);
    }
    public ValueTask DisposeAsync()
    { lock (sync) { return new(disposal ??= DisposeCoreAsync()); } }
    private async Task DisposeCoreAsync()
    {
        Stop();
        try {
            await Task.WhenAll(monitor ?? Task.CompletedTask, worker ?? Task.CompletedTask, manualDrained.Task).ConfigureAwait(false);
            await Task.WhenAll(translations).ConfigureAwait(false);
        }
        finally { ocrResultCache.Clear(); lifetime.Dispose(); captureGate.Dispose(); monitorWake.Dispose(); recognizedFrame = null; ocrRegion.Reset(); nvlOcr.Reset(); nvlPage.Reset(); frames.Reset(); animations.Reset(); repeatedCandidate.Reset(); verifiedFrames.Clear(); verifiedOrder.Clear(); }
        // Shared OCR remains owned by the app. Native inference has drained, never forcibly terminated.
    }
}

public enum CaptureWaitReason { None, NotForeground, HiddenOrMinimized, Occluded, ScalingTransition, WindowBusy }
public sealed class CaptureUnavailableException(string message, CaptureWaitReason waitReason = CaptureWaitReason.None) : Exception(message)
{
    public CaptureWaitReason WaitReason { get; } = waitReason;
    public bool IsExpectedWait => WaitReason is CaptureWaitReason.NotForeground or CaptureWaitReason.HiddenOrMinimized or CaptureWaitReason.Occluded or CaptureWaitReason.ScalingTransition or CaptureWaitReason.WindowBusy;
}

// Keep the full-frame and ROI baselines separate. Changing the ROI cannot manufacture an OCR request.
public sealed class RealtimeFrameGate
{
    private readonly FrameChangeDetector full = new(), roi = new();
    private readonly TextRegionTracker tracker = new();
    private PixelRect? scope;
    private int ticks, frameWidth, frameHeight;
    public bool ProbeOutside { get; private set; }
    public bool HasRoi => scope is not null;
    public void Reset() { full.Reset(); roi.Reset(); tracker.Reset(); scope = null; ticks = frameWidth = frameHeight = 0; }
    public void AcceptManual(ImageFrame frame) { full.Reset(); full.Evaluate(frame); full.Evaluate(frame); SeedRoi(frame); }
    private void SeedRoi(ImageFrame frame)
    {
        scope = tracker.GetRegion(frame.Width, frame.Height); roi.Reset(); roi.Evaluate(frame, scope); roi.Evaluate(frame, scope);
    }
    public void Observe(OcrResult result, ImageFrame frame, bool fullCoverage = false)
    {
        if (frameWidth != frame.Width || frameHeight != frame.Height) Reset();
        frameWidth = frame.Width; frameHeight = frame.Height;
        tracker.Observe(result, frame.Width, frame.Height); SeedRoi(frame);
        // OCR has inspected these pixels already. Keeping the older full-frame
        // baseline would rediscover the same change at the next outside-ROI probe.
        // Partial reads cannot certify unseen pixels and leave that baseline alone.
        if (fullCoverage) {
            full.Reset(); full.Evaluate(frame); full.Evaluate(frame);
            ticks = 0; ProbeOutside = false;
        }
    }
    public FrameDecision Evaluate(ImageFrame frame)
    {
        ProbeOutside=false;
        if (frameWidth != frame.Width || frameHeight != frame.Height) { Reset(); frameWidth = frame.Width; frameHeight = frame.Height; }
        var region = tracker.GetRegion(frame.Width, frame.Height);
        if (region != scope) SeedRoi(frame);
        if (region is null) return full.Evaluate(frame);
        var decision = roi.Evaluate(frame, region);
        // A cursor-sized edit is still examined, but must not cancel an in-flight paid request.
        // Do not update the full baseline: periodic reacquisition checks it again when idle.
        if (decision is FrameDecision.Stabilizing or FrameDecision.Ready && roi.ChangedPixels <= 64) return FrameDecision.Probe;
        if (decision == FrameDecision.Ready) { full.Reset(); full.Evaluate(frame); full.Evaluate(frame); return decision; }
        // A periodic outside-region search is not a dialogue invalidation.
        if (decision == FrameDecision.Unchanged && ++ticks % 5 == 0)
        {
            var probe = full.Evaluate(frame);
            if (probe != FrameDecision.Unchanged) { ProbeOutside=true; return FrameDecision.Probe; }
        }
        return decision;
    }
}
