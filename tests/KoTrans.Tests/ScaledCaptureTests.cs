using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class ScaledCaptureTests
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool ok,string message) {if(!ok) throw new Exception(message);checks++;}
        void Reject(Action action) {try {action();}catch(CaptureUnavailableException) {checks++;return;}throw new Exception("Invalid scale geometry was accepted");}
        var game=new IntPtr(1);var output=new IntPtr(2);
        foreach(var window in new[] {new PixelRect(100,200,800,600),new PixelRect(-1800,-200,800,600),new PixelRect(0,0,800,600)}) {
            var crop=new PixelRect(7,15,781,559);var original=WindowCaptureBounds.Resolve(window,crop);
            var plain=WindowCapturePlan.Create(game,window,crop,null);
            Check(plain.ScreenArea==original && plain.OutputWidth==781 && plain.OutputHeight==559 && plain.Scale is null,"Ordinary capture rectangle changed");
            foreach(var size in new[] {(1600,1200),(1200,900),(2400,1800)}) {
                var scale=new WindowScaleTransform(WindowScaleProvider.Magpie,game,output,window,new(-1920,50,size.Item1,size.Item2));
                var plan=WindowCapturePlan.Create(game,window,crop,scale);
                Check(plan.ScreenArea==scale.ToDisplay(original) && plan.OutputWidth==781 && plan.OutputHeight==559,"Pre-scaling saved region lost output size or mapped pixels");
                var restored=scale.ToSource(plan.ScreenArea);
                Check(restored.X<=original.X && restored.Y<=original.Y
                    && restored.X+restored.Width>=original.X+original.Width && restored.Y+restored.Height>=original.Y+original.Height
                    && restored.Width-original.Width<=2 && restored.Height-original.Height<=2,"Selection inverse mapping lost source pixels");
                Check(scale.ToSource(scale.Destination)==window && scale.ToDisplay(window)==scale.Destination,"Full-window mapping lost letterbox origin");
                var other=WindowCapturePlan.Create(new IntPtr(99),window,crop,scale);
                Check(other==plain,"Scaler attached to another game changed ordinary capture");
            }
        }
        var realScale=new WindowScaleTransform(WindowScaleProvider.Magpie,game,output,new(433,372,800,600),new(33,54,1600,1200));
        var realPlan=WindowCapturePlan.Create(game,new(433,373,800,600),new(7,15,781,559),realScale);
        Check(realPlan.ScreenArea==new PixelRect(47,86,1562,1118),"Observed Magpie offset/border case maps to wrong pixels");
        var exact=new PixelRect(440,388,781,559);
        var croppedPresenter=realScale with {Source=new(600,450,300,200)};
        var originalPlan=WindowCapturePlan.Create(game,new(433,373,800,600),new(7,15,781,559),croppedPresenter,originalPixels:true);
        Check(originalPlan.ScreenArea==exact && originalPlan.OutputWidth==781 && originalPlan.Scale==croppedPresenter,
            "Original-window mode still depended on cropped/scaled output geometry or lost transition metadata");
        Check(realScale.ToSource(realScale.ToDisplay(exact))==exact,"2x expanded selection did not preserve the saved original region");
        foreach (var client in new[] {new PixelRect(100,200,800,600),new PixelRect(-1800,-200,800,600)}) {
            var crop=new PixelRect(7,15,100,50);
            var captured=new ImageFrame(100,50,400,new byte[20000]);
            foreach (var scale in new WindowScaleTransform?[] {null,
                new(WindowScaleProvider.Magpie,game,output,client,new(-1920,0,1600,1200))}) {
                var plan=WindowCapturePlan.Create(game,client,crop,scale);
                plan.ValidateCompletion(captured,client,client,scale);checks++;
                Reject(()=>plan.ValidateCompletion(captured,client,client with {X=client.X+10},scale));
                Reject(()=>plan.ValidateCompletion(captured,client,client with {Width=900},scale));
                Reject(()=>plan.ValidateCompletion(new ImageFrame(50,50,200,new byte[10000]),client,client,scale));
                if(scale is not null) {
                    Reject(()=>plan.ValidateCompletion(captured,client,client,null));
                    Reject(()=>plan.ValidateCompletion(captured,client,client,scale with {Destination=new(0,0,1200,900)}));
                }
            }
        }
        Reject(()=>realScale.ToSource(new(0,0,10,10)));
        Reject(()=>realScale.ToDisplay(new(0,0,500,500)));
        Reject(()=>(realScale with {Source=default}).ToDisplay(exact));
        Reject(()=>(realScale with {Destination=default}).ToDisplay(exact));
        var pixels=new byte[12*8*4];
        for(int y=0;y<8;y++) for(int x=0;x<12;x++) for(int c=0;c<4;c++) pixels[(y*12+x)*4+c]=(byte)((x/2+y/2*6)*10);
        var image=new ImageFrame(12,8,48,pixels);
        Check(ReferenceEquals(ScreenCapture.Normalize(image,12,8),image),"Ordinary frame was copied/resampled");
        var normalized=ScreenCapture.Normalize(image,6,4);
        Check(normalized.Width==6 && normalized.Height==4 && normalized.Pixels.Span[0]==0
            && normalized.Pixels.Span[3*normalized.Stride+5*4]==230,"Upscaled frame did not normalize to original glyph pixels");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        bool cancelled=false;try {ScreenCapture.Normalize(image,6,4,cancellation.Token);}catch(OperationCanceledException) {cancelled=true;}
        Check(cancelled,"Stopped capture still normalized pixels");
        var telemetry=new DebugTelemetry();long session=telemetry.BeginSession(new("ja","NVL",false),new(),700);
        telemetry.CapturePaused(session,CaptureWaitReason.ScalingTransition);
        Check(telemetry.Snapshot().Values.CaptureStatus==DebugReason.Paused && !telemetry.Snapshot().Events.Any(e=>e.Code==DebugCode.CAPTURE_FAILED),"Scaling transition was reported as capture failure");
        Console.WriteLine($"PASS: {checks} scaling/no-scaler/other-game/negative-monitor/selection/normalization assertions. Synthetic pixels only.");
    }
}
