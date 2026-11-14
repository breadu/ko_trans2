using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class TerminalMarkerTrackingTests
{
    private static int count;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
    private static readonly string[] Pages=["今日はとてもよい天気です。","静かな図書館で本を読みます。","遠くの山から不思議な音が聞こえます。","懐かしい友達と駅で待ち合わせます。","新しい世界への扉が開かれました。"];
    private static readonly PixelRect Body=new(20,30,220,32);
    private static ImageFrame Frame(PixelRect marker,bool visible=true)
    {
        byte[] pixels=new byte[500*240*4];
        // Synthetic neutral text, including a visible final punctuation column.
        for(int y=Body.Y+5;y<Body.Y+Body.Height-5;y++)for(int x=Body.X+3;x<Body.X+Body.Width-3;x++)if((x+y)%5<2)Set(x,y,230,230,230);
        if(visible)for(int y=2;y<marker.Height-2;y++)for(int x=2;x<marker.Width-2;x++)
            if(x>=marker.Width/3 && x<marker.Width*2/3 || y>=marker.Height/3 && y<marker.Height*2/3)Set(marker.X+x,marker.Y+y,245,150,180);
        void Set(int x,int y,byte r,byte g,byte b){int i=(y*500+x)*4;pixels[i]=b;pixels[i+1]=g;pixels[i+2]=r;}
        return new(500,240,2000,pixels);
    }
    private static OcrResult Scan(ImageFrame frame,int page,PixelRect marker,bool joined)
    {
        var bounds=joined ? Body with {Width=marker.X+marker.Width-Body.X} : Body;
        var line=new OcrLine(Pages[page],.98f,bounds);
        return new([line],TimeSpan.Zero){Candidates=[line],DetectedBoxes=joined?[bounds]:[Body,marker],TerminalMarkers=TerminalMarkerScanner.Scan(frame,[line])};
    }
    internal static void Run()
    {
        var right=new PixelRect(260,38,18,18);var frame=Frame(right);var joined=Scan(frame,0,right,true);
        Check(joined.TerminalMarkers!.Count==1,"Joined detector box prevented terminal observation");
        Check(joined.TerminalMarkers[0].Parent.Bounds.X+joined.TerminalMarkers[0].Parent.Bounds.Width<right.X,"Joined indicator remained inside protected body bounds");
        var learner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        PixelRect[] positions=[right,new(266,38,18,18),new(264,39,18,18),new(30,76,18,18),new(210,77,18,18)];
        for(int page=0;page<5;page++) {
            var marker=positions[page];var a=Frame(marker);var r=Scan(a,page,marker,page<3);
            Check(r.TerminalMarkers!.Count==1,$"Terminal/following-row observation missing on synthetic page {page}");
            learner.ObserveOcr(a,r,page*1000);learner.ObserveFrame(a,page*1000+200);
            learner.ObserveFrame(Frame(marker,false),page*1000+450);learner.ObserveFrame(a,page*1000+700);
        }
        Check(learner.Confirmed.Count==1,"Movement or wrapping split a single terminal marker family");
        Check(learner.Confirmed[0].Placements?.Any(p=>p.Anchor==MarkerAnchor.BlockBottom)==true,"Following-row placements were not retained");
        Check(GameMarkerGeometry.ValidPatterns(learner.Confirmed),"Tracked placements are not valid for persistence/worker transport");
        var below=new PixelRect(30,76,18,18);var belowSample=Scan(Frame(below),3,below,false).TerminalMarkers!.Single();
        Check(GameMarkerGeometry.AppearanceMatches(learner.Confirmed[0],belowSample),"Confirmed family did not match a following-row indicator");
        Check(!TerminalMarkerScanner.RequiresBodyConfirmation(belowSample,new(Pages[3],.98f,Body),false),
            "A clean following-row marker caused redundant recognition and a false exclusion count");
        Check(TerminalMarkerScanner.RequiresBodyConfirmation(belowSample,new(Pages[3],.98f,Body),true),
            "Separate marker noise bypassed body confirmation");
        Check(TerminalMarkerScanner.FreshBodyConfirms(belowSample,new(Pages[3],.98f,Body)),
            "A separate marker did not reuse the unchanged fresh complete body");
        Check(!TerminalMarkerScanner.FreshBodyConfirms(belowSample,new(Pages[3],.7f,Body)),"A weak body authorized a separate exclusion");
        Check(!TerminalMarkerScanner.FreshBodyConfirms(belowSample,new(Pages[3]+"A",.98f,Body)),"A real appended character was lost");
        Check(!TerminalMarkerScanner.FreshBodyConfirms(belowSample,new(Pages[4],.98f,Body)),"A different fresh body confirmed an old proposal");
        Check(!TerminalMarkerScanner.FreshBodyConfirms(belowSample,new(Pages[3],.98f,Body with {Width=Body.Width+1})),
            "A modified body crop bypassed exact verification");
        Check(!TerminalMarkerScanner.FreshBodyConfirms(belowSample with {Bounds=Body},new(Pages[3],.98f,Body)),
            "An overlapping marker bypassed body verification");
        Check(TerminalMarkerScanner.RequiresBodyConfirmation(joined.TerminalMarkers[0],joined.Lines[0],false),
            "A joined marker bypassed body confirmation");
        Check(!TerminalMarkerScanner.FreshBodyConfirms(joined.TerminalMarkers[0],joined.Lines[0]),
            "A joined suffix reused the untrimmed body without checking its crop");
        var distant=new PixelRect(320,160,18,18);
        Check(Scan(Frame(distant),0,distant,false).TerminalMarkers!.Count==0,"Unrelated artwork was scanned as a terminal pattern");
        var followingText=new OcrLine("これは次の文章です。",.99f,new(20,74,230,32));
        var samples=TerminalMarkerScanner.Scan(Frame(below),[new(Pages[0],.98f,Body),followingText]);
        Check(samples.All(s=>!GameMarkerGeometry.Intersects(s.Bounds,followingText.Bounds)),"Real following text was proposed for exclusion");
        var staticLearner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int i=0;i<5;i++)staticLearner.ObserveOcr(frame,Scan(frame,i,right,true),i*1000);
        Check(staticLearner.Confirmed.Count==0 && staticLearner.Status.BestPages==5,"Static screenshot tracking bypassed animation proof");
        Check(TerminalMarkerScanner.ConfirmsBody(Pages[0],new(Pages[0],.99f,Body)),"Exact completed body confirmation failed");
        Check(!TerminalMarkerScanner.ConfirmsBody(Pages[0],new(Pages[1],.99f,Body)),"A different body authorized trimming");
        Check(!TerminalMarkerScanner.ConfirmsBody(Pages[0],new(Pages[0]+"A",.99f,Body)),"A genuine ASCII suffix was deleted");
        Check(!TerminalMarkerScanner.ConfirmsBody(Pages[0],new(Pages[0],.7f,Body)),"Low confidence authorized trimming");
        Check(!TerminalMarkerScanner.TryCompletedPrefix("日本語の文章です。EXAMPLE",out _),"Long alphabetic words were treated as marker noise");
        ulong cross=0;
        for(int y=1;y<7;y++)for(int x=1;x<7;x++)if(x is >=2 and <=4 || y is >=2 and <=4)cross|=1UL<<(y*8+x);
        ulong jitter=cross;
        foreach(var (x,y) in new[]{(2,2),(3,2),(2,3),(3,3),(0,2),(7,2),(0,3),(7,3)})jitter^=1UL<<(y*8+x);
        var stableShape=new MarkerVisual(cross,220,110,190,true);
        Check(stableShape.Similar(new(jitter,220,110,190,true)),"Small resampling jitter split an indicator silhouette");
        Check(!stableShape.Similar(new(0x0000000F0F0F0F00,220,110,190,true)),"A different chromatic silhouette matched");
        var neutralPixels=frame.Pixels.ToArray();
        for(int y=right.Y;y<right.Y+right.Height;y++)for(int x=right.X;x<right.X+right.Width;x++) {
            int i=y*frame.Stride+x*4;
            if(neutralPixels[i+2]>0)neutralPixels[i]=neutralPixels[i+1]=neutralPixels[i+2]=240;
        }
        var neutral=new ImageFrame(frame.Width,frame.Height,frame.Stride,neutralPixels);
        var neutralLines=joined.Lines;
        Check(TerminalMarkerScanner.Scan(neutral,neutralLines).Count==0,"A frame-only scan carved neutral pixels out of a body box");
        var proposal=TerminalMarkerScanner.Scan(neutral,neutralLines,proposeJoinedNeutral:true).Single(s=>GameMarkerGeometry.Intersects(s.Bounds,right));
        Check(!proposal.Visual.Chromatic && proposal.Parent.Bounds.X+proposal.Parent.Bounds.Width<right.X,
            "A joined text-coloured indicator was not proposed for native body verification");
        Check(TerminalMarkerScanner.RequiresBodyConfirmation(proposal,neutralLines[0],false),"A joined neutral proposal bypassed fresh body verification");
        Check(!TerminalMarkerScanner.ConfirmsBody(proposal.Parent.Text,new(Pages[0][..^1],.99f,proposal.Parent.Bounds)),
            "A crop losing the final punctuation authorized a joined neutral proposal");
        var adjacent=new OcrLine("次の文字",.99f,right with {Height=Body.Height});
        Check(TerminalMarkerScanner.Scan(neutral,[neutralLines[0],adjacent],proposeJoinedNeutral:true)
            .All(s=>!GameMarkerGeometry.Intersects(s.Bounds,adjacent.Bounds)),"Joined proposals overlapped another real text row");
        var clean=new OcrLine(Pages[0],.99f,Body);
        var reference=TerminalMarkerReference.Create(frame,[clean])!;
        var corrupted=clean with {Text=Pages[0][..^2]+"トト",Bounds=Body with {Width=Body.Width+30}};
        Check(reference.CurrentParent(frame,[corrupted])==corrupted,"A terminal OCR error lost the clean body reference");
        Check(reference.CurrentParent(frame,[clean with {Text=Pages[1]}]) is null,"A changed sentence reused a prior body reference");
        Check(reference.CurrentParent(frame,[clean with {Text=Pages[0]+"追加の文章"}]) is null,"Appended dialogue reused a shorter body reference");
        var context=new OcrLine(Pages[1],.99f,Body with {Y=0,Height=24});
        var two=TerminalMarkerReference.Create(frame,[context,clean])!;
        Check(two.CurrentParent(frame,[context with {Text=Pages[2]},corrupted]) is null,"A changed preceding row reused a prior reference");
        Check(two.CurrentParent(frame,[corrupted]) is null,"A different row count reused a prior reference");
        Check(TerminalMarkerReference.Create(frame,[corrupted]) is null,"Corrupted punctuation replaced the clean reference");
        Check(!TerminalMarkerReference.Valid(reference with {Lines=[clean with {Bounds=new(-1,0,10,10)}]}),"Invalid reference geometry crossed the worker boundary");
        Check(!TerminalMarkerReference.Valid(reference with {Lines=[clean with {Text=new string('日',513)}]}),"Oversized reference text crossed the worker boundary");
        using var wire=new MemoryStream();
        DirectMlProtocol.WriteAsync(wire,new OcrOptions("ja","ADV",false,MarkerReference:reference),DirectMlProtocol.MaxHeader,CancellationToken.None).GetAwaiter().GetResult();
        wire.Position=0;
        var restored=DirectMlProtocol.ReadAsync<OcrOptions>(wire,DirectMlProtocol.MaxHeader,CancellationToken.None).GetAwaiter().GetResult();
        Check(restored.MarkerReference?.Lines[0]==clean,"DirectML lost the bounded session reference");
        Console.WriteLine($"PASS: {count} terminal-row/following-row/joined-box tracking and exact body protection assertions. Synthetic pixels only.");
    }
}
