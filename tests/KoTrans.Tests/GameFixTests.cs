using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class GameFixTests
{
    public static async Task Run()
    {
        int n = 0; void Check(bool v, string m) { if (!v) throw new Exception(m); n++; }
        var opts = new OcrOptions("en", "ADV", false);
        OcrLine[] candidates = [new("A",1,new(200,60,100,30)), new("B",1,new(450,60,100,30))];
        var raw = new OcrResult(LayoutProcessor.Arrange(candidates,opts,610,150),TimeSpan.Zero) { Candidates = candidates };
        var full = new ImageFrame(1000,400,4000,new byte[1600000]);
        var restored = OcrRegionTracker.Restore(raw,new(0,140,610,150),full,opts);
        Check(raw.Text == "A" && restored.Text == "B", "ROI ADV must retain the rejected B candidate");
        Check(restored.Lines[0].Bounds.Y == 200 && raw.Candidates[1].Bounds.Y == 60,"Raw and source coordinates mixed");
        var tiny = new FrameChangeDetector(); tiny.Evaluate(full); tiny.Evaluate(full);
        var pixels = full.Pixels.ToArray(); for(int y=1;y<=4;y++) for(int x=1;x<=4;x++) for(int c=0;c<3;c++) pixels[y*4000+x*4+c]=255;
        var changed = new ImageFrame(1000,400,4000,pixels);
        Check(tiny.Evaluate(changed)==FrameDecision.Stabilizing && tiny.Evaluate(changed)==FrameDecision.Ready,"16 pixel fixed-grid blind spot remains");
        var window = new PixelRect(225,100,2100,1200); var selected = new PixelRect(382,1037,1786,210);
        var relative = WindowCaptureBounds.FromScreen(window,selected);
        Check(WindowCaptureBounds.Resolve(window,relative)==selected,"Window ignores dialogue selection");
        Check(WindowCaptureBounds.Resolve(window with { X=325 },relative).X==482,"Moving window lost relative region");
        try { WindowCaptureBounds.Resolve(new(0,0,600,300),relative);throw new Exception("Resize accepted"); }catch(InvalidDataException){n++;}
        var frame = new ImageFrame(400,300,1600,new byte[480000]);
        var gate = new RealtimeFrameGate(); gate.Evaluate(frame);gate.Evaluate(frame);
        gate.Observe(new([new("dialogue",1,new(100,200,180,20))],TimeSpan.Zero),frame);
        var timer=Stopwatch.StartNew();int probes=0;
        for(int i=0;i<50;i++) { var b=frame.Pixels.ToArray();for(int y=0;y<50;y++)for(int x=0;x<100;x++)for(int c=0;c<3;c++)b[y*1600+x*4+c]=(byte)(i*4); var d=gate.Evaluate(new(400,300,1600,b));Check(d is FrameDecision.Unchanged or FrameDecision.Probe,"Scenery invalidated stable dialogue");if(d==FrameDecision.Probe)probes++; }
        Check(probes>0 && probes<=10,"Outside dialogue reacquisition missing/unbounded");
        var ai=new SlowAi();var ocr=new StableOcr();int captures=0;var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ImageFrame Capture() { int i=Interlocked.Increment(ref captures);var b=frame.Pixels.ToArray();for(int y=0;y<50;y++)for(int x=0;x<100;x++)for(int c=0;c<3;c++)b[y*1600+x*4+c]=(byte)(i*9);return new(400,300,1600,b); }
        // Start with a stable full frame, then animate scenery only after text ROI is known.
        await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(ocr.Calls==0?frame:Capture()),opts,new(),new(),"synthetic",TimeSpan.FromMilliseconds(10),TimeSpan.Zero);
        pipe.Updated+=u=>{if(u.Translation=="translated")done.TrySetResult();};pipe.Start(true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(4));await Task.Delay(180);
        Check(ai.Calls==1,"Background animation made duplicate AI calls");Check(ai.Canceled==0,"Background canceled valid AI work");
        var cursorAi = new SlowAi(); var cursorOcr = new StableOcr(); int cursorTick = 0;
        var cursorDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ImageFrame Blink() {
            var b = frame.Pixels.ToArray(); byte v = (byte)((Interlocked.Increment(ref cursorTick) / 3 % 2) * 255);
            for (int y = 210; y < 214; y++) for (int x = 275; x < 279; x++) for (int c = 0; c < 3; c++) b[y * 1600 + x * 4 + c] = v;
            return new(400, 300, 1600, b);
        }
        await using (var blinking = new TranslationPipeline(cursorOcr, cursorAi, _ => Task.FromResult(cursorOcr.Calls == 0 ? frame : Blink()), opts, new(), new(), "synthetic", TimeSpan.FromMilliseconds(10), TimeSpan.Zero)) {
            blinking.Updated += u => { if (u.Translation == "translated") cursorDone.TrySetResult(); };
            blinking.Start(true); await cursorDone.Task.WaitAsync(TimeSpan.FromSeconds(4)); await Task.Delay(250);
            Check(cursorAi.Calls == 1 && cursorAi.Canceled == 0, "Blinking cursor canceled/duplicated paid work");
            Check(cursorOcr.Calls > 1, "Small edits were ignored instead of rechecked");
        }
        // A one-time outside edit during AI must survive two probe baseline evaluations.
        var pendingAi = new SlowAi(); var pendingOcr = new OutsideOcr();
        var outsideBytes = frame.Pixels.ToArray();
        for (int y = 0; y < 20; y++) for (int x = 0; x < 100; x++) for (int c = 0; c < 3; c++) outsideBytes[y * 1600 + x * 4 + c] = 255;
        var outsideFrame = new ImageFrame(400, 300, 1600, outsideBytes);
        await using (var pending = new TranslationPipeline(pendingOcr, pendingAi, _ => Task.FromResult(pendingOcr.Calls == 0 ? frame : outsideFrame), opts, new(), new(), "synthetic", TimeSpan.FromMilliseconds(10), TimeSpan.Zero)) {
            pending.Start(true); var until = Stopwatch.StartNew();
            while (pendingAi.Calls < 2) { if (until.ElapsedMilliseconds > 3000) throw new Exception("Deferred outside dialogue was forgotten during AI"); await Task.Delay(5); }
            Check(pendingAi.Calls == 2 && pendingOcr.Calls >= 2, "Confirmed new outside dialogue was not translated exactly once");
        }
        Console.WriteLine($"PASS: {n} game regression assertions; 50 comparisons {timer.ElapsedMilliseconds}ms including slow Mock pipeline; OCR={ocr.Calls}, AI={ai.Calls}, canceled={ai.Canceled}.");
    }
    private sealed class OutsideOcr : IOcrService
    {
        public int Calls; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t) {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new OcrResult([new(f.Pixels.Span[0] == 0 ? "old" : "new outside", 1, new(100,200,180,20))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class SlowAi:ITranslationService
    { public int Calls,Canceled;public async Task<TranslationResult> TranslateAsync(TranslationRequest r,CancellationToken t){Calls++;try{await Task.Delay(220,t);}catch(OperationCanceledException){Canceled++;throw;}return new("translated",TimeSpan.Zero,0);} }
    private sealed class StableOcr:IOcrService
    {public int Calls;public OcrState State=>OcrState.Ready;public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t){Calls++;return Task.FromResult(new OcrResult([new("dialogue",1,new(100,200,180,20))],TimeSpan.Zero));}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
}
