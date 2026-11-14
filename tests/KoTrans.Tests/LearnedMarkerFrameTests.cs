using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class LearnedMarkerFrameTests
{
    private static int checks;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    private static readonly PixelRect Body=new(20,30,220,32),Marker=new(260,38,18,18);
    private const string Text="今日はとてもよい天気です。";
    private static ImageFrame Frame(int phase=0,bool nextRow=false,bool hidden=false,PixelRect? markerBounds=null)
    {
        byte[] pixels=new byte[500*240*4];
        for(int y=Body.Y+5;y<Body.Y+Body.Height-5;y++)for(int x=Body.X+3;x<Body.X+Body.Width-3;x++)if((x+y)%5<2)Set(x,y,230,230,230);
        var marker=markerBounds??(nextRow?new PixelRect(30,76,18,18):Marker);
        if(!hidden)for(int y=2;y<marker.Height-2;y++)for(int x=2;x<marker.Width-2;x++)if(x is >=6 and <12 || y is >=6 and <12)
            Set(marker.X+x,marker.Y+y,(byte)(180+phase%70),(byte)(phase%2==0?110:170),210);
        void Set(int x,int y,byte r,byte g,byte b){int i=(y*500+x)*4;pixels[i]=b;pixels[i+1]=g;pixels[i+2]=r;}
        return new(500,240,2000,pixels);
    }
    private static ImageFrame Edit(ImageFrame frame,int x,int y)
    {var p=frame.Pixels.ToArray();int i=y*frame.Stride+x*4;p[i]=p[i+1]=p[i+2]=255;return new(frame.Width,frame.Height,frame.Stride,p);}
    private static ImageFrame Scale(ImageFrame frame,int factor)
    {
        int width=frame.Width*factor,height=frame.Height*factor;var pixels=new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            frame.Pixels.Span.Slice(y/factor*frame.Stride+x/factor*4,4).CopyTo(pixels.AsSpan((y*width+x)*4,4));
        return new(width,height,width*4,pixels);
    }
    private static OcrResult Result(ImageFrame frame,string text=Text)=>new([new(text,.99f,Body)],TimeSpan.Zero)
        {Candidates=[new(text,.99f,Body)],DetectedBoxes=[Body],TerminalMarkers=TerminalMarkerScanner.Scan(frame,[new(text,.99f,Body)])};
    private static GameMarkerPattern Pattern(ImageFrame frame)
    {
        var sample=Result(frame).TerminalMarkers!.Single();
        return new(sample.Location.Anchor,sample.Location.X,sample.Location.Y,sample.Location.Width,sample.Location.Height,
            [sample.Visual with {Red=215,Green=140,Blue=210}]);
    }
    internal static async Task Run()
    {
        Geometry();EmptyPadding();VerifiedProposal();RestoreCrop();await RoiCoordinates();await Totals();await QuietCandidate();await PipelineComparison();
        Console.WriteLine($"PASS: {checks} learned-marker frame gate/counters/privacy/session assertions; synthetic frames, Mock OCR, no desktop or AI.");
    }
    private static void Geometry()
    {
        var a=Frame();var b=Frame(1);var patterns=new[]{Pattern(a)};var gate=new LearnedMarkerFrameGuard();
        gate.Observe(a,Result(a),patterns,true);
        Check(gate.Evaluate(a,a,patterns)==MarkerFrameDecision.Unchanged,"Identical frame counted as a marker change");
        Check(gate.Evaluate(a,b,patterns)==MarkerFrameDecision.Reused,"Known marker-only colour/antialias changes were not reused");
        Check(gate.Evaluate(a,b,patterns)==MarkerFrameDecision.Unchanged,"Repeated identical marker frame inflated skip count");
        Check(gate.Evaluate(a,a,patterns)==MarkerFrameDecision.Reused,"Returning to the accepted marker pose was not reused");
        Check(gate.Evaluate(a,Edit(b,70,45),patterns)==MarkerFrameDecision.BodyChanged,"Changed body was hidden by the marker gate");
        Check(gate.Evaluate(a,b,patterns)==MarkerFrameDecision.Unavailable,"Body change did not invalidate the binding");
        gate.Observe(a,Result(a),patterns,true);
        Check(gate.Evaluate(a,Edit(b,269,47),patterns)==MarkerFrameDecision.Uncertain,"A new glyph inside the marker's filled area passed on silhouette alone");
        gate.Observe(a,Result(a),patterns,true);
        Check(gate.Evaluate(a,Edit(b,285,63),patterns)==MarkerFrameDecision.BodyChanged,"New stroke inside the margin but outside sprite support was hidden");
        gate.Observe(a,Result(a),patterns,true);
        Check(gate.Evaluate(a,Edit(b,400,190),patterns)==MarkerFrameDecision.BodyChanged,"An outside scene change was ignored");
        gate.Observe(a,Result(a),patterns,false);
        Check(gate.Evaluate(a,b,patterns)==MarkerFrameDecision.Unavailable,"Partial OCR armed a full-frame exclusion");
        gate.Observe(a,Result(a),patterns,true);
        Check(gate.Evaluate(a,Frame(hidden:true),patterns)==MarkerFrameDecision.Uncertain,"Unverified invisible pose was treated as a wildcard");
        gate.Observe(a,Result(a),patterns,true);
        Check(gate.Evaluate(Frame(),b,patterns)==MarkerFrameDecision.Unavailable,"A different accepted-frame identity reused stale metadata");
        var off=Frame(hidden:true);gate.Observe(a,Result(a),patterns,true);
        var padded=new OcrLine(Text,.99f,Body with {Width=Body.Width+5});
        gate.Observe(off,new([padded],TimeSpan.Zero){Candidates=[padded]},patterns,true);
        Check(gate.Evaluate(off,b,patterns)==MarkerFrameDecision.Reused,"Clean body detector padding blocked a verified blink-off phase");
        gate.Observe(a,Result(a),patterns,true);
        var altered=padded with {Text="今日は違う天気です。"};
        gate.Observe(off,new([altered],TimeSpan.Zero){Candidates=[altered]},patterns,true);
        Check(gate.Evaluate(off,b,patterns)==MarkerFrameDecision.Unavailable,"Different text in the padded box certified an empty indicator");
        gate.Observe(a,Result(a),patterns,true);
        var extended=padded with {Bounds=padded.Bounds with {Width=Marker.X-Body.X+2}};
        gate.Observe(off,new([extended],TimeSpan.Zero){Candidates=[extended]},patterns,true);
        Check(gate.Evaluate(off,b,patterns)==MarkerFrameDecision.Unavailable,"A body box crossing the indicator certified an empty phase");
        var next=Frame(nextRow:true);var nextGate=new LearnedMarkerFrameGuard();var nextPatterns=new[]{Pattern(next)};
        nextGate.Observe(next,Result(next),nextPatterns,true);
        Check(nextGate.Evaluate(next,Frame(1,nextRow:true),nextPatterns)==MarkerFrameDecision.Reused,"Following-row marker was not reusable");
        var large=Scale(a,3);var largeBody=new PixelRect(Body.X*3,Body.Y*3,Body.Width*3,Body.Height*3);
        var largeResult=new OcrResult([new(Text,.99f,largeBody)],TimeSpan.Zero);
        var sample=TerminalMarkerScanner.Scan(large,largeResult.Lines).Single();
        var largePatterns=new[]{new GameMarkerPattern(sample.Location.Anchor,sample.Location.X,sample.Location.Y,sample.Location.Width,sample.Location.Height,[sample.Visual])};
        gate.Observe(large,largeResult,largePatterns,true);
        Check(gate.Evaluate(large,Edit(large,Marker.X*3+27,Marker.Y*3+27),largePatterns)==MarkerFrameDecision.Uncertain,
            "A small new stroke between the 16x16 samples of a scaled marker was hidden");
    }
    private static void EmptyPadding()
    {
        var position=new PixelRect(250,38,18,18);var on=Frame(markerBounds:position);var off=Frame(hidden:true,markerBounds:position);
        var sample=Result(on).TerminalMarkers!.Single();var patterns=new[]{Pattern(on)};var gate=new LearnedMarkerFrameGuard();
        var padded=new OcrLine(Text,.99f,Body with {Width=sample.Bounds.X+2-Body.X});
        var paddedResult=new OcrResult([padded],TimeSpan.Zero){Candidates=[padded]};
        gate.Observe(on,Result(on),patterns,true);gate.Observe(off,paddedResult,patterns,true);
        Check(gate.IsReady && gate.Evaluate(off,on,patterns)==MarkerFrameDecision.Reused,"Verified disappearing sprite was confused with empty detector padding");
        gate.Observe(off,paddedResult,patterns,true);
        Check(gate.IsReady,"Previously verified empty padding was lost on another observation");
        var roundedPixels=off.Pixels.ToArray();roundedPixels[42*off.Stride+255*4]=1;
        var roundedOff=new ImageFrame(off.Width,off.Height,off.Stride,roundedPixels);
        gate.Observe(roundedOff,paddedResult,patterns,true);
        Check(gate.IsReady && gate.Evaluate(roundedOff,on,patterns)==MarkerFrameDecision.Reused,"Fresh OCR of a rounded empty pose lost the known disappearance");
        roundedPixels=off.Pixels.ToArray();roundedPixels[42*off.Stride+255*4]=20;
        gate.Observe(new(off.Width,off.Height,off.Stride,roundedPixels),paddedResult,patterns,true);
        Check(!gate.IsReady,"An unrecognized changed pixel passed through an already empty padding state");
        gate.Observe(on,Result(on),patterns,true);
        var changed=padded with {Text=Text+"A"};
        gate.Observe(off,paddedResult with {Lines=[changed],Candidates=[changed]},patterns,true);
        Check(!gate.IsReady && gate.Failure==MarkerFrameFailure.ProtectedTextOverlap,"A real appended letter passed the padding exception");
        gate.Observe(on,Result(on),patterns,true);
        var extra=new OcrLine("A",.99f,sample.Bounds);
        gate.Observe(off,paddedResult with {Candidates=[padded,extra]},patterns,true);
        Check(!gate.IsReady,"Another raw text candidate was hidden by the padding exception");
        gate.Observe(on,Result(on),patterns,true);
        var tooWide=padded with {Bounds=padded.Bounds with {Width=padded.Bounds.Width+8}};
        gate.Observe(off,paddedResult with {Lines=[tooWide],Candidates=[tooWide]},patterns,true);
        Check(!gate.IsReady,"A wide box crossing the sprite was accepted as padding");
        gate.Observe(on,Result(on),patterns,true);
        gate.Observe(Edit(off,245,34),paddedResult,patterns,true);
        Check(!gate.IsReady && gate.Failure==MarkerFrameFailure.UnsupportedPixelChanged,"A new stroke outside verified sprite support was ignored");
        gate.Observe(on,Result(on),patterns,true);
        gate.Observe(Edit(off,70,45),paddedResult,patterns,true);
        Check(!gate.IsReady && gate.Failure==MarkerFrameFailure.OutsideChanged,"Changed body pixels were hidden as padding");
        gate.Observe(on,Result(on),patterns,true);gate.Observe(off,paddedResult,patterns,false);
        Check(!gate.IsReady,"Partial OCR certified a widened empty body box");
        var remaining=Frame(1,markerBounds:position);
        gate.Observe(on,Result(on),patterns,true);
        // Prevent a fresh scanner match from turning this into a normal pose.
        // Exercise the disappearance proof directly with a known remaining icon.
        var method=typeof(LearnedMarkerFrameGuard).GetMethod("KnownMarkerDisappeared",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        object?[] args=[on,remaining,MarkerFrameFailure.None];
        Check(!(bool)method.Invoke(gate,args)!,"A remaining character-shaped sprite certified disappearance");
    }
    private static void VerifiedProposal()
    {
        var a=Frame();var sample=Result(a).TerminalMarkers!.Single();var pattern=Pattern(a);
        ImageFrame WithBackground(ImageFrame frame) {
            var p=frame.Pixels.ToArray();
            foreach(var region in new[]{new PixelRect(244,35,6,6),new PixelRect(251,51,6,6)})
                for(int y=region.Y;y<region.Y+region.Height;y++)for(int x=region.X;x<region.X+region.Width;x++) {
                    int i=y*frame.Stride+x*4;p[i]=190;p[i+1]=90;p[i+2]=235;
                }
            return new(frame.Width,frame.Height,frame.Stride,p);
        }
        var crowded=WithBackground(a);var result=Result(crowded) with {TerminalMarkers=[sample]};
        Check(!TerminalMarkerScanner.Scan(crowded,result.Lines).Any(s=>GameMarkerGeometry.AppearanceMatches(pattern,s)),
            "Synthetic background did not reproduce candidate crowding");
        var gate=new LearnedMarkerFrameGuard();gate.Observe(crowded,result,[pattern],true);
        Check(gate.Failure==MarkerFrameFailure.None,"A native-verified marker was lost during rediscovery");
        Check(gate.Evaluate(crowded,WithBackground(Frame(1)),[pattern])==MarkerFrameDecision.Reused,
            "Background discovery displaced a bound, verified marker");
        Check(gate.Evaluate(crowded,Edit(WithBackground(Frame(1)),70,45),[pattern])==MarkerFrameDecision.BodyChanged,
            "Using a verified proposal hid a changed body pixel");
    }
    private static void RestoreCrop()
    {
        var frame=Frame();var crop=new PixelRect(10,20,300,140);var input=OcrRegionTracker.Crop(frame,crop);
        var body=Body with {X=Body.X-crop.X,Y=Body.Y-crop.Y};var line=new OcrLine(Text,.99f,body);
        var result=new OcrResult([line],TimeSpan.Zero){Candidates=[line],DetectedBoxes=[body],
            TerminalMarkers=TerminalMarkerScanner.Scan(input,[line]),
            MarkerDiagnostics=[OcrMarkerDiagnostics.Describe(line,OcrMarkerStage.JoinedTail,OcrMarkerDecision.CropAccepted,line)]};
        var restored=OcrRegionTracker.Restore(result,crop,frame,new("ja","ADV",false));
        var expected=Result(frame).TerminalMarkers!.Single();var actual=restored.TerminalMarkers!.Single();
        Check(actual==expected,"Restoring a crop lost marker/parent coordinates or changed relative placement");
        var diagnostic=restored.MarkerDiagnostics!.Single();
        Check(diagnostic.Bounds==Body && diagnostic.ParentBounds==Body,
            "Marker diagnostics retained crop-local coordinates");
        Check(!OcrRegionTracker.NeedsFullSearch(restored,crop,true),"A valid absolute ROI was treated as clipped");
        Check(OcrRegionTracker.NeedsFullSearch(restored with {DetectedBoxes=[new(crop.X+2,crop.Y+10,20,20)]},crop,true),
            "Absolute ROI boundary clipping did not request a full search");
        var gate=new LearnedMarkerFrameGuard();gate.Observe(frame,restored,[Pattern(frame)],true);
        Check(gate.Failure==MarkerFrameFailure.None && gate.Evaluate(frame,Frame(1),[Pattern(frame)])==MarkerFrameDecision.Reused,
            "A restored native marker could not arm frame reuse");
    }
    private static async Task RoiCoordinates()
    {
        foreach(var mode in new[]{MarkerLearningMode.Observe,MarkerLearningMode.Apply,MarkerLearningMode.Off}) {
            var body=new PixelRect(320,330,440,32);int changed=0;
            ImageFrame Capture() {
                var small=Frame();var pixels=new byte[1000*600*4];
                for(int y=0;y<small.Height;y++)small.Pixels.Span.Slice(y*small.Stride,small.Width*4).CopyTo(pixels.AsSpan(((y+300)*1000+300)*4,small.Width*4));
                if(Volatile.Read(ref changed)!=0)for(int y=340;y<355;y++)for(int x=370;x<400;x++) {
                    int i=(y*1000+x)*4;pixels[i]=pixels[i+1]=pixels[i+2]=255;
                }
                return new(1000,600,4000,pixels);
            }
            var mock=new RoiOcr(body);var service=new MarkerLearningOcrService(mock,mode,new(mode,[]),
                new(Path.Combine(Path.GetTempPath(),"unused-marker-roi"),new string('D',64)),_=>{});
            var seen=new ConcurrentQueue<PipelineUpdate>();
            await using(var pipeline=new TranslationPipeline(service,null,_=>Task.FromResult(Capture()),new("ja","ADV",false),new(),new(),"",
                interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.Zero,translationEnabled:false){SuppressRepeatedAnimations=false}) {
                pipeline.Updated+=seen.Enqueue;pipeline.Start(true);await Until(()=>seen.Any(u=>u.Original==Text));
                Volatile.Write(ref changed,1);await Until(()=>mock.Requests.Count>=2);
                var request=mock.Requests.Last();
                if(mode!=MarkerLearningMode.Off) {
                    Check(request.Width==1000 && request.Height==600 && request.Area is {X:>0,Y:>0},
                        "Tracked ROI changed the coordinate space used by marker learning");
                    Check(request.ReferenceWidth==1000,"Tracked ROI discarded the full-frame clean-body reference");
                } else Check(request.Width<1000 && request.Area is null,"Learning disabled changed the original crop path");
            }
            await service.StopAsync();
        }
    }
    private sealed class RoiOcr(PixelRect body):IOcrService
    {
        public readonly ConcurrentQueue<(int Width,int Height,PixelRect? Area,int? ReferenceWidth)> Requests=new();
        public OcrCapabilities Capabilities=>OcrCapabilities.Paddle;
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)
        {
            Requests.Enqueue((frame.Width,frame.Height,options.SearchArea,options.MarkerReference?.Width));
            var b=frame.Width==1000?body:body with {X=64,Y=64};var line=new OcrLine(Text,.99f,b);
            return Task.FromResult(new OcrResult([line],TimeSpan.Zero){Candidates=[line],DetectedBoxes=[b],TerminalMarkers=TerminalMarkerScanner.Scan(frame,[line])});
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static async Task QuietCandidate()
    {
        async Task<int> Run(MarkerLearningMode mode,bool changeBody=false) {
            int captured=0,source=0;var seen=new ConcurrentQueue<PipelineUpdate>();var mock=new MockOcr {DelayMs=60};
            var service=new MarkerLearningOcrService(mock,mode,new(mode,[Pattern(Frame())]),
                new(Path.Combine(Path.GetTempPath(),"unused-marker-quiet"),new string('C',64)),_=>{});
            var telemetry=new DebugTelemetry();
            await using(var pipeline=new TranslationPipeline(service,null,_=> {
                int phase=Interlocked.Increment(ref captured);var f=Frame((phase-1)/3);
                if(Volatile.Read(ref source)!=0)f=Edit(f,70,45);
                return Task.FromResult(f);
            },new("ja","ADV",false),new(),new(),"",interval:TimeSpan.FromMilliseconds(8),settle:TimeSpan.FromMilliseconds(700),
                translationEnabled:false,diagnostics:telemetry){SuppressRepeatedAnimations=false}) {
                pipeline.Updated+=seen.Enqueue;pipeline.Start(true);
                await Until(()=>telemetry.Snapshot().Events.Any(e=>e.Code==DebugCode.WAIT_STABLE));
                Check(!seen.Any(u=>!string.IsNullOrEmpty(u.Original)),"Candidate text escaped before the quiet interval");
                if(changeBody)Volatile.Write(ref source,1);
                string expected=changeBody?"静かな図書館で本を読みます。":Text;
                await Until(()=>seen.Any(u=>u.Original==expected));
                int after=Volatile.Read(ref captured);await Until(()=>Volatile.Read(ref captured)>=after+20);
                if(changeBody)Check(!seen.Any(u=>u.Original==Text),"The superseded quiet candidate was published after a body change");
                else if(mode==MarkerLearningMode.Apply)Check(mock.Calls==1,"Known marker changes during quiet wait repeated OCR or left stale queued work");
            }
            await service.StopAsync();return mock.Calls;
        }
        int normal=await Run(MarkerLearningMode.Observe),apply=await Run(MarkerLearningMode.Apply);
        Check(apply<normal,"The learned gate did not reduce OCR during a nonzero quiet wait");
        await Run(MarkerLearningMode.Apply,true);
        Console.WriteLine($"Learned marker with active quiet wait: OCR {normal}->{apply}; synthetic pixels and Mock OCR only.");
    }
    private static async Task Totals()
    {
        var a=Frame();var pattern=Pattern(a);string root=Path.Combine(Path.GetTempPath(),"KoTrans.MarkerTotals",Guid.NewGuid().ToString("N"));
        var store=new MarkerLearningStore(root,new string('A',64));var mock=new MockOcr();
        var service=new MarkerLearningOcrService(mock,MarkerLearningMode.Apply,new(MarkerLearningMode.Apply,[pattern]),store,_=>{});
        foreach(var (excluded,extra) in new[]{(2,1),(0,0),(1,2)}) {
            mock.Next=Result(a) with {LearnedMarkerExclusions=excluded,MarkerBodyChecks=extra,MarkerBodyCacheHits=2};
            await service.RecognizeAsync(a,new("ja","ADV",false),CancellationToken.None);
        }
        Check(service.LearningStatus.ExcludedTotal==3 && service.LearningStatus.ExcludingOcrCalls==2 && service.LearningStatus.ExtraBodyChecks==3,"Actual exclusions/affected calls/body checks are not accumulated separately");
        Check(service.LearningStatus.BodyCheckCacheHits==6,"Cached body verification was counted as new recognition");
        service.ObserveAcceptedFrame(a,Result(a),true);service.EvaluateMarkerFrame(a,Frame(1));service.EvaluateMarkerFrame(a,Frame(1));
        Check(service.LearningStatus.SkippedFrames==1,"Duplicate marker pose inflated the cumulative skip counter");
        service.ObserveAcceptedFrame(a,Result(a),false);
        Check(service.LearningStatus.FramePreparations==1 && service.LearningStatus.FramePreparationFailures==1
            && service.LearningStatus.FrameFailure==MarkerFrameFailure.PartialCoverage,"Frame preparation diagnostics do not separate success from incomplete recognition");
        service.ResetMarkerFrames();
        Check(service.LearningStatus.FrameDecision==MarkerFrameDecision.Unavailable && service.LearningStatus.FrameFailure==MarkerFrameFailure.NoVerifiedFrame,
            "Capture pause retained a stale reused marker decision");
        service.ObserveAcceptedFrame(a,Result(a),true);
        service.EvaluateMarkerFrame(a,Edit(Frame(1),400,190));
        Check(service.LearningStatus.RecheckFrames==1,"Fallback transition was not counted");
        var telemetry=new DebugTelemetry();long owner=telemetry.BeginSession(new("ja","ADV",false),new(),0);
        telemetry.MarkerLearning(owner,service.LearningStatus);
        string report=DebugReportWriter.Format(telemetry.Snapshot(),"test",new string('a',40));
        Check(report.Contains("LearnedMarkerExcludedTotal: 3") && report.Contains("LearnedMarkerSkippedFrames: 1") && report.Contains("LearnedMarkerExtraBodyChecks: 3") && report.Contains("LearnedMarkerBodyCacheHits: 6") && !report.Contains(Text),"Saved debug report lost cumulative counters or leaked source text");
        telemetry.Stop(owner);telemetry.MarkerLearning(owner,service.LearningStatus with {ExcludedTotal=99});
        Check(telemetry.Snapshot().Values.LearnedMarkerExcludedTotal==3,"Late update overwrote the stopped session totals");
        long fresh=telemetry.BeginSession(new("ja","ADV",false),new(),0);telemetry.MarkerLearning(owner,service.LearningStatus);
        Check(telemetry.Snapshot().Values.LearnedMarkerExcludedTotal==0 && fresh!=owner,"New session retained counters or accepted the old owner");
        await service.StopAsync();Check(service.EvaluateMarkerFrame(a,Frame(1))==MarkerFrameDecision.Unavailable,"Stopped session suppressed OCR");
        var observe=new MarkerLearningOcrService(mock,MarkerLearningMode.Observe,new(MarkerLearningMode.Observe,[pattern]),store,_=>{});
        observe.ObserveAcceptedFrame(a,Result(a),true);Check(observe.EvaluateMarkerFrame(a,Frame(1))==MarkerFrameDecision.Unavailable,"Observe-only mode suppressed OCR");await observe.StopAsync();
        Check(!Directory.Exists(root),"Counter recording wrote extra game data to disk");
    }
    private static async Task PipelineComparison()
    {
        var normal=await RunPipeline(MarkerLearningMode.Observe);var apply=await RunPipeline(MarkerLearningMode.Apply);
        Check(apply.Calls<normal.Calls,"Applying the learned gate did not reduce actual Mock OCR calls");
        Check(apply.Skips>0 && normal.Skips==0,"Apply/observe skip totals are wrong");
        Console.WriteLine($"Learned marker A/B: OCR {normal.Calls}->{apply.Calls}, learned-only changed frames skipped={apply.Skips}. Synthetic scenario, not a hardware benchmark.");
        var noisyNormal=await RunPipeline(MarkerLearningMode.Observe,true);var noisyApply=await RunPipeline(MarkerLearningMode.Apply,true);
        Check(noisyApply.Calls<noisyNormal.Calls && noisyApply.Skips>0,"Capture rounding prevented reduction in actual Mock OCR calls");
        Console.WriteLine($"Learned marker with one-level capture rounding A/B: OCR {noisyNormal.Calls}->{noisyApply.Calls}; synthetic Mock comparison only.");
    }
    private static async Task<(int Calls,long Skips)> RunPipeline(MarkerLearningMode mode,bool rounding=false)
    {
        int captured=0,source=0;var seen=new ConcurrentQueue<PipelineUpdate>();var first=Frame();var mock=new MockOcr();
        var store=new MarkerLearningStore(Path.Combine(Path.GetTempPath(),"unused-marker-store"),new string('B',64));
        var service=new MarkerLearningOcrService(mock,mode,new(mode,[Pattern(first)]),store,_=>{});
        var telemetry=new DebugTelemetry();
        await using var pipeline=new TranslationPipeline(service,null,_=> {
            int phase=Interlocked.Increment(ref captured);var f=Frame((phase-1)/3);
            if(rounding) {
                var p=f.Pixels.ToArray();p[10*f.Stride+(300+phase%100)*4]=1;
                f=new(f.Width,f.Height,f.Stride,p);
            }
            if(Volatile.Read(ref source)!=0) {
                var p=f.Pixels.ToArray();for(int y=45;y<53;y++)for(int x=70;x<77;x++) {int i=y*f.Stride+x*4;p[i]=p[i+1]=p[i+2]=255;}
                f=new(f.Width,f.Height,f.Stride,p);
            }
            return Task.FromResult(f);
        },new("ja","ADV",false),new(),new(),"",interval:TimeSpan.FromMilliseconds(8),settle:TimeSpan.Zero,translationEnabled:false,diagnostics:telemetry) {SuppressRepeatedAnimations=false};
        pipeline.Updated+=seen.Enqueue;pipeline.Start(true);
        Check(telemetry.Snapshot().Values.MarkerLearningMode==mode && telemetry.Snapshot().Values.LearnedMarkerPatterns==1,"Initial waiting state lost the loaded learning mode/pattern count");
        await Until(()=>seen.Any(u=>u.Original==Text));int start=Volatile.Read(ref captured);
        await Until(()=>Volatile.Read(ref captured)>=start+45);int calls=mock.Calls;long skips=service.LearningStatus.SkippedFrames;
        Volatile.Write(ref source,1);await Until(()=>seen.Any(u=>u.Original=="静かな図書館で本を読みます。"));
        Check(mock.Calls>calls,"A new body was never recognized after the skip period");
        Check(telemetry.Snapshot().Values.LearnedMarkerSkippedFrames>=skips,"Pipeline did not publish current-session skip counters");
        await pipeline.DisposeAsync();
        Check(telemetry.Snapshot().Values.LearnedMarkerFrameDecision==MarkerFrameDecision.Unavailable
            && telemetry.Snapshot().Values.LearnedMarkerSkippedFrames>=skips,"Stopped report retained a reused marker decision or lost totals");
        await service.StopAsync();return(calls,skips);
    }
    private static async Task Until(Func<bool> condition)
    {var clock=Stopwatch.StartNew();while(!condition()){if(clock.Elapsed>TimeSpan.FromSeconds(6))throw new TimeoutException("Learned frame test did not progress");await Task.Delay(5);}}
    private sealed class MockOcr:IOcrService
    {
        public int Calls,DelayMs;public OcrResult? Next;
        public OcrCapabilities Capabilities=>OcrCapabilities.Basic;
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)
        {
            Interlocked.Increment(ref Calls);if(DelayMs>0)await Task.Delay(DelayMs,token);if(Next is not null)return Next;
            int i=45*frame.Stride+70*4;bool changed=frame.Pixels.Span[i]==255 && frame.Pixels.Span[i+1]==255 && frame.Pixels.Span[i+2]==255;
            return Result(frame,changed?"静かな図書館で本を読みます。":Text);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
