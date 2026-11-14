using KoTrans.Core;
using KoTrans.Ocr;

internal static class OcrAlgorithmTests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value,string message) { if(!value) throw new Exception(message); count++; }
        // blank, A, A, blank, A, emoji => AA😀; blank resets repeat-collapse.
        float[] probabilities = [1,0,0, 0,1,0, 0,1,0, 1,0,0, 0,1,0, 0,0,1];
        var decoded=TextProcessing.DecodeCtc(probabilities,6,3,["A","😀"]);
        Check(decoded.Text=="AA😀" && decoded.Confidence==1,"CTC blank/repeat/unicode contract");
        Check(TextProcessing.CharacterErrorRate("😀A","😀B")==.5,"CER must count Unicode scalars");
        Check(TextProcessing.Normalize(" 力とカ、口とロ ","ja")=="力とカ、口とロ","Japanese correction changed valid characters");
        Check(TextProcessing.Normalize("  A  | B° ","en")=="A | B°","English symbols must survive");
        var vertical=LayoutProcessor.Arrange([new("left",1,new(30,10,30,200),true),new("right",1,new(90,10,30,200),true)],new("ja","NVL",true),200,300);
        Check(vertical[0].Text=="right" && vertical[1].Text=="left","Vertical RTL order");
        var nvl=LayoutProcessor.Arrange([new("third",1,new(20,220,180,25)),new("first",1,new(20,20,180,25)),new("second",1,new(20,55,180,25))],new("en","NVL",false),400,400);
        Check(nvl.Select(l=>l.Text).SequenceEqual(new[]{"first","second","third"}),"NVL order");
        Check(nvl[0].Paragraph==nvl[1].Paragraph && nvl[2].Paragraph!=nvl[1].Paragraph,"Adaptive paragraph gap");
        Check(LayoutProcessor.IsRuby(new(20,5,35,8),new(20,17,100,30),false),"Furigana identification");
        var ruby=LayoutProcessor.Arrange([new("きょう",1,new(20,5,35,8)),new("今日",1,new(20,17,100,30))],new("ja","NVL",false),400,300);
        Check(ruby.Count==1 && ruby[0].Text=="今日","Furigana suppression");
        var fragments=LayoutProcessor.Arrange([new("Hello",1,new(20,20,80,25)),new("world!",1,new(110,20,80,25))],new("en","ADV",false),400,300);
        Check(fragments.Count==1 && fragments[0].Text=="Hello world!","Same-row word fragments must merge");
        Check(LayoutProcessor.ShouldJoinDetectionFragments(new(0,17,375,38),new(359,16,233,40)),"Overlapping same-line detection must recrop before recognition");
        Check(LayoutProcessor.ShouldJoinDetectionFragments(new(17,0,38,375),new(16,359,40,233)),"Vertical same-column fragments must recrop");
        Check(!LayoutProcessor.ShouldJoinDetectionFragments(new(0,17,375,38),new(359,70,233,40)),"Separate text rows must not join");
        Check(!LayoutProcessor.ShouldJoinDetectionFragments(new(0,17,375,38),new(400,16,233,40)),"Non-overlapping UI columns must not join");
        Check(!LayoutProcessor.ShouldJoinDetectionFragments(new(0,17,375,38),new(80,17,30,10)),"Tiny annotation must not join body");
        var adv=LayoutProcessor.Arrange([new("MENU",1,new(5,5,50,15)),new("Dialogue",1,new(100,180,220,30)),new("Next line",1,new(100,225,220,30))],new("en","ADV",false),400,300);
        Check(adv.Count==2 && adv[0].Text=="Dialogue","ADV selected corner UI instead of dialogue");
        var mixed=LayoutProcessor.Arrange([new("horizontal",1,new(10,10,200,20)),new("left",1,new(250,50,30,180),true),new("right",1,new(310,50,30,180),true)],new("ja","NVL",true),400,300);
        Check(mixed.Select(l=>l.Text).SequenceEqual(new[]{"right","left","horizontal"}),"Mixed orientation reading order");
        var a=new ImageFrame(100,100,400,new byte[40000]); var b=new byte[40000]; Array.Fill(b,(byte)255); var changed=new ImageFrame(100,100,400,b);
        var detector=new FrameChangeDetector();
        Check(detector.Evaluate(a)==FrameDecision.Stabilizing,"Initial stabilization");
        Check(detector.Evaluate(a)==FrameDecision.Ready,"Stable frame not ready");
        Check(detector.Evaluate(a)==FrameDecision.Unchanged,"Duplicate frame not suppressed");
        Check(detector.Evaluate(changed)==FrameDecision.Stabilizing,"Changed frame not delayed");
        Check(detector.Evaluate(changed)==FrameDecision.Ready,"Changed stable frame not ready");
        detector.Reset(); Check(detector.Evaluate(a)==FrameDecision.Stabilizing,"Profile reset");
        detector.Evaluate(a);
        var cursorPixels=new byte[40000]; Array.Fill(cursorPixels,(byte)255,0,4);
        Check(detector.Evaluate(new(100,100,400,cursorPixels))==FrameDecision.Unchanged,"Tiny blinking cursor should be suppressed");
        var roiDetector=new FrameChangeDetector(); var roi=new PixelRect(0,0,40,40);
        roiDetector.Evaluate(a,roi); roiDetector.Evaluate(a,roi);
        var animation=new byte[40000]; Array.Fill(animation,(byte)255,80*400,20*400);
        Check(roiDetector.Evaluate(new(100,100,400,animation),roi)==FrameDecision.Unchanged,"Background animation outside ROI caused OCR");
        var tracker=new TextRegionTracker();tracker.Observe(new([new("line",1,new(30,30,40,10))],TimeSpan.Zero),100,100);
        Check(tracker.GetRegion(100,100)==new PixelRect(10,10,80,50),"ROI glyph padding");
        Check(tracker.GetRegion(100,100,true) is null,"Full-frame reacquisition");
        Check(tracker.GetRegion(200,200) is null,"Resolution change must invalidate ROI");
        int calls=0; var staticDetector=new FrameChangeDetector();
        for(int i=0;i<100;i++) if(staticDetector.Evaluate(a)==FrameDecision.Ready)calls++;
        Check(calls==1,"100 unchanged frames should produce one ready decision");
        var filter=new TextDuplicateFilter(); Check(!filter.IsDuplicate("A") && filter.IsDuplicate("A") && !filter.IsDuplicate("AB"),"Typewriter extension suppressed");
        filter.Reset();Check(!filter.IsDuplicate("AB"),"Text profile reset");
        try {new ImageFrame(4,4,-16,new byte[64]);throw new Exception("Negative stride accepted");}catch(ArgumentException){count++;}
        // Native image conversion only: model-free, validates channel order, ignored alpha and padded rows.
        var padded=new ImageFrame(1,2,8,new byte[]{10,20,30,0,99,99,99,99,40,50,60,255,99,99,99,99});
        using(var bgr=PaddleOcrService.ToBgr(padded))
            Check(bgr.At<OpenCvSharp.Vec3b>(0,0)==new OpenCvSharp.Vec3b(10,20,30) && bgr.At<OpenCvSharp.Vec3b>(1,0)==new OpenCvSharp.Vec3b(40,50,60),"BGRX channels or padded stride corrupted");
        // Padding is generated after capture, in one detector pass. Original coordinates never grow.
        var pad=DetectionBorder.Default(1377,278);
        Check(pad==new OcrPadding(16,16,16,16),"Default four-sided virtual padding missing");
        Check(DetectionBorder.Default(100,40)==pad && DetectionBorder.Default(1752,208)==pad,"Padding unexpectedly depends on successful text detection");
        Check(DetectionBorder.Default(16384,3900) is null,"Virtual padding exceeds image safety bound");
        Check(DetectionBorder.Default(0,40) is null,"Invalid input accepted for padding");
        Check(DetectionBorder.Restore(new(96,40,300,48),pad!,1377,278)==new PixelRect(80,24,300,48),"Padding leaked into source coordinates");
        Check(DetectionBorder.Restore(new(0,0,30,30),pad!,1377,278)==new PixelRect(0,0,14,14),"Top/left clipping removed original pixels");
        Check(DetectionBorder.Restore(new(1370,270,30,40),pad!,1377,278)==new PixelRect(1354,254,23,24),"Right/bottom clipping left the captured frame");
        Check(DetectionBorder.Restore(new(0,0,10,10),pad!,1377,278) is null,"Padding-only detection entered recognition");
        Check(DetectionBorder.Restore(new(16,16,1377,278),pad!,1377,278)==new PixelRect(0,0,1377,278),"Full input coordinates did not roundtrip");
        using(var tailImage=new OpenCvSharp.Mat(100,500,OpenCvSharp.MatType.CV_8UC3,OpenCvSharp.Scalar.Black)) {
            var line=new PixelRect(20,20,300,40);
            Check(RecognitionTail.Extend(tailImage,line,[])==line,"Blank background extended recognition crop");
            OpenCvSharp.Cv2.Rectangle(tailImage,new(318,27,15,25),OpenCvSharp.Scalar.White,-1);
            OpenCvSharp.Cv2.Rectangle(tailImage,new(342,47,5,5),OpenCvSharp.Scalar.White,-1);
            var extended=RecognitionTail.Extend(tailImage,line,[]);
            Check(extended.Width>=327 && extended.Height==line.Height && extended.X==line.X,"Immediate light glyph/punctuation not included");
            Check(RecognitionTail.Extend(tailImage,line,[new(322,20,100,40)]).X+RecognitionTail.Extend(tailImage,line,[new(322,20,100,40)]).Width<=322,"Tail extension crossed neighboring detection");
            Check(RecognitionTail.Extend(tailImage,new(20,20,30,60),[])==new PixelRect(20,20,30,60),"Vertical detection got horizontal tail extension");
            tailImage.SetTo(OpenCvSharp.Scalar.Black);
            OpenCvSharp.Cv2.Rectangle(tailImage,new(345,27,15,25),OpenCvSharp.Scalar.White,-1);
            Check(RecognitionTail.Extend(tailImage,line,[])==line,"Detached background/UI became a continuation");
            tailImage.SetTo(OpenCvSharp.Scalar.Black);
            OpenCvSharp.Cv2.Rectangle(tailImage,new(318,40,42,3),OpenCvSharp.Scalar.White,-1);
            Check(RecognitionTail.Extend(tailImage,line,[])==line,"Thin decorative dash became a glyph");
            tailImage.SetTo(OpenCvSharp.Scalar.White);
            Check(RecognitionTail.Extend(tailImage,line,[])==line,"Broad bright background extended text crop");
        }
        Console.WriteLine($"PASS: {count} OCR algorithm assertions (no models)."); return count;
    }
}
