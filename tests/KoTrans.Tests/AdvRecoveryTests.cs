using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using System.Diagnostics;
internal static class AdvRecoveryTests
{
    public static async Task Run() {
        int n=0; void Check(bool ok,string why){if(!ok)throw new Exception(why);n++;}
        var frame=new ImageFrame(1651,850,6604,new byte[6604*850]);
        OcrLine[] names=[new("Old",1,new(330,76,80,46)),new("Warrior",1,new(289,123,163,48))];
        var heading=new OcrResult(names,TimeSpan.Zero){Candidates=names};
        var body=new OcrLine("...ngh...",.95f,new(139,205,107,45));
        var detected=heading with{Candidates=names.Append(body).ToArray()};
        var selected=AdvTextRecovery.IncludeNearby(frame,detected,detected);
        Check(selected.Text.Contains("ngh") && selected.Lines.Count==3,"ADV discarded a nearby detected short line");
        Check(heading.Text=="Old\nWarrior","Recovery mutated the original result");
        var tracker=new AdvTextRecovery(); var area=tracker.Retry(frame,heading,0);
        Check(area is not null && area.Value.X<=body.Bounds.X && area.Value.Y+area.Value.Height>=body.Bounds.Y+45,"Speaker focus missed nearby short text");
        Check(tracker.Retry(frame,heading,0) is null,"Failed retry repeated for unchanged source");
        Check(tracker.Retry(frame,heading,1) is not null,"New game input did not permit one retry");
        tracker.Observe(frame,selected,true);
        Check(tracker.Select(frame,false) is not null && tracker.Select(frame,true) is null,"ADV focus/manual full search policy");
        Check(tracker.Select(new ImageFrame(800,600,3200,new byte[1920000]),false) is null,"Resize retained stale focus");
        Check(!AdvTextRecovery.Supports(new("ja","ADV",false)) && !AdvTextRecovery.Supports(new("en","NVL",false)),"Recovery changed unrelated modes");
        Check(!AdvTextRecovery.Clipped(heading with {DetectedBoxes=[new(0,0,20,20)]},new(0,0,700,500)),"Unrecognized edge decoration forced full OCR");
        Check(AdvTextRecovery.Clipped(new([new("edge",1,new(0,20,50,30))],TimeSpan.Zero),new(0,0,700,500)),"Clipped readable text failed to reacquire full frame");
        await using(var unconfigured=new OcrServiceFactory(OcrEngineCatalog.Default,"missing-test-models").Create("paddle")) {
            bool rejected=false; try{await unconfigured.RecognizeAsync(frame,new("en","ADV",false,SearchArea:new(-1,0,100,100)),default);}catch(ArgumentException){rejected=true;}
            Check(rejected && unconfigured.State==OcrState.NotConfigured,"Invalid focus loaded models or reached native crop");
        }
        var uncertain=new OcrResult([body with { Text="...ng...", Confidence=.7f }],TimeSpan.Zero);
        var corrected=AdvTextRecovery.IncludeNearby(frame,uncertain,new([body],TimeSpan.Zero));
        Check(corrected.Text==body.Text && corrected.Lines.Count==1,"Stronger local reading did not replace uncertain short text");
        Check(AdvTextRecovery.IncludeNearby(frame,new([body],TimeSpan.Zero),uncertain).Text==body.Text,"Weaker reading replaced strong short text");
        Check(AdvTextRecovery.IncludeNearby(frame,heading,heading).Text==heading.Text,"Failed retry changed original result");
        Check(new AdvTextRecovery().Retry(frame,new([body],TimeSpan.Zero),0) is null,"Confident short text retried unnecessarily");
        Check(new AdvTextRecovery().Retry(frame,new([body with {Text=new string('a',80), Confidence=.5f}],TimeSpan.Zero),0) is null,"Normal paragraph enabled high-resolution retry");
        var far=body with {Bounds=new(1250,700,107,45)};
        Check(AdvTextRecovery.IncludeNearby(frame,heading,heading with {Candidates=names.Append(far).ToArray()}).Text==heading.Text,"Unrelated remote text entered the ADV paragraph");
        tracker.Observe(frame,selected,true);
        for(int i=0;i<4;i++) tracker.Observe(frame,selected,false);
        Check(tracker.Select(frame,false) is null,"Periodic full search did not escape a tracked paragraph");
        var fake=new Fake(); var ai=new Ai(); var qa=new GameQaRecorder();
        var diagnostics=new DebugTelemetry();
        await using(var pipe=new TranslationPipeline(fake,ai,_=>Task.FromResult(frame),new("en","ADV",false),new(),new(),"test",settle:TimeSpan.Zero,gameQa:qa,qaProfile:GameQaProfile.From(new()),diagnostics:diagnostics)) {
            pipe.Start(false); await pipe.RequestManualAsync(image:frame);
            var clock=Stopwatch.StartNew(); while(ai.Calls==0){if(clock.ElapsedMilliseconds>3000)throw new Exception("Recovery pipeline timed out");await Task.Delay(5);}
            Check(fake.Retries==1 && ai.Last.Contains("ngh"),"Conditional local retry did not reach AI");
            Check(diagnostics.Snapshot().Values.LogicalOcrCalls==2 && diagnostics.Snapshot().Current!.OcrCalls==2,"ADV normal/recovery calls not assigned to one cycle");
            Check(qa.Snapshot!.Frame.Width==1651 && qa.Snapshot.Record.Options.SearchArea is not null && qa.Snapshot.Record.Options.DetectionLongSide==1280,"QA lost actual focused OCR options/full input");
        }
        foreach(bool error in new[]{false,true}) {
            var failed=new Fake { NoRecovery=true, FailRetry=error }; var fallbackAi=new Ai(); var fallbackQa=new GameQaRecorder();
            await using var pipe=new TranslationPipeline(failed,fallbackAi,_=>Task.FromResult(frame),new("en","ADV",false),new(),new(),"test",settle:TimeSpan.Zero,gameQa:fallbackQa,qaProfile:GameQaProfile.From(new()));
            pipe.Start(false); await pipe.RequestManualAsync(image:frame);
            var timer=Stopwatch.StartNew(); while(fallbackAi.Calls==0) {if(timer.ElapsedMilliseconds>3000)throw new Exception("Fallback pipeline timed out");await Task.Delay(5);}
            Check(failed.Retries==1 && fallbackAi.Last==heading.Text,"Optional retry failure lost original completed OCR");
            Check(fallbackQa.Snapshot!.Record.Options.SearchArea is null && fallbackQa.Snapshot.Record.Options.DetectionLongSide==0,"Failed attempt recorded as accepted high-resolution result");
        }
        var moving=new MovingOcr(); var movingAi=new Ai(); var movingQa=new GameQaRecorder();
        var observed=frame;
        await using(var pipe=new TranslationPipeline(moving,movingAi,_=>Task.FromResult(Volatile.Read(ref observed)),new("en","ADV",false),new(),new(),"test",TimeSpan.FromMilliseconds(10),TimeSpan.Zero,movingQa,GameQaProfile.From(new()))) {
            async Task Until(Func<bool> condition) {var t=Stopwatch.StartNew();while(!condition()){if(t.ElapsedMilliseconds>3000)throw new Exception("ADV tracking transition timed out");await Task.Delay(5);}}
            ImageFrame Changed(byte value) => new(frame.Width,frame.Height,frame.Stride,Enumerable.Repeat(value,frame.Stride*frame.Height).ToArray());
            pipe.Start(true); await Until(()=>movingAi.Calls>=1);
            Volatile.Write(ref observed,Changed(60)); await Until(()=>movingAi.Last.EndsWith("60"));
            Check(moving.Local>0 && movingQa.Snapshot!.Record.Options.SearchArea is not null,"Stable ADV did not follow the previous paragraph");
            Volatile.Write(ref observed,Changed(120)); await Until(()=>movingAi.Last.EndsWith("120"));
            Check(moving.EmptyLocal>0 && movingQa.Snapshot!.Record.Options.SearchArea is null,"Moved paragraph failed to fall back to a full search");
            Check(movingQa.Snapshot!.Record.Ocr!.Lines[0].Bounds.X==1200,"Full fallback lost moved paragraph coordinates");
            Check(moving.High==0,"Normal paragraph tracking enabled high-resolution OCR");
        }
        Console.WriteLine($"PASS: {n} ADV short recovery assertions; synthetic/Mock only.");
    }
    private sealed class Fake:IOcrService {
        public int Retries; public bool NoRecovery, FailRetry; public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t) {
            OcrLine[] names=[new("Old",1,new(330,76,80,46)),new("Warrior",1,new(289,123,163,48))];
            if(o.DetectionLongSide>0){Retries++;if(FailRetry)throw new IOException("synthetic optional retry failure"); if(NoRecovery)return Task.FromResult(new OcrResult(names,TimeSpan.Zero){Candidates=names}); var all=names.Append(new OcrLine("...ngh...",.96f,new(139,205,107,45))).ToArray();return Task.FromResult(new OcrResult(all,TimeSpan.Zero){Candidates=all});}
            return Task.FromResult(new OcrResult(names,TimeSpan.Zero){Candidates=names});
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class MovingOcr:IOcrService {
        public int Local,EmptyLocal,High; public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t) {
            int value=f.Pixels.Span[0]; if(o.DetectionLongSide>0)Interlocked.Increment(ref High);
            if(o.SearchArea is not null) {Interlocked.Increment(ref Local); if(value==120){Interlocked.Increment(ref EmptyLocal);return Task.FromResult(new OcrResult([],TimeSpan.Zero));}}
            var lines=new[]{new OcrLine("A complete normal dialogue paragraph with sufficient context "+value,1,new(value==120?1200:300,200,350,40))};
            return Task.FromResult(new OcrResult(lines,TimeSpan.Zero){Candidates=lines});
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Ai:ITranslationService {public int Calls;public string Last="";public Task<TranslationResult> TranslateAsync(TranslationRequest r,CancellationToken t){Last=r.Text;Interlocked.Increment(ref Calls);return Task.FromResult(new TranslationResult("mock",TimeSpan.Zero,0));}}
}
