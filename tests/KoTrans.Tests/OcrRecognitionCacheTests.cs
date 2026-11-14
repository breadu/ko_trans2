using KoTrans.Core;
using KoTrans.Ocr;
using OpenCvSharp;

internal static class OcrRecognitionCacheTests
{
    private static int checks;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    internal static void Run()
    {
        PreparedInputs();LimitsAndFailures();
        Console.WriteLine($"PASS: {checks} shared recognition cache input/model/body/cancel/eviction assertions; synthetic tensors and Mock inference only.");
    }
    private static void PreparedInputs()
    {
        using var image=new Mat(16,24,MatType.CV_8UC3,new Scalar(30,40,50));
        using var crop=new Mat(image,new Rect(3,4,10,4));
        var cache=new OcrRecognitionCache();var spec=new ModelSpec();int calls=0;
        float[] Input()=>PaddleOcrService.TensorFrom(crop,12,4,spec).ToArray();
        (string Text,float Confidence) Read(string model="model-a",bool bypass=false)=>cache.Recognize(Input(),12,4,model,bypass,default,()=>{calls++;return ("今日はよい天気です。",.99f);});
        var first=Read();Check(cache.Stats.LastMiss==OcrCacheMissReason.Empty && calls==1,"First crop did not execute inference");
        cache.BeginRequest();image.Set(0,0,new Vec3b(200,201,202));
        var second=Read();Check(second==first && calls==1 && cache.Stats.Hits==1 && cache.Stats.Inferences==0,"Changed background outside crop repeated recognition");
        // The ordinary line and a marker-body verification can share this exact
        // prepared input without changing the independent body validation.
        var body=Read();Check(body==first && cache.Stats.Hits==2,"Identical verification crop was not shared");
        Check(TerminalMarkerScanner.ConfirmsBody(first.Text,new(body.Text,body.Confidence,new(0,0,120,30))),"Cached body evidence changed confirmation");
        image.Set(4,3,new Vec3b(31,40,50));Read();
        Check(calls==2 && cache.Stats.LastMiss==OcrCacheMissReason.InputNotCached,"One changed model-input value reused old text");
        Read("model-b");Check(calls==3 && cache.Stats.LastMiss==OcrCacheMissReason.ConditionsChanged,"Model/dictionary change reused a result");
        Read("model-b",true);Check(calls==4 && cache.Stats.LastMiss==OcrCacheMissReason.Bypassed,"Manual request used recognition cache");
        Read("model-b");Check(calls==4,"Forced result did not refresh cache");
        var sameValues=Input();cache.Recognize(sameValues,6,8,"model-b",false,default,()=>{calls++;return first;});
        Check(calls==5,"Different tensor dimensions reused an answer");
        cache.Clear();Check(cache.Count==0 && cache.Stats.Inferences==0 && cache.Stats.Hits==0,"Model/session teardown retained cache");
    }
    private static void LimitsAndFailures()
    {
        var cache=new OcrRecognitionCache();int calls=0;float[] input=new float[12];
        (string Text,float Confidence) Infer(){calls++;return ("synthetic",.9f);}
        cache.Recognize(input,2,2,"a",false,default,Infer);
        using(var cts=new CancellationTokenSource()) {
            cts.Cancel();try {cache.Recognize(input,2,2,"a",false,cts.Token,Infer);throw new Exception("Cancelled hit succeeded");}
            catch(OperationCanceledException){checks++;}
        }
        try {cache.Recognize(input,2,2,"a",true,default,()=>throw new InvalidOperationException());}catch(InvalidOperationException){checks++;}
        Check(cache.Count==0,"Failed manual retry left stale result");
        using(var cts=new CancellationTokenSource()) {
            try {cache.Recognize(input,2,2,"a",false,cts.Token,()=>{cts.Cancel();return Infer();});throw new Exception("Late cancellation was stored");}
            catch(OperationCanceledException){checks++;}
        }
        Check(cache.Count==0,"Cancelled inference was cached");
        cache.Recognize(input,2,2,"a",false,default,()=> ("",0));
        var empty=cache.Recognize(input,2,2,"a",false,default,Infer);
        Check(empty.Text=="" && !TerminalMarkerScanner.ConfirmsBody("本文です。",new(empty.Text,empty.Confidence,new(0,0,80,20))),"Cached empty result authorized body removal");
        cache.Clear();
        for(int i=0;i<=OcrRecognitionCache.Capacity;i++) {input[0]=i;cache.Recognize(input,2,2,"a",false,default,Infer);}
        Check(cache.Count==OcrRecognitionCache.Capacity,"Recognition cache exceeded its bound");
        input[0]=0;cache.Recognize(input,2,2,"a",false,default,Infer);
        Check(cache.Stats.LastMiss==OcrCacheMissReason.Evicted && cache.Stats.Misses.Evicted==1,"Recent eviction was misreported as an unknown input");
        var lru=new ExactOcrCache<int>(2);lru.Remember("a","one",1);lru.Remember("b","one",2);
        lru.TryGet("a","one",false,out _,out _);lru.Remember("c","one",3);
        Check(lru.TryGet("a","one",false,out _,out _) && !lru.TryGet("b","one",false,out _,out var why) && why==OcrCacheMissReason.Evicted,"Recently used result was not retained");
        for(int i=0;i<20;i++)lru.Remember("new"+i,"one",i);
        lru.TryGet("b","one",false,out _,out why);
        Check(why==OcrCacheMissReason.InputNotCached,"Expired eviction history claimed a known cause");
        lru.Clear();lru.TryGet("b","one",false,out _,out why);Check(why==OcrCacheMissReason.Empty,"Clear retained old eviction evidence");
    }
}
