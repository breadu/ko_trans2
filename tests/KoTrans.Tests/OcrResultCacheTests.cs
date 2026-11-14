using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class OcrResultCacheTests
{
    private static int checks;
    private static void Check(bool value,string message) { if(!value)throw new Exception(message);checks++; }
    private static ImageFrame Frame(byte value,int width=80)
    {
        var pixels=new byte[width*40*4];pixels[0]=value;
        for(int y=5;y<17;y++)for(int x=5;x<30;x++) {
            int i=(y*width+x)*4;pixels[i]=pixels[i+1]=pixels[i+2]=(byte)(value*73);
        }
        return new(width,40,width*4,pixels);
    }
    private sealed class Engine : IOcrService
    {
        public int Calls; public bool Empty,Fail,Cancel; public OcrOptions? LastOptions;
        public OcrState State=>OcrState.Ready;
        public OcrCapabilities Capabilities=>OcrCapabilities.Basic;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t) {
            Interlocked.Increment(ref Calls);
            LastOptions=o;
            if(Fail)throw new InvalidOperationException("synthetic failure");
            if(Cancel)throw new OperationCanceledException();
            // Different marker suffixes invalidate the old source-bound frame set.
            return Task.FromResult(new OcrResult(Empty?[]:[new("body"+f.Pixels.Span[0],.9f,new(4,4,48,12))],TimeSpan.FromMilliseconds(4)) {
                RecognitionCache=new(3,2,OcrCacheMissReason.InputNotCached) {Misses=new(InputNotCached:3)},
                MarkerBodyChecks=1,MarkerBodyCacheHits=2,LearnedMarkerExclusions=1,Attempts=new(0,1),DetectionPasses=1 });
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    internal static async Task Run()
    {
        await KeysAndLifetime(); await Learning(); await Pipeline();
        Console.WriteLine($"PASS: {checks} OCR result cache pixel/options/manual/learning/lifetime/diagnostic checks. Mock OCR only.");
    }
    private static async Task KeysAndLifetime()
    {
        var cache=new OcrResultCache();var engine=new Engine();var options=new OcrOptions("ja","ADV",false);int misses=0;
        Task<OcrResult> Read(ImageFrame? f=null,OcrOptions? o=null,bool bypass=false,CancellationToken token=default)
            =>cache.RecognizeAsync(engine,f??Frame(1),o??options,token,bypass,()=>misses++);
        await Read();var hit=await Read();
        Check(cache.LastMissReason==OcrCacheMissReason.Empty && hit.RecognitionCache is null,"Full-frame hit replayed recognition counts or lost miss reason");
        Check(engine.Calls==1 && misses==1 && hit.ReusedFromCache,"Exact request did not avoid native OCR");
        Check(hit.InferenceTime==TimeSpan.Zero && hit.Attempts==new OcrAttemptCounts(0,0) && hit.DetectionPasses==0
            && hit.MarkerBodyChecks==0 && hit.MarkerBodyCacheHits==0 && hit.LearnedMarkerExclusions==1,"Cached evidence counted old inference work");
        var onePixel=Frame(1);var changed=onePixel.Pixels.ToArray();changed[123]^=1;
        Check(!(await Read(new(onePixel.Width,onePixel.Height,onePixel.Stride,changed))).ReusedFromCache,"One changed pixel was treated as the same request");
        await Read(Frame(2));await Read(Frame(1,81));
        var pattern=new GameMarkerPattern(MarkerAnchor.LineEnd,.5,0,.5,.5,[new(0xFF00,200,200,200)]);
        var variants=new[]{ options with {Language="en"},options with {ReadingMode="NVL"},options with {Vertical=true},
            options with {SuppressFurigana=false}, options with {SearchArea=new(0,0,60,30)}, options with {DetectionLongSide=1280},
            options with {KeepLocalParagraph=true},options with {EngineId="future-engine"},options with {ModelPresetId="other"},
            options with {UseDirectMl=true},options with {CollectMarkerDiagnostics=true},options with {CollectMarkerLearning=true},
            options with {MarkerPatterns=[pattern]},options with {MarkerReference=new(80,40,[new("synthetic",.9f,new(4,4,48,12))])} };
        foreach(var variant in variants) {var value=await Read(o:variant);Check(!value.ReusedFromCache && cache.LastMissReason==OcrCacheMissReason.ConditionsChanged,"Different OCR/learning condition reused stale evidence or wrong reason");}
        int before=engine.Calls;await Read(bypass:true);Check(engine.Calls==before+1 && engine.LastOptions!.BypassOcrCaches && cache.LastMissReason==OcrCacheMissReason.Bypassed,"Forced request used cache or bypass was not forwarded");
        using(var cts=new CancellationTokenSource()) {cts.Cancel();try {await Read(token:cts.Token);throw new Exception("Cancelled hit returned");}catch(OperationCanceledException){checks++;}}
        engine.Empty=true;await Read(bypass:true);engine.Empty=false;before=engine.Calls;await Read();Check(engine.Calls==before+1,"Empty forced result kept old answer");
        engine.Fail=true;try{await Read(bypass:true);}catch(InvalidOperationException){}engine.Fail=false;
        before=engine.Calls;await Read();Check(engine.Calls==before+1,"Failed explicit retry left a stale cached answer");
        cache.Clear();engine.Fail=true;try{await Read();}catch(InvalidOperationException){}engine.Fail=false;
        engine.Cancel=true;try{await Read();}catch(OperationCanceledException){}engine.Cancel=false;
        Check(cache.Count==0,"Failure/cancellation populated cache");
        for(byte i=0;i<40;i++)await Read(Frame(i));
        Check(cache.Count==OcrResultCache.Capacity,"Cache is unbounded");
        before=engine.Calls;await Read(Frame(0));Check(engine.Calls==before+1 && cache.LastMissReason==OcrCacheMissReason.Evicted,"Old entry eviction was not recorded");
        cache.Clear();Check(cache.Count==0,"Session cleanup retained results");
    }
    private static async Task Learning()
    {
        foreach(var mode in new[]{MarkerLearningMode.Off,MarkerLearningMode.Observe,MarkerLearningMode.Apply}) {
            var engine=new Engine();var cache=new OcrResultCache();
            await using var learning=new MarkerLearningOcrService(engine,mode,new(mode,[]),
                new(Path.Combine(Path.GetTempPath(),"ko-cache-"+Guid.NewGuid().ToString("N")),new string('a',64)),_=>{});
            var options=new OcrOptions("ja","ADV",false);
            await learning.RecognizeCachedAsync(Frame(1),options,default,cache,false,()=>{});
            var hit=await learning.RecognizeCachedAsync(Frame(1),options,default,cache,false,()=>{});
            Check(hit.ReusedFromCache && engine.Calls==1 && hit.Text=="body1","Learning wrapper bypassed exact reuse");
            Check(learning.LearningStatus.ExcludedTotal==1 && learning.LearningStatus.ExtraBodyChecks==1
                && learning.LearningStatus.BodyCheckCacheHits==2,"Cached replay inflated exclusion/body inference totals");
            Check(learning.LearningStatus.Confirmed==0,"Repeating one page falsely confirmed a pattern");
        }
    }
    private static async Task Pipeline()
    {
        int phase=0;var engine=new Engine();var telemetry=new DebugTelemetry();var seen=new ConcurrentQueue<PipelineUpdate>();
        await using var pipeline=new TranslationPipeline(engine,null,_=>Task.FromResult(Frame((byte)Volatile.Read(ref phase))),
            new("ja","ADV",false),new(),new(),"",interval:TimeSpan.FromMilliseconds(15),settle:TimeSpan.Zero,
            translationEnabled:false,diagnostics:telemetry) {SuppressRepeatedAnimations=false};
        pipeline.Updated+=seen.Enqueue;pipeline.Start(true);
        await Until(()=>seen.Any(u=>u.Original=="body0"));
        for(int i=0;i<4;i++) {
            int count=seen.Count;int next=(i+1)%2;Volatile.Write(ref phase,next);
            await Until(()=>seen.Skip(count).Any(u=>u.Original=="body"+next));
        }
        Check(engine.Calls==2,"Alternating known OCR results repeatedly invoked native service");
        var snapshot=telemetry.Snapshot();
        Check(snapshot.Values.OcrResultCacheHits>=3 && snapshot.Values.LogicalOcrCalls==2
            && seen.Max(u=>u.OcrRequests)==2,"Cache hits inflated model call count");
        Check(snapshot.Cycles.Sum(c=>c.OcrCacheHits)==snapshot.Values.OcrResultCacheHits,"Cycle cache counts were lost during merge");
        Check(snapshot.Values.RecognitionInferences==6 && snapshot.Values.RecognitionCacheHits==4
            && snapshot.Cycles.Sum(c=>c.RecognitionInferences)==6 && snapshot.Cycles.Sum(c=>c.RecognitionCacheHits)==4,"Recognizer counts duplicated on frame-cache hit or lost on merge");
        Check(snapshot.Values.OcrResultCacheMisses.Empty==1 && snapshot.Values.OcrResultCacheMisses.InputNotCached==1
            && snapshot.Cycles.Sum(c=>c.OcrCacheMisses.InputNotCached)==1,"Frame miss reason totals disagree with actual OCR");
        string report=DebugReportWriter.Format(snapshot,"test",new string('a',40));
        Check(report.Contains("OcrResultCacheHits:") && report.Contains("OCR_RESULT_CACHE_HIT") && report.Contains("RecognitionCacheMisses=")
            && report.Contains("InputNotCached") && !report.Contains("body0"),"Cache report absent or exposed source text");
        await pipeline.RequestManualAsync();await Until(()=>engine.Calls==3 && seen.Max(u=>u.OcrRequests)==3);
        Check(engine.Calls==3,"Manual OCR did not bypass populated cache");
        Volatile.Write(ref phase,2);await Until(()=>seen.Any(u=>u.Original=="body2"));
        Check(engine.Calls==4,"Changed character pixels reused stale text");
    }
    private static async Task Until(Func<bool> ready)
    { var watch=Stopwatch.StartNew();while(!ready()) {if(watch.ElapsedMilliseconds>7000)throw new Exception("OCR cache pipeline timed out");await Task.Delay(5);} }
}
