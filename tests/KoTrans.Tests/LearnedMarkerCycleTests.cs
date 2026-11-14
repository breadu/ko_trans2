using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class LearnedMarkerCycleTests
{
    private static int checks;
    private static readonly PixelRect Body=new(20,30,220,32),Icon=new(24,76,18,18);
    private const string Text="今日はとてもよい天気です。";
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
    private static ImageFrame Frame(bool on,bool background=true,PixelRect? icon=null)
    {
        var p=new byte[400*180*4];
        var marker=icon??Icon;
        void Paint(int x,int y){int i=(y*400+x)*4;p[i]=p[i+1]=p[i+2]=240;}
        for(int y=35;y<57;y++)for(int x=23;x<237;x++)if((x+y)%5<2)Paint(x,y);
        // This fixed highlight joins the icon into a component too tall to scan.
        if(background)for(int y=marker.Y-11;y<marker.Y+39;y++)for(int x=marker.X;x<marker.X+3;x++)Paint(x,y);
        if(on)for(int y=0;y<18;y++)for(int x=0;x<18-Math.Abs(y-9);x++)Paint(marker.X+x,marker.Y+y);
        return new(400,180,1600,p);
    }
    private static ImageFrame Edit(ImageFrame f,int x,int y)
    {var p=f.Pixels.ToArray();int i=y*f.Stride+x*4;p[i]^=127;return new(f.Width,f.Height,f.Stride,p,f.PixelFormat);}
    private static OcrResult Result(bool on,string noise="6",float confidence=.6f,PixelRect? icon=null)
    {
        OcrLine body=new(Text,.99f,Body),extra=new(noise,confidence,icon??Icon);
        return new(on?[body,extra]:[body],TimeSpan.Zero){Candidates=on?[body,extra]:[body]};
    }
    internal static async Task Run()
    {
        var bare=Frame(true,false);var sample=TerminalMarkerScanner.Scan(bare,Result(false).Lines).Single();
        var pattern=new GameMarkerPattern(sample.Location.Anchor,sample.Location.X,sample.Location.Y,sample.Location.Width,sample.Location.Height,[sample.Visual]);
        var on=Frame(true);var off=Frame(false);
        Check(!TerminalMarkerScanner.Scan(on,Result(false).Lines).Any(s=>GameMarkerGeometry.AppearanceMatches(pattern,s)),"Fixture did not merge the icon with background");
        LearnedMarkerCycleRecovery Prepare()
        {
            var gate=new LearnedMarkerCycleRecovery();
            Check(gate.Observe(off,Result(false),[pattern],0).Text==Text,"Off phase changed body");
            Check(gate.Observe(on,Result(true),[pattern],25).Text!=Text,"One transition prematurely removed a glyph");
            gate.Bind(on,Result(true),true);Check(!gate.IsReady,"One transition enabled reuse");
            var firstReturn=gate.Observe(off,Result(false),[pattern],50);gate.Bind(off,firstReturn,true);
            Check(!gate.IsReady,"One cycle prematurely certified the reference");
            gate.Observe(on,Result(true),[pattern],75);
            var clean=gate.Observe(off,Result(false),[pattern],100);gate.Bind(off,clean,true);
            Check(gate.IsReady && gate.ConfirmedCycles==1,"Known reversible background-blended icon was not recovered");
            return gate;
        }
        var gate=Prepare();
        Check(gate.Evaluate(off,on)==MarkerFrameDecision.Reused,"Verified visible phase repeated OCR");
        Check(gate.Evaluate(off,on)==MarkerFrameDecision.Unchanged,"Repeated phase inflated skips");
        Check(gate.Observe(on,Result(true),[pattern],300).Text==Text,"Verified low-confidence icon was retained");
        Check(gate.Observe(on,Result(true),[pattern],400).LearnedMarkerExclusions==1,"Exclusion counter missing");
        Check(gate.Evaluate(off,Edit(on,30,82))==MarkerFrameDecision.Unavailable,"Unknown stroke inside the icon bypassed OCR");
        foreach(var point in new[]{(80,45),(350,150),(45,100)}) {
            gate=Prepare();Check(gate.Evaluate(off,Edit(on,point.Item1,point.Item2))==MarkerFrameDecision.Unavailable,"Changed text/background bypassed OCR");
        }
        foreach(var (text,confidence) in new[]{("字",.6f),("VO",.99f),("12345",.5f)}) {
            gate=Prepare();var r=gate.Observe(on,Result(true,text,confidence),[pattern],300);
            Check(r.LearnedMarkerExclusions==0 && r.Text.Contains(text),"Protected or uncertain text was removed");
        }
        // Confidence alone describes a character-shaped image. A known tight
        // terminal icon still needs two complete return cycles and fresh OCR.
        foreach(float confidence in new[]{.8f,.91f,.99f}) {
            gate=new();
            for(int i=0;i<5;i++) {
                bool visible=i%2==1;var frame=visible?on:off;
                var clean=gate.Observe(frame,Result(visible,"VO",confidence),[pattern],i*100);
                gate.Bind(frame,clean,true);
                if(i<4)Check(!gate.IsReady && clean.LearnedMarkerExclusions==0,"Confident glyph was accepted before two cycles");
            }
            Check(gate.IsReady && gate.ConfirmedCycles==1,"Repeated tight known glyph was not recovered");
            Check(gate.Evaluate(off,on)==MarkerFrameDecision.Reused,"Repeated confident marker did not bypass OCR");
            var cleanOn=gate.Observe(on,Result(true,"VO",confidence),[pattern],500);
            Check(cleanOn.Text==Text && cleanOn.LearnedMarkerExclusions==1,"Confirmed glyph removal changed or retained wrong text");
            Check(gate.Evaluate(off,Edit(on,80,45))==MarkerFrameDecision.Unavailable,"Confident marker proof hid a body pixel change");
        }
        gate=Prepare();
        gate.Observe(on,Result(true,"VO",.99f),[pattern],40000);
        gate.Bind(on,Result(true,"VO",.99f),true);
        Check(!gate.IsReady && gate.Failure==MarkerRecoveryFailure.WaitingForRepeatedCycle,"Confidence upgrade retained weaker binding");
        for(int i=0;i<4;i++) {
            bool visible=i%2==1;var frame=visible?on:off;
            var r=gate.Observe(frame,Result(visible,"VO",.99f),[pattern],40100+i*100);gate.Bind(frame,r,true);
            if(i<3)Check(!gate.IsReady,"Earlier weak observations counted toward stronger proof");
        }
        Check(gate.IsReady && gate.ConfirmedCycles==1,"Confidence upgrade failed or double-counted recovery");
        var oversized=Result(true,"VO",.99f) with {
            Lines=[Result(false).Lines[0],new("VO",.99f,new(Icon.X-6,Icon.Y,80,Icon.Height))],Candidates=null};
        foreach(var protectedResult in new[]{Result(true,"字",.99f),Result(true,"12345",.99f),oversized}) {
            gate=new();
            for(int i=0;i<8;i++) {
                bool visible=i%2==1;var frame=visible?on:off;
                var r=gate.Observe(frame,visible?protectedResult:Result(false),[pattern],i*100);gate.Bind(frame,r,true);
                Check(r.LearnedMarkerExclusions==0 && !gate.IsReady,"Repeated protected text was treated as a tight known icon");
            }
            Check(gate.Failure is MarkerRecoveryFailure.IndicatorText or MarkerRecoveryFailure.IndicatorBounds,
                "Protected text details were not distinguished in diagnostics");
        }
        gate=new();
        for(int i=0;i<5;i++) {
            bool visible=i%2==1;var frame=visible?on:off;
            var slow=Result(visible,"VO",.99f) with {InferenceTime=TimeSpan.FromSeconds(6)};
            var r=gate.Observe(frame,slow,[pattern],i*6000);gate.Bind(frame,r,true);
        }
        Check(gate.IsReady,"Slow OCR expired a valid repeated proof before it could finish");
        gate=new();gate.Observe(off,Result(false),[pattern],0);gate.Observe(on,Result(true,"VO",.99f),[pattern],100);
        gate.Observe(off,Result(false) with {InferenceTime=TimeSpan.FromSeconds(60)},[pattern],100000);
        gate.Bind(off,Result(false),true);Check(!gate.IsReady,"Long interruption retained pending repeated proof");
        gate=Prepare();gate.Bind(off,Result(false),false);Check(!gate.IsReady,"Partial coverage armed recovery");
        gate=Prepare();Check(gate.Evaluate(Frame(false),on)==MarkerFrameDecision.Unavailable,"Unrelated baseline reused old verification");
        gate=Prepare();var resized=new ImageFrame(200,360,800,on.Pixels.ToArray());
        Check(gate.Evaluate(off,resized)==MarkerFrameDecision.SizeChanged,"Dimension change kept old binding");
        gate=Prepare();gate.Reset();Check(!gate.IsReady && gate.Evaluate(off,on)==MarkerFrameDecision.Unavailable,"Reset retained reuse");
        var roundedPixels=on.Pixels.ToArray();for(int i=0;i<60;i++)roundedPixels[(150*400+200+i)*4]=1;
        var rounded=new ImageFrame(400,180,1600,roundedPixels);
        gate=Prepare();Check(gate.Evaluate(off,rounded)==MarkerFrameDecision.Unavailable,"Unverified rounding state bypassed OCR");
        gate.Observe(rounded,Result(true),[pattern],300);
        Check(gate.Evaluate(off,rounded)==MarkerFrameDecision.Unavailable,"A first-seen rounding state was immediately reused");
        var returned=gate.Observe(off,Result(false),[pattern],400);gate.Bind(off,returned,true);
        Check(gate.Evaluate(off,rounded)==MarkerFrameDecision.Reused,"OCR-verified reversible rounding state was not reused");
        gate=Prepare();var foreign=Result(true) with {Characters=[new("6",.6f,Icon,1,false)]};
        Check(gate.Observe(on,foreign,[pattern],300).LearnedMarkerExclusions==0,"Character indices were left inconsistent");
        gate=new();for(int i=0;i<6;i++)gate.Observe(on,Result(true),[pattern],i*100);
        gate.Bind(on,Result(true),true);Check(!gate.IsReady,"Static glyph learned a reversible cycle");
        gate=new();gate.Observe(off,Result(false),[pattern],0);gate.Observe(on,Result(true),[pattern],6000);
        gate.Bind(on,Result(true),true);Check(!gate.IsReady,"Expired observation armed reuse");
        foreach(var position in new[]{new PixelRect(264,38,18,18),new PixelRect(278,40,18,18),new PixelRect(34,84,18,18)}) {
            var hidden=Frame(false,icon:position);var visible=Frame(true,icon:position);gate=new();
            gate.Observe(hidden,Result(false),[pattern],0);
            gate.Observe(visible,Result(true,icon:position),[pattern],100);
            gate.Observe(hidden,Result(false),[pattern],125);gate.Observe(visible,Result(true,icon:position),[pattern],150);
            var clean=gate.Observe(hidden,Result(false),[pattern],200);gate.Bind(hidden,clean,true);
            Check(gate.Evaluate(hidden,visible)==MarkerFrameDecision.Reused,"Known shape moving between terminal anchors could not reuse");
            Check(gate.Observe(visible,Result(true,icon:position),[pattern],300).Text==Text,"Moved indicator was not excluded");
            Check(gate.Evaluate(hidden,Edit(visible,80,45))==MarkerFrameDecision.Unavailable,"Cross-anchor recovery hid changed body");
        }
        var remote=new PixelRect(340,118,18,18);gate=new();var remoteOff=Frame(false,icon:remote);var remoteOn=Frame(true,icon:remote);
        gate.Observe(remoteOff,Result(false),[pattern],0);gate.Observe(remoteOn,Result(true,icon:remote),[pattern],100);
        gate.Observe(remoteOff,Result(false),[pattern],200);gate.Bind(remoteOff,Result(false),true);
        Check(!gate.IsReady,"A matching sprite unrelated to terminal text was reused");
        var joinedPosition=new PixelRect(264,38,18,18);var joinedOff=Frame(false,icon:joinedPosition);var joinedOn=Frame(true,icon:joinedPosition);
        var separate=Result(true,icon:joinedPosition);var joinedLine=new OcrLine(Text+"6",.79f,new(20,30,262,32),Paragraph:0);
        var joined=separate with {Lines=[joinedLine]};gate=new();
        gate.Observe(joinedOff,Result(false),[pattern],0);
        Check(gate.Observe(joinedOn,joined,[pattern],100).Text==Text+"6","Unverified layout suffix was removed");
        gate.Observe(joinedOff,Result(false),[pattern],125);gate.Observe(joinedOn,joined,[pattern],150);
        var joinedClean=gate.Observe(joinedOff,Result(false),[pattern],200);gate.Bind(joinedOff,joinedClean,true);
        Check(gate.Evaluate(joinedOff,joinedOn)==MarkerFrameDecision.Reused,"Exact body/indicator layout join blocked recovery");
        var repaired=gate.Observe(joinedOn,joined,[pattern],300);
        Check(repaired.Text==Text && repaired.Lines.Single().Bounds==Body && repaired.LearnedMarkerExclusions==1,"Verified layout join was not restored to its raw body");
        var unknownJoin=joined with {Lines=[joinedLine with {Text=Text+"76"}]};
        Check(gate.Observe(joinedOn,unknownJoin,[pattern],400).Text==Text+"76","A suffix without exact raw components was trimmed");
        ReferencePolicy(pattern,on,off);
        WrappedClosingRows(pattern);
        await TrackedCoverage(pattern,on,off);
        foreach(bool useCache in new[]{false,true})
        foreach(var mode in new[]{MarkerLearningMode.Observe,MarkerLearningMode.Off,MarkerLearningMode.Apply}) {
            var mock=new Mock();await using var service=new MarkerLearningOcrService(mock,mode,new(mode,[pattern]),
                new(Path.Combine(Path.GetTempPath(),"unused-cycle-store"),new string('C',64)),_=>{});
            var options=new OcrOptions("ja","ADV",false);var cache=new OcrResultCache();
            Task<OcrResult> Read(ImageFrame frame)=>useCache ? service.RecognizeCachedAsync(frame,options,default,cache,false,()=>{}) : service.RecognizeAsync(frame,options,default);
            mock.Result=Result(false);await Read(off);
            mock.Result=Result(true);await Read(on);
            mock.Result=Result(false);await Read(off);
            mock.Result=Result(true);await Read(on);
            mock.Result=Result(false);var clean=await Read(off);service.ObserveAcceptedFrame(off,clean,true);
            Check((service.EvaluateMarkerFrame(off,on)==MarkerFrameDecision.Reused)==(mode==MarkerLearningMode.Apply),"Recovery ignored selected learning mode");
            var telemetry=new DebugTelemetry();long owner=telemetry.BeginSession(options,new(),0);telemetry.MarkerLearning(owner,service.LearningStatus);
            Check(telemetry.Snapshot().Values.LearnedMarkerRecoveredCycles==(mode==MarkerLearningMode.Apply?1:0),"Recovered cycle counter was lost in diagnostics");
            Check(telemetry.Snapshot().Values.LearnedMarkerRecoveryFailure==service.LearningStatus.RecoveryFailure
                && DebugReportWriter.Format(telemetry.Snapshot(),"test",new string('a',40)).Contains("LearnedMarkerRecoveryFailure:"),
                "Recovery rejection reason was lost from the saved report");
            if(mode==MarkerLearningMode.Apply) {
                service.EvaluateMarkerFrame(off,Edit(on,30,82));
                Check(service.LearningStatus.FrameFailure!=MarkerFrameFailure.None,"Unknown current frame was labelled verified");
            }
        }
        Console.WriteLine($"PASS: {checks} blended-marker cycle/known-pose/body/reset/mode assertions; synthetic only.");
    }
    private static async Task TrackedCoverage(GameMarkerPattern pattern,ImageFrame on,ImageFrame off)
    {
        var area=new PixelRect(10,20,260,110);var gate=new LearnedMarkerCycleRecovery();
        for(int i=0;i<5;i++) {
            bool visible=i%2==1;var frame=visible?on:off;
            var r=gate.Observe(frame,Result(visible),[pattern],i*100,fullSearch:i==0,searchArea:i==0?null:area);
            gate.Bind(frame,r,true);
            if(i<4)Check(!gate.IsReady,"Tracked ROI bypassed two complete return cycles");
        }
        Check(gate.Evaluate(off,on)==MarkerFrameDecision.Reused,"Fully covered tracked ROIs repeatedly reset the cycle");
        var clean=gate.Observe(on,Result(true),[pattern],500,false,area);gate.Bind(on,clean,true);
        Check(clean.Text==Text && clean.LearnedMarkerExclusions==1 && gate.Evaluate(on,off)==MarkerFrameDecision.Reused,
            "Covered ROI did not preserve the reference text and reuse after confirmation");
        var hidden=new OcrLine("別の文章",.99f,new(300,140,70,20));
        foreach(var prior in new[]{Result(false) with {Lines=[Result(false).Lines[0],hidden]},
            Result(false) with {Candidates=[Result(false).Lines[0],hidden]}}) {
            gate=new();gate.Observe(off,prior,[pattern],0);
            var r=gate.Observe(on,Result(true),[pattern],100,false,area);gate.Bind(on,r,true);
            Check(!gate.IsReady && gate.Failure==MarkerRecoveryFailure.PartialCoverage && r.Text==Result(true).Text,
                "An unread visible/raw text row was dropped when promoting an ROI");
        }
        foreach(int level in new[]{1,127}) {
            gate=new();gate.Observe(off,Result(false),[pattern],0);
            var pixels=on.Pixels.ToArray();pixels[150*on.Stride+350*4]=(byte)level;
            var changed=new ImageFrame(on.Width,on.Height,on.Stride,pixels);
            var r=gate.Observe(changed,Result(true),[pattern],100,false,area);gate.Bind(changed,r,true);
            Check(!gate.IsReady && gate.Failure==MarkerRecoveryFailure.PartialCoverage,
                "Unread outside pixel changes, even one-level rounding, were ignored by ROI coverage");
        }
        foreach(var invalid in new[]{new PixelRect(10,20,230,40),new PixelRect(-1,20,270,110),default(PixelRect)}) {
            gate=new();gate.Observe(off,Result(false),[pattern],0);
            var r=gate.Observe(on,Result(true),[pattern],100,false,invalid);gate.Bind(on,r,true);
            Check(!gate.IsReady && gate.Failure==MarkerRecoveryFailure.PartialCoverage,
                "Clipped or invalid ROI bounds certified unread output");
        }
        gate=new();
        for(int i=0;i<8;i++) {
            bool visible=i%2==1;var frame=visible?on:off;
            var r=gate.Observe(frame,Result(visible),[pattern],i*100,false,area);gate.Bind(frame,r,true);
            Check(!gate.IsReady,"A sequence of ROIs manufactured an initial full-frame reference");
        }
        var mock=new Mock();await using var service=new MarkerLearningOcrService(mock,MarkerLearningMode.Apply,
            new(MarkerLearningMode.Apply,[pattern]),new(Path.Combine(Path.GetTempPath(),"unused-roi-cycle-store"),new string('D',64)),_=>{});
        for(int i=0;i<5;i++) {
            bool visible=i%2==1;var frame=visible?on:off;mock.Result=Result(visible);
            var options=new OcrOptions("ja","ADV",false,SearchArea:i==0?null:area);
            var r=await service.RecognizeAsync(frame,options,default);service.ObserveAcceptedFrame(frame,r,true);
        }
        Check(service.EvaluateMarkerFrame(off,on)==MarkerFrameDecision.Reused && service.LearningStatus.RecoveredCycles==1,
            "The learning wrapper lost the tracked search area before recovery");
    }
    private static void WrappedClosingRows(GameMarkerPattern pattern)
    {
        var tailBox=new PixelRect(20,78,48,32);
        var preceding=new OcrLine(Text[..^2],.99f,Body); // Sentence completes on the next row.
        ImageFrame Page(bool visible,PixelRect marker,bool background=true) {
            var frame=Frame(visible,background,marker);var p=frame.Pixels.ToArray();
            for(int y=tailBox.Y+5;y<tailBox.Y+27;y++)for(int x=tailBox.X+3;x<tailBox.X+45;x++)if((x+y)%5<2) {
                int i=y*frame.Stride+x*4;p[i]=p[i+1]=p[i+2]=240;
            }
            return new(frame.Width,frame.Height,frame.Stride,p);
        }
        OcrResult Reading(OcrLine tail,PixelRect marker,bool visible,bool context=true,string noise="VO") {
            OcrLine[] body=context?[preceding,tail]:[tail];
            OcrLine[] lines=visible?[..body,new(noise,.94f,marker)]:body;
            return new(lines,TimeSpan.Zero){Candidates=lines};
        }
        foreach(var marker in new[]{new PixelRect(24,124,18,18),new PixelRect(80,84,18,18)})
        foreach(string ending in new[]{"す。","か？","ね！","よ」","よ』","よ）","よ…"}) {
            var tail=new OcrLine(ending,.99f,tailBox);var off=Page(false,marker);var on=Page(true,marker);
            var clean=Reading(tail,marker,false);
            Check(TerminalMarkerReference.Create(off,clean.Lines)?.Lines.Last()==tail,
                "Clean reference lost a wrapped one-glyph closing row");
            Check(TerminalMarkerScanner.Scan(Page(true,marker,false),clean.Lines)
                .Any(s=>s.Parent.Text==ending),"Discovery disagreed with the wrapped closing row");
            var gate=new LearnedMarkerCycleRecovery();
            for(int i=0;i<5;i++) {
                bool visible=i%2==1;var f=visible?on:off;
                var r=gate.Observe(f,Reading(tail,marker,visible),[pattern],i*100);gate.Bind(f,r,true);
                if(i<4)Check(!gate.IsReady,"Short closing row bypassed repeated-cycle verification");
            }
            Check(gate.Evaluate(off,on)==MarkerFrameDecision.Reused,"Wrapped closing row blocked verified frame reuse");
            var repaired=gate.Observe(on,Reading(tail,marker,true),[pattern],500);
            Check(repaired.Text==clean.Text && repaired.Lines.SequenceEqual(clean.Lines),
                "Wrapped closing row or its punctuation was lost during marker removal");
            var protectedGlyph=gate.Observe(on,Reading(tail,marker,true,noise:"字"),[pattern],600);
            Check(protectedGlyph.LearnedMarkerExclusions==0 && protectedGlyph.Text.EndsWith("字",StringComparison.Ordinal),
                "Short closing row allowed a Japanese character to be removed");
        }
        var below=new PixelRect(24,124,18,18);
        foreach(var (ending,confidence,context) in new[]{("す",.99f,true),("。",.99f,true),("す。",.84f,true),("す。",.99f,false)}) {
            var tail=new OcrLine(ending,confidence,tailBox);var off=Page(false,below);var on=Page(true,below);
            var gate=new LearnedMarkerCycleRecovery();
            for(int i=0;i<6;i++) {
                bool visible=i%2==1;var f=visible?on:off;var input=Reading(tail,below,visible,context);
                var r=gate.Observe(f,input,[pattern],i*100);gate.Bind(f,r,true);
                Check(!gate.IsReady && r.Text==input.Text,"Incomplete, weak or context-free short row enabled reuse");
            }
        }
    }
    private static void ReferencePolicy(GameMarkerPattern pattern,ImageFrame on,ImageFrame off)
    {
        var body=new OcrLine(Text,.99f,Body);var other=new OcrLine("ABC123",.7f,new(120,76,80,18),Paragraph:1);
        var reference=new OcrResult([body,other],TimeSpan.Zero){Candidates=[body,other]};
        var padded=new OcrLine("VO",.95f,new(16,65,34,39),Paragraph:1);
        var jitter=other with {Text="ABC128"};
        var joined=new OcrLine("VOABC128",.8f,new(16,65,184,39),Paragraph:1);
        var observed=new OcrResult([body,joined],TimeSpan.Zero){Candidates=[body,padded,jitter]};
        var gate=new LearnedMarkerCycleRecovery();
        for(int i=0;i<5;i++) {
            bool visible=i%2==1;var frame=visible?on:off;
            var result=gate.Observe(frame,visible?observed:reference,[pattern],i*100);gate.Bind(frame,result,true);
            if(i<4)Check(!gate.IsReady,"Reference became usable without two complete return cycles");
        }
        var reused=gate.Observe(on,observed,[pattern],500);gate.Bind(on,reused,true);
        Check(reused.Text==reference.Text && reused.Lines.SequenceEqual(reference.Lines)
            && reused.Candidates!.SequenceEqual(reference.Candidates!) && reused.LearnedMarkerExclusions==1,
            "Reference did not preserve all other text/paragraphs across padded joined-box OCR jitter");
        Check(gate.Evaluate(on,off)==MarkerFrameDecision.Reused,"Verified reference did not avoid another OCR");
        Check(gate.Evaluate(on,Edit(off,140,80))==MarkerFrameDecision.Unavailable,"Real changes in the other text were hidden by the reference");
        gate=new();
        for(int i=0;i<5;i++)gate.Observe(i%2==0?off:on,i%2==0?reference:observed,[pattern],i*100);
        var paddingChange=Edit(on,45,82);
        var fresh=gate.Observe(paddingChange,observed,[pattern],500);gate.Bind(paddingChange,fresh,true);
        Check(!gate.IsReady && fresh.Text==observed.Text && fresh.LearnedMarkerExclusions==0,
            "New pixels outside the actual icon were hidden by its observation padding");
        gate=new();for(int i=0;i<6;i++)gate.Observe(i%2==0?off:on,i%2==0?reference:observed,[pattern],i*100,fullSearch:false);
        gate.Bind(off,reference,true);Check(!gate.IsReady,"A partial OCR supplied the full reference text");
        gate=new();
        for(int i=0;i<5;i++)gate.Observe(i%2==0?off:on,i%2==0?reference:observed,[pattern],i*100);
        ImageFrame Drift(byte value){var p=on.Pixels.ToArray();p[150*on.Stride+350*4]=value;return new(on.Width,on.Height,on.Stride,p);}
        gate.Observe(Drift(1),observed,[pattern],500);
        var drift=Drift(2);var changed=gate.Observe(drift,observed,[pattern],600);gate.Bind(drift,changed,true);
        Check(!gate.IsReady && changed.Text==observed.Text,"Small differences accumulated away from the fixed reference pixels");
        gate=new();
        for(int i=0;i<5;i++) {
            bool visible=i%2==0;var frame=visible?on:off;
            var r=gate.Observe(frame,visible?observed:reference,[pattern],i*100);gate.Bind(frame,r,true);
        }
        Check(gate.IsReady && gate.Observe(on,observed,[pattern],600).Text==reference.Text,
            "Starting from the visible phase selected the marker as reference text");
        var retained=ReferenceWithoutExternalOwner(pattern);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        var roundedPixels=retained.On.Pixels.ToArray();roundedPixels[150*retained.On.Stride+350*4]=1;
        var rounded=new ImageFrame(400,180,1600,roundedPixels);
        retained.Gate.Observe(rounded,Result(true),[pattern],500);
        var freshOff=Frame(false);var returned=retained.Gate.Observe(freshOff,Result(false),[pattern],600);
        retained.Gate.Bind(freshOff,returned,true);
        Check(retained.Gate.Evaluate(freshOff,rounded)==MarkerFrameDecision.Reused,
            "Collecting prior pipeline frames discarded the fixed reference needed to verify new rounding");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (LearnedMarkerCycleRecovery Gate,ImageFrame On) ReferenceWithoutExternalOwner(GameMarkerPattern pattern)
    {
        var gate=new LearnedMarkerCycleRecovery();var off=Frame(false);var on=Frame(true);
        for(int i=0;i<5;i++) {
            bool visible=i%2==1;var frame=visible?on:off;
            var r=gate.Observe(frame,Result(visible),[pattern],i*100);gate.Bind(frame,r,true);
        }
        return (gate,on);
    }
    private sealed class Mock:IOcrService
    {
        public OcrResult Result=LearnedMarkerCycleTests.Result(false);
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t)=>Task.FromResult(Result);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
