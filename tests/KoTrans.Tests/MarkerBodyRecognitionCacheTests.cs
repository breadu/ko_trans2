using KoTrans.Core;

internal static class MarkerBodyRecognitionCacheTests
{
    internal static void Run()
    {
        int count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        var pixels=new byte[80*40*4];
        for(int y=10;y<20;y++)for(int x=10;x<40;x++)if((x+y)%3==0) {
            int i=(y*80+x)*4;pixels[i]=220;pixels[i+1]=230;pixels[i+2]=240;
        }
        var frame=new ImageFrame(80,40,320,pixels);var crop=new PixelRect(10,10,30,10);
        var cache=new MarkerBodyRecognitionCache();var key=MarkerBodyRecognitionCache.Key(frame,crop,"model-A");
        const string body="ここは静かな図書館です。";
        Check(!cache.TryGet(key,out _),"Unverified crop was cached");cache.Remember(key,(body,.99f));
        var outside=(byte[])pixels.Clone();outside[35*320+60*4]=255;
        Check(cache.TryGet(MarkerBodyRecognitionCache.Key(new(80,40,320,outside),crop,"model-A"),out var hit)
            && hit.Text==body,"An indicator change outside the body repeated recognition");
        var changed=(byte[])pixels.Clone();changed[15*320+20*4]^=1;
        Check(!cache.TryGet(MarkerBodyRecognitionCache.Key(new(80,40,320,changed),crop,"model-A"),out _),"A one-level body pixel change reused recognition");
        Check(!cache.TryGet(MarkerBodyRecognitionCache.Key(frame,crop with {Width=29},"model-A"),out _),"Changed crop dimensions reused recognition");
        Check(!cache.TryGet(MarkerBodyRecognitionCache.Key(frame,crop,"model-B"),out _),"Changed model reused recognition");
        var alpha=(byte[])pixels.Clone();alpha[15*320+20*4+3]=127;
        Check(cache.TryGet(MarkerBodyRecognitionCache.Key(new(80,40,320,alpha,FramePixelFormat.Bgra32),crop,"model-A"),out _),"Alpha discarded by native BGR conversion broke cache identity");
        var padded=new byte[336*40];for(int y=0;y<40;y++)pixels.AsSpan(y*320,320).CopyTo(padded.AsSpan(y*336,320));
        Check(cache.TryGet(MarkerBodyRecognitionCache.Key(new(80,40,336,padded),crop,"model-A"),out _),"Stride padding changed the body identity");
        Check(MarkerBodyRecognitionCache.Key(frame,new(-1,0,10,10),"model-A") is null,"Invalid crop was cached");
        cache.Remember("failed",("不完全",.4f));
        Check(cache.TryGet("failed",out var failed) && !TerminalMarkerScanner.ConfirmsBody(body,new(failed.Text,failed.Confidence,crop)),"A cached rejected crop authorized trimming");
        for(int i=0;i<MarkerBodyRecognitionCache.Capacity;i++)cache.Remember("bounded-"+i,(body,.99f));
        Check(cache.Count==MarkerBodyRecognitionCache.Capacity && !cache.TryGet(key,out _),"Body cache is unbounded");
        cache.Clear();Check(cache.Count==0 && !cache.TryGet("bounded-31",out _),"Model disposal retained cached dialogue");
        Console.WriteLine($"PASS: {count} exact body crop cache/model/bounds/privacy assertions; synthetic pixels only.");
    }
}
