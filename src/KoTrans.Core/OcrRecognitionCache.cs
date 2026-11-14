using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KoTrans.Core;

// All recognizer paths share exact, prepared model inputs: ordinary lines,
// continuation-marker crops and learned-marker body verification. No shape or
// approximate-text comparison is permitted. Inputs themselves are never retained.
public sealed class OcrRecognitionCache
{
    public const int Capacity=128;
    private readonly ExactOcrCache<(string Text,float Confidence)> cache=new(Capacity);
    public OcrRecognitionCacheStats Stats {get;private set;}=new();
    public int Count=>cache.Count;
    public void BeginRequest()=>Stats=new();
    public (string Text,float Confidence) Recognize(ReadOnlySpan<float> input,int width,int height,string model,
        bool bypass,CancellationToken token,Func<(string Text,float Confidence)> infer)
    {
        token.ThrowIfCancellationRequested();
        if(width<=0 || height<=0 || (long)width*height*3!=input.Length)throw new ArgumentException("Invalid recognizer input shape.");
        string key=$"{width}:{height}:"+Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(input)));
        if(cache.TryGet(key,model,bypass,out var found,out var reason)) {
            token.ThrowIfCancellationRequested();Stats=Stats with {Hits=Stats.Hits+1};return found;
        }
        Stats=Stats with {Inferences=Stats.Inferences+1,LastMiss=reason,Misses=Stats.Misses.Count(reason)};
        var result=infer();token.ThrowIfCancellationRequested();
        // Empty recognition is a valid deterministic answer for an identical
        // input. Never retain an exception, cancelled call or malformed result.
        if(result.Text is {Length:<=4096} && float.IsFinite(result.Confidence))cache.Remember(key,model,result);
        return result;
    }
    public void Clear(){cache.Clear();BeginRequest();}
}
