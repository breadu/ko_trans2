using KoTrans.Core;
namespace KoTrans.Infrastructure;

public sealed partial class MarkerLearningOcrService
{
    private readonly CancellationTokenSource fastCancellation=new();
    private Task fastWorker=Task.CompletedTask;
    private bool fastStarted;
    // Only unconfirmed, already located indicators are sampled. No OCR, focus
    // change, new full-screen capture or additional retained screenshot.
    public void StartRegionObservation(Func<PixelRect,CancellationToken,Task<ImageFrame?>> captureRegion)
    {
        lock(sync) {
            if(stopped || fastStarted || mode==MarkerLearningMode.Off)return;
            fastStarted=true;fastWorker=Task.Run(async ()=> {
                int turn=0;int[] delays=[73,97,67,103];var token=fastCancellation.Token;
                try {while(!token.IsCancellationRequested) {
                    MarkerRegionProbe? probe;lock(sync)probe=learner.NextRegionProbe(Environment.TickCount64);
                    if(probe is not null) {
                        try {
                            var frame=await captureRegion(probe.Region,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                            if(frame is not null)lock(sync) {if(!stopped){learner.ObserveRegion(probe,frame,Environment.TickCount64);Update();}}
                        } catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                        catch(Exception ex) when(ex is not OutOfMemoryException) {
                            // An obscured/moving window must not create a busy retry loop.
                            await Task.Delay(600,token).ConfigureAwait(false);
                        }
                    }
                    await Task.Delay(probe is null?300:delays[turn++%delays.Length],token).ConfigureAwait(false);
                }}catch(OperationCanceledException) when(token.IsCancellationRequested){}
            });
        }
    }
}
