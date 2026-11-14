using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class WindowPrintCaptureTests
{
    internal static async Task Run()
    {
        int checks=0,calls=0;
        void Check(bool ok,string message) {if(!ok)throw new Exception(message);checks++;}
        var target=new WindowTarget(new IntPtr(7),7,7,"synthetic","synthetic");
        var first=new ImageFrame(10,10,40,new byte[400]);var second=new ImageFrame(20,10,80,new byte[800]);
        using var release=new ManualResetEventSlim();using var entered=new ManualResetEventSlim();
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture=new WindowPrintCapture((_,crop)=>{
            if(Interlocked.Increment(ref calls)==1) {entered.Set();release.Wait();finished.TrySetResult();return first;}
            return second;
        },TimeSpan.FromMilliseconds(30));
        async Task<bool> Busy() {try {await capture.CaptureAsync(target,null);return false;}catch(CaptureUnavailableException ex) {return ex.IsExpectedWait && ex.WaitReason==CaptureWaitReason.WindowBusy;}}
        try {
            var blocked=Busy();Check(entered.Wait(2000),"Synthetic renderer never entered");Check(await blocked,"Native render timeout was not an expected wait");
            for(int i=0;i<5;i++)Check(await Busy(),"Outstanding render spawned another capture");
            Check(calls==1,"Timeouts accumulated native rendering threads");
        } finally {release.Set();}
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ImageFrame? recovered=null;var deadline=DateTime.UtcNow.AddSeconds(2);
        while(recovered is null && DateTime.UtcNow<deadline) {
            try {recovered=await capture.CaptureAsync(target,null);}catch(CaptureUnavailableException) {await Task.Delay(5);}
        }
        Check(ReferenceEquals(recovered,second) && calls==2,"Late timed-out frame was published as a fresh capture");
        using var stop=new CancellationTokenSource();stop.Cancel();int before=calls;
        bool canceled=false;try {await capture.CaptureAsync(target,null,stop.Token);}catch(OperationCanceledException){canceled=true;}
        Check(canceled && calls==before,"Canceled capture started a render");
        using var pendingRelease=new ManualResetEventSlim();using var pendingEntered=new ManualResetEventSlim();
        var pendingFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingCapture=new WindowPrintCapture((_,_)=>{pendingEntered.Set();pendingRelease.Wait();pendingFinished.TrySetResult();return first;},TimeSpan.FromSeconds(2));
        using var lifetime=new CancellationTokenSource();var waiting=pendingCapture.CaptureAsync(target,null,lifetime.Token);
        try {
            Check(pendingEntered.Wait(2000),"Cancelable render never entered");lifetime.Cancel();
            canceled=false;try {await waiting.WaitAsync(TimeSpan.FromMilliseconds(250));}catch(OperationCanceledException){canceled=true;}
            Check(canceled,"Stop waited for a nonresponsive foreign renderer");
        } finally {pendingRelease.Set();}
        await pendingFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine($"PASS: {checks} bounded original-window capture timeout/cancel/recovery assertions. Mock renderer only.");
    }
}
