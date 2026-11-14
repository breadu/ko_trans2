using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class GeneralMarkerBlinkTests
{
    private static int checks;
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
    private static readonly string[] Pages=["今日はとてもよい天気です。","静かな図書館で本を読みます。","遠くの山から不思議な音が聞こえます。","懐かしい友達と駅で待ち合わせます。","新しい世界への扉が開かれました。"];
    private static readonly PixelRect Body=new(20,30,220,32);
    private static ImageFrame Frame(int page,bool visible=true)
    {
        var pixels=new byte[400*180*4];
        void Paint(int x,int y){int i=(y*400+x)*4;pixels[i]=pixels[i+1]=pixels[i+2]=240;}
        for(int y=35;y<57;y++)for(int x=23;x<237;x++)if((x+y+page)%5<2)Paint(x,y);
        if(visible)for(int y=0;y<18;y++)for(int x=0;x<18-Math.Abs(y-9);x++)Paint(24+x,76+y);
        return new(400,180,1600,pixels);
    }
    private static OcrResult Result(int page,ImageFrame f)=>new([new(Pages[page],.99f,Body)],TimeSpan.Zero)
        {Candidates=[new(Pages[page],.99f,Body)],TerminalMarkers=TerminalMarkerScanner.Scan(f,[new(Pages[page],.99f,Body)])};
    private static ImageFrame Crop(ImageFrame f,PixelRect r)
    {var p=new byte[r.Width*r.Height*4];for(int y=0;y<r.Height;y++)f.Pixels.Span.Slice((y+r.Y)*f.Stride+r.X*4,r.Width*4).CopyTo(p.AsSpan(y*r.Width*4));return new(r.Width,r.Height,r.Width*4,p);}
    internal static async Task Run()
    {
        BackgroundAndOcrJitter();
        LearningBodyShapes();
        var visible=Frame(0);var sample=Result(0,visible).TerminalMarkers!.Single();
        Check(!sample.Visual.Chromatic,"Text-coloured marker was not observed");
        Check(TerminalMarkerScanner.SeparateNoise(sample,new("ト",.9f,sample.Bounds)),"Learned small neutral icon recognition cannot be excluded");
        Check(!TerminalMarkerScanner.SeparateNoise(sample,new("字",.99f,sample.Bounds with {Height=Body.Height})),"A full-height Japanese glyph was treated as marker noise");
        Check(Result(0,Frame(0,false)).TerminalMarkers!.Count==0,"An invisible marker became a visual wildcard");
        var following=new OcrLine("ここには次の文章があります。",.99f,new(20,74,220,32));
        Check(TerminalMarkerScanner.Scan(visible,[new(Pages[0],.99f,Body),following]).All(s=>!GameMarkerGeometry.Intersects(s.Bounds,following.Bounds)),"Real next-row text was proposed as a marker");
        var learner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int page=0;page<5;page++) {
            long now=page*2000;var on=Frame(page);learner.ObserveOcr(on,Result(page,on),now);
            if(page>=2){learner.ObserveFrame(on,now+600);continue;}
            var probe=learner.NextRegionProbe(now+200);Check(probe is not null,"No bounded observation region was available");
            Check(probe!.Region.Width<=192 && probe.Region.Height<=192,"Fast observation requested a full screen");
            learner.ObserveRegion(probe,Crop(Frame(page,false),probe.Region),now+200);
            learner.ObserveRegion(probe,Crop(on,probe.Region),now+400);
            Check(learner.Confirmed.Count==0,"Fast samples promoted before full-frame body validation");
            learner.ObserveFrame(on,now+600);
        }
        Check(learner.Confirmed.Count==1,"Separate region observation failed when regular frames always saw the same blink phase");
        Check(learner.Status.FastSamples==4 && learner.Status.FastCycles==2,"Fast observation counters are inaccurate");
        Check(GameMarkerGeometry.ValidPatterns(learner.Confirmed),"Neutral patterns cannot be saved or passed to the worker");
        var changed=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        changed.ObserveOcr(visible,Result(0,visible),0);var stale=changed.NextRegionProbe(200)!;
        changed.ObserveRegion(stale,Crop(Frame(0,false),stale.Region),200);changed.ObserveRegion(stale,Crop(visible,stale.Region),400);
        changed.ObserveFrame(Frame(1),600);
        Check(changed.Status.AnimatedPages==0,"Changed body validated an unrelated fast blink");
        long before=changed.Status.FastSamples;changed.ObserveRegion(stale,Crop(visible,stale.Region),700);
        Check(changed.Status.FastSamples==before,"A retired region updated a new observation");
        var staticLearner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int page=0;page<5;page++){var f=Frame(page);staticLearner.ObserveOcr(f,Result(page,f),page*2000);staticLearner.ObserveFrame(f,page*2000+500);}
        Check(staticLearner.Confirmed.Count==0,"Static white symbols were confirmed without blinking");
        var longPlay=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int page=0;page<37;page++) {
            long now=page*2000;var f=Frame(page);string label=new((char)('\u4e00'+page),8);label+="。";
            var r=Result(page%5,f);var line=new OcrLine(label,.99f,Body);
            r=r with {Lines=[line],Candidates=[line],TerminalMarkers=r.TerminalMarkers!.Select(s=>s with {Parent=s.Parent with {Text=label}}).ToArray()};
            longPlay.ObserveFrame(f,now);longPlay.ObserveOcr(f,r,now);
            if(page>=35) {
                var p=longPlay.NextRegionProbe(now+200) ?? throw new Exception("A 32-page evidence limit permanently disabled observation");
                Check(p.Region.IsValid,"Late observation has invalid geometry");
                longPlay.ObserveRegion(p,Crop(Frame(page,false),p.Region),now+200);longPlay.ObserveRegion(p,Crop(f,p.Region),now+400);
            }
            longPlay.ObserveFrame(f,now+600);
        }
        Check(longPlay.Confirmed.Count==1,"Late blink evidence could not mature after a long static history");
        var pattern=learner.Confirmed.Single();var service=new MarkerLearningOcrService(new NoOcr(),MarkerLearningMode.Apply,new(MarkerLearningMode.Apply,[pattern]),
            new(Path.Combine(Path.GetTempPath(),"unused-marker-fast"),new string('D',64)),_=>{});
        service.ObserveAcceptedFrame(visible,Result(0,visible),true);
        var off=Frame(0,false);
        Check(service.EvaluateMarkerFrame(visible,off)==MarkerFrameDecision.Uncertain,"An unverified hidden pose skipped OCR");
        service.ObserveAcceptedFrame(off,Result(0,off),true);
        Check(service.EvaluateMarkerFrame(off,visible)==MarkerFrameDecision.Reused,"OCR-verified hidden and visible states did not reuse");
        Check(service.EvaluateMarkerFrame(off,off)==MarkerFrameDecision.Reused,"A learned blink-off state re-ran OCR");
        await service.StopAsync();
        // A blocked optional capture must cancel and drain at Stop, never call OCR.
        var pending=new GameMarkerLearner(MarkerLearningMode.Observe,[]);
        pending.ObserveOcr(visible,Result(0,visible),Environment.TickCount64);
        var wrapper=new MarkerLearningOcrService(new NoOcr(),MarkerLearningMode.Observe,pending,new(Path.GetTempPath(),new string('E',64)),_=>{});
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wrapper.StartRegionObservation(async (region,token)=>{Check(region.Width<400,"Observer captured the whole frame");entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return null;});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await wrapper.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(true,"Observer drained");
        Console.WriteLine($"PASS: {checks} same-colour/fast-blink/verified-invisible/body-protection/lifetime assertions. Synthetic pixels and Mock OCR only.");
    }
    private static void LearningBodyShapes()
    {
        ImageFrame ShiftBody(ImageFrame frame,int value)
        {
            var pixels=frame.Pixels.ToArray();
            for(int y=Body.Y;y<Body.Y+Body.Height;y++)for(int x=Body.X;x<Body.X+Body.Width;x++)
                for(int c=0;c<3;c++){int i=y*frame.Stride+x*4+c;pixels[i]=(byte)Math.Clamp(pixels[i]+value,0,255);}
            return new(frame.Width,frame.Height,frame.Stride,pixels);
        }
        var on=Frame(0);var signature=MarkerBodyShape.Create(on,[Body]) ?? throw new Exception("Body shape unavailable");
        Check(signature.Matches(ShiftBody(on,1)),"One-level rounding discarded the same body shape");
        Check(signature.Matches(ShiftBody(on,4)),"A small uniform colour change discarded unchanged glyph structure");
        Check(!signature.Matches(Frame(1)),"A different body passed the native-resolution edge check");
        var pixels=on.Pixels.ToArray();int at=32*on.Stride+25*4;pixels[at]=pixels[at+1]=pixels[at+2]=240;
        Check(!signature.Matches(new(on.Width,on.Height,on.Stride,pixels)),"A new one-pixel punctuation stroke was missed");
        Check(MarkerBodyShape.Create(Frame(0),[new(-1,0,20,20)]) is null,"Invalid body region created shape evidence");
        var learner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int page=0;page<5;page++) {
            var f=Frame(page);long now=page*2000;learner.ObserveFrame(f,now);learner.ObserveOcr(f,Result(page,f),now);
            learner.ObserveFrame(ShiftBody(Frame(page,false),1),now+250);
            learner.ObserveFrame(ShiftBody(f,1),now+500);
            Check(learner.Status.BodyChangeDiscards==0,"Harmless intensity jitter discarded blink learning");
            learner.ObserveOcr(f,Result(page,f),now+600);
            Check(learner.NextRegionProbe(now+800) is null,"An already verified page consumed another fast-observation slot");
        }
        Check(learner.Confirmed.Count==1 && learner.Status.ValidatedCycles==4,"Learning with stable shapes did not mature after independent pages");
        var changed=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        changed.ObserveOcr(on,Result(0,on),0);changed.ObserveFrame(Frame(1,false),250);changed.ObserveFrame(on,500);
        Check(changed.Status.BodyChangeDiscards==1 && changed.Status.ValidatedCycles==0,"Body changes were not rejected/counted");
        changed.ObserveOcr(on,Result(0,on),1000);changed.ObserveFrame(on,17000);
        Check(changed.Status.ExpiredWatches==1,"Expired observation was not counted separately");
        var appendLearner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        appendLearner.ObserveOcr(on,Result(0,on),0);
        var appended=Frame(0,false).Pixels.ToArray();
        for(int y=0;y<70;y++)on.Pixels.Span.Slice((30+y)*on.Stride,on.Stride).CopyTo(appended.AsSpan((100+y)*on.Stride,on.Stride));
        var next=new ImageFrame(on.Width,on.Height,on.Stride,appended);
        var nextRow=new OcrLine(Pages[1],.99f,Body with {Y=100});
        OcrLine[] nextLines=[new(Pages[0],.99f,Body),nextRow];
        appendLearner.ObserveFrame(next,300);
        appendLearner.ObserveOcr(next,new(nextLines,TimeSpan.Zero){Candidates=nextLines,TerminalMarkers=TerminalMarkerScanner.Scan(next,nextLines)},400);
        Check(appendLearner.NextRegionProbe(600)?.Region.Y>130 && appendLearner.Status.BodyChangeDiscards==1,
            "An appended NVL row left the old terminal watch occupying a sampling slot");
        var joinedPixels=Frame(0,false).Pixels.ToArray();
        for(int y=38;y<56;y++)for(int x=260;x<278;x++)if(x is >=266 and <272 || y is >=44 and <50) {
            int i=y*on.Stride+x*4;joinedPixels[i]=190;joinedPixels[i+1]=100;joinedPixels[i+2]=240;
        }
        var joinedFrame=new ImageFrame(on.Width,on.Height,on.Stride,joinedPixels);
        var joinedLine=new OcrLine(Pages[0]+"VO",.99f,Body with {Width=275});
        var joinedResult=new OcrResult([joinedLine],TimeSpan.Zero){Candidates=[joinedLine],TerminalMarkers=TerminalMarkerScanner.Scan(joinedFrame,[joinedLine])};
        var joinedLearner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        joinedLearner.ObserveOcr(joinedFrame,joinedResult,0);
        var firstProbe=joinedLearner.NextRegionProbe(200) ?? throw new Exception("Joined marker was not observed");
        joinedLearner.ObserveOcr(joinedFrame,joinedResult,300);
        Check(joinedLearner.NextRegionProbe(500)?.Id==firstProbe.Id && joinedLearner.Status.BodyChangeDiscards==0,
            "The original joined icon extent was mistaken for newly appended text");
    }
    private static void BackgroundAndOcrJitter()
    {
        ImageFrame Background(bool visible,byte level=240)
        {
            var f=Frame(0,visible);var pixels=f.Pixels.ToArray();
            // A coloured photo feature crosses the whole observation margin.
            // It is not part of the white indicator, and never changes.
            for(int y=65;y<114;y++)for(int x=15;x<57;x++) {
                int at=y*f.Stride+x*4;
                if(pixels[at]==240){pixels[at]=pixels[at+1]=pixels[at+2]=level;continue;}
                pixels[at]=80;pixels[at+1]=100;pixels[at+2]=220;
            }
            return new(f.Width,f.Height,f.Stride,pixels);
        }
        var on=Background(true);var result=Result(0,on);
        var sample=result.TerminalMarkers!.Single(s=>!s.Visual.Chromatic);
        var pattern=new GameMarkerPattern(sample.Location.Anchor,sample.Location.X,sample.Location.Y,sample.Location.Width,sample.Location.Height,[sample.Visual]);
        var guard=new LearnedMarkerFrameGuard();guard.Observe(on,result,[pattern],true);
        Check(guard.Failure==MarkerFrameFailure.None,"Coloured background touching a neutral icon disabled the learned gate");
        Check(guard.Evaluate(on,Background(true,220),[pattern])==MarkerFrameDecision.Reused,"Known white marker animation on a photo re-ran OCR");
        Check(guard.Evaluate(on,Background(true,239),[pattern])==MarkerFrameDecision.Reused && guard.NoisePixels==0,
            "Already shape-verified indicator pixels inflated the protected-area noise counter");
        var off=Background(false);
        Check(guard.Evaluate(on,off,[pattern])==MarkerFrameDecision.Uncertain,"An unseen blink-off phase was not checked by OCR");
        var jitter=Result(0,off);var jitterLine=jitter.Lines.Single() with {Text="今日はとてもよい天氣です。",Bounds=Body with {Width=Body.Width+1}};
        jitter=jitter with {Lines=[jitterLine],Candidates=[jitterLine]};
        guard.Observe(off,jitter,[pattern],true);
        Check(guard.Evaluate(off,on,[pattern])==MarkerFrameDecision.Reused,"Identical body pixels with OCR spelling/box jitter broke the verified blink pair");
        var edited=on.Pixels.ToArray();int index=48*on.Stride+75*4;edited[index]^=127;
        Check(guard.Evaluate(off,new(on.Width,on.Height,on.Stride,edited),[pattern])==MarkerFrameDecision.BodyChanged,"The blink pair hid a one-pixel body change");
        guard.Observe(on,result,[pattern],true);
        edited=Background(true,220).Pixels.ToArray();index=83*on.Stride+32*4;edited[index]=edited[index+1]=edited[index+2]=0;
        Check(guard.Evaluate(on,new(on.Width,on.Height,on.Stride,edited),[pattern])==MarkerFrameDecision.Uncertain,"A new internal stroke in the neutral icon was hidden");
        guard.Observe(on,result,[pattern],true);
        var extra=new OcrLine("A",.9f,sample.Bounds);
        guard.Observe(off,jitter with {Candidates=[jitterLine,extra]},[pattern],true);
        Check(guard.Evaluate(off,on,[pattern])==MarkerFrameDecision.Unavailable,"A discarded OCR candidate inside the icon area certified an empty phase");
        ImageFrame Noise(ImageFrame frame,int amount,int count=1)
        {
            var p=frame.Pixels.ToArray();
            for(int n=0;n<count;n++)p[10*frame.Stride+(100+n)*4]=(byte)amount;
            return new(frame.Width,frame.Height,frame.Stride,p);
        }
        guard.Observe(on,result,[pattern],true);
        Check(guard.Evaluate(on,Noise(Background(true,220),1,16),[pattern])==MarkerFrameDecision.Reused
            && guard.NoisePixels==16,"Bounded one-level rounding blocked a known marker animation");
        Check(guard.Evaluate(on,Noise(Background(true,220),2),[pattern])==MarkerFrameDecision.BodyChanged,"One-level tolerance accumulated into a larger change");
        guard.Observe(on,result,[pattern],true);
        Check(guard.Evaluate(on,Noise(Background(true,220),1,73),[pattern])==MarkerFrameDecision.BodyChanged,"Widespread rounding exceeded the pixel budget without OCR");
        guard.Observe(on,result,[pattern],true);
        var noisyOff=Noise(off,1,16);guard.Observe(noisyOff,Result(0,noisyOff),[pattern],true);
        Check(guard.Evaluate(noisyOff,on,[pattern])==MarkerFrameDecision.Reused,"OCR-verified empty phase rejected bounded capture rounding");
        guard.Observe(on,result,[pattern],false);
        Check(guard.Failure==MarkerFrameFailure.PartialCoverage,"Partial recognition was not diagnosed distinctly");
        var alphaOn=new ImageFrame(on.Width,on.Height,on.Stride,on.Pixels.ToArray(),FramePixelFormat.Bgra32);
        guard.Observe(alphaOn,Result(0,alphaOn),[pattern],true);
        var alpha=on.Pixels.ToArray();alpha[10*on.Stride+100*4+3]=1;
        Check(guard.Evaluate(alphaOn,new(on.Width,on.Height,on.Stride,alpha,FramePixelFormat.Bgra32),[pattern])==MarkerFrameDecision.BodyChanged,"Alpha changes were mistaken for RGB rounding");
    }
    private sealed class NoOcr:IOcrService
    {
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)=>throw new Exception("Observation invoked OCR");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
