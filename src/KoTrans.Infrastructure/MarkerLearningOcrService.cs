using System.Collections.Generic;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// The app retains ownership of the shared native OCR. The session adds no capture
// or provider calls. The native engine may verify a proposed joined-body crop.
public sealed partial class MarkerLearningOcrService(IOcrService inner,MarkerLearningMode mode,GameMarkerLearner learner,
    MarkerLearningStore store,Action<MarkerLearningStatus> report) : IOcrService,ILearnedMarkerFrameGate
{
    private readonly object sync=new();
    private bool stopped,storageError;
    private int savedRevision;
    private MarkerLearningStatus? lastStatus;
    private TerminalMarkerReference? reference;
    private long referenceAt;
    private Task writes=Task.CompletedTask;
    private readonly LearnedMarkerFrameGuard frameGuard=new();
    private readonly LearnedMarkerCycleRecovery blendedCycle=new();
    private bool cycleVerifiedFrame;
    private long excludedTotal,excludingCalls,skippedFrames,recheckFrames,extraBodyChecks,bodyCheckCacheHits,framePreparations,framePreparationFailures;
    private MarkerFrameDecision frameDecision;
    public MarkerLearningStatus LearningStatus {get {lock(sync)return learner.Status with {
        StorageError=storageError,ExcludedTotal=excludedTotal,ExcludingOcrCalls=excludingCalls,SkippedFrames=skippedFrames,
        RecheckFrames=recheckFrames,ExtraBodyChecks=extraBodyChecks,BodyCheckCacheHits=bodyCheckCacheHits,FrameDecision=frameDecision,FrameFailure=cycleVerifiedFrame?MarkerFrameFailure.None:frameGuard.Failure,
        FramePreparations=framePreparations,FramePreparationFailures=framePreparationFailures,FrameNoisePixels=frameGuard.NoisePixels,
        RecoveredCycles=blendedCycle.ConfirmedCycles,RecoveryFailure=blendedCycle.Failure};}}
    public void ObserveAcceptedFrame(ImageFrame frame,OcrResult result,bool fullCoverage)
    {
        lock(sync) {
            if(stopped || mode!=MarkerLearningMode.Apply)return;
            frameGuard.Observe(frame,result,learner.Confirmed,fullCoverage);
            blendedCycle.Bind(frame,result,fullCoverage);
            cycleVerifiedFrame=blendedCycle.IsReady;
            if(learner.Confirmed.Count>0) {if(frameGuard.IsReady||blendedCycle.IsReady)framePreparations++;else framePreparationFailures++;}
            frameDecision=MarkerFrameDecision.Unavailable;Update();
        }
    }
    public MarkerFrameDecision EvaluateMarkerFrame(ImageFrame baseline,ImageFrame current)
    {
        lock(sync) {
            if(stopped || mode!=MarkerLearningMode.Apply)return MarkerFrameDecision.Unavailable;
            cycleVerifiedFrame=false;
            frameDecision=blendedCycle.Evaluate(baseline,current);
            cycleVerifiedFrame=frameDecision is MarkerFrameDecision.Reused or MarkerFrameDecision.Unchanged;
            if(frameDecision is not (MarkerFrameDecision.Reused or MarkerFrameDecision.Unchanged)) {
                frameDecision=frameGuard.Evaluate(baseline,current,learner.Confirmed);
            }
            if(frameDecision==MarkerFrameDecision.Reused)skippedFrames++;
            else if(frameDecision is MarkerFrameDecision.BodyChanged or MarkerFrameDecision.Uncertain or MarkerFrameDecision.SizeChanged)recheckFrames++;
            Update();return frameDecision;
        }
    }
    public void ResetMarkerFrames(){lock(sync){frameGuard.Reset();blendedCycle.Reset();cycleVerifiedFrame=false;reference=null;frameDecision=MarkerFrameDecision.Unavailable;if(!stopped)Update();}}
    public OcrState State=>inner.State;
    public OcrCapabilities Capabilities=>inner.Capabilities;
    public OcrExecutionStatus ExecutionStatus=>inner.ExecutionStatus;
    public bool CollectAttemptDiagnostics {set=>inner.CollectAttemptDiagnostics=value;}
    public event Action<OcrExecutionStatus>? ExecutionChanged {add=>inner.ExecutionChanged+=value;remove=>inner.ExecutionChanged-=value;}
    public Task InitializeAsync(OcrOptions options,CancellationToken token)=>inner.InitializeAsync(options,token);
    public void ObserveFrame(ImageFrame frame)
    {
        lock(sync) {if(stopped)return;learner.ObserveFrame(frame,Environment.TickCount64);Update();}
    }
    private void Update()
    {
        if(savedRevision!=learner.Revision) {
            savedRevision=learner.Revision;var snapshot=learner.Confirmed;
            writes=SaveAfterAsync(writes,snapshot);
        }
        var status=LearningStatus;
        if(status!=lastStatus) {lastStatus=status;report(status);}
    }
    private async Task SaveAfterAsync(Task previous,IReadOnlyList<GameMarkerPattern> patterns)
    {
        await previous.ConfigureAwait(false);
        try {await store.SaveAsync(patterns).ConfigureAwait(false);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException) {
            lock(sync) {storageError=true;if(!stopped)report(LearningStatus);}
        }
    }
    public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)
        => RecognizeCoreAsync(frame,options,token,null,false,()=>{});
    internal Task<OcrResult> RecognizeCachedAsync(ImageFrame frame,OcrOptions options,CancellationToken token,
        OcrResultCache cache,bool bypass,Action onMiss)
        => RecognizeCoreAsync(frame,options,token,cache,bypass,onMiss);
    private async Task<OcrResult> RecognizeCoreAsync(ImageFrame frame,OcrOptions options,CancellationToken token,
        OcrResultCache? cache,bool bypass,Action onMiss)
    {
        IReadOnlyList<GameMarkerPattern>? patterns;
        TerminalMarkerReference? previous;
        lock(sync) {
            patterns=!stopped && mode==MarkerLearningMode.Apply && GameMarkerGeometry.Supported(options) ? learner.Confirmed : null;
            previous=!stopped && mode!=MarkerLearningMode.Off && GameMarkerGeometry.Supported(options)
                && Environment.TickCount64-referenceAt<=5000 && reference?.Width==frame.Width && reference.Height==frame.Height ? reference : null;
        }
        var effective=options with {MarkerPatterns=patterns,MarkerReference=previous,CollectMarkerLearning=mode!=MarkerLearningMode.Off && GameMarkerGeometry.Supported(options)};
        var result=await (cache is null ? inner.RecognizeAsync(frame,effective,token)
            : cache.RecognizeAsync(inner,frame,effective,token,bypass,onMiss)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock(sync) {if(!stopped && GameMarkerGeometry.Supported(options)){
            if(mode==MarkerLearningMode.Apply)result=blendedCycle.Observe(frame,result,learner.Confirmed,Environment.TickCount64,
                fullSearch:options.SearchArea is null,searchArea:options.SearchArea);
            if(mode!=MarkerLearningMode.Off && TerminalMarkerReference.Create(frame,result.Candidates??result.Lines) is {} clean) {
                reference=clean;referenceAt=Environment.TickCount64;
            }
            if(!result.ReusedFromCache) {
                excludedTotal+=Math.Max(0,result.LearnedMarkerExclusions);if(result.LearnedMarkerExclusions>0)excludingCalls++;
            }
            extraBodyChecks+=Math.Max(0,result.MarkerBodyChecks);
            bodyCheckCacheHits+=Math.Max(0,result.MarkerBodyCacheHits);
            learner.ObserveOcr(frame,result,Environment.TickCount64);Update();}}
        return result;
    }
    public Task StopAsync() {lock(sync){stopped=true;reference=null;fastCancellation.Cancel();frameGuard.Reset();blendedCycle.Reset();cycleVerifiedFrame=false;frameDecision=MarkerFrameDecision.Unavailable;return Task.WhenAll(writes,fastWorker);}}
    public ValueTask DisposeAsync()=>new(StopAsync());
}
