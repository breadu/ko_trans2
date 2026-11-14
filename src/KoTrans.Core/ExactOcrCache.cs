namespace KoTrans.Core;

public enum OcrCacheMissReason { None, Empty, InputNotCached, ConditionsChanged, Evicted, Bypassed }

// Numeric evidence only. Never include cache keys, model paths, pixels or text in reports.
public sealed record OcrCacheMissCounts(long Empty=0,long InputNotCached=0,long ConditionsChanged=0,long Evicted=0,long Bypassed=0)
{
    public OcrCacheMissCounts Count(OcrCacheMissReason reason)=>reason switch {
        OcrCacheMissReason.Empty=>this with {Empty=Empty+1},
        OcrCacheMissReason.InputNotCached=>this with {InputNotCached=InputNotCached+1},
        OcrCacheMissReason.ConditionsChanged=>this with {ConditionsChanged=ConditionsChanged+1},
        OcrCacheMissReason.Evicted=>this with {Evicted=Evicted+1},
        OcrCacheMissReason.Bypassed=>this with {Bypassed=Bypassed+1},_=>this };
    public OcrCacheMissCounts Add(OcrCacheMissCounts other)=>new(Empty+other.Empty,InputNotCached+other.InputNotCached,
        ConditionsChanged+other.ConditionsChanged,Evicted+other.Evicted,Bypassed+other.Bypassed);
}
public sealed record OcrRecognitionCacheStats(int Inferences=0,int Hits=0,OcrCacheMissReason LastMiss=OcrCacheMissReason.None)
{
    public OcrCacheMissCounts Misses {get;init;}=new();
}

// Accessed by one serialized OCR worker. Retain bounded digests of evicted keys
// so an exact recent eviction is distinguishable from an unseen input. A key
// older than this diagnostic history is only reported as InputNotCached.
public sealed class ExactOcrCache<T>
{
    private readonly int capacity;
    private readonly Dictionary<(string Input,string Conditions),T> entries=[];
    private readonly LinkedList<(string Input,string Conditions)> recent=[];
    private readonly LinkedList<(string Input,string Conditions)> evicted=[];
    public ExactOcrCache(int capacity)
    {if(capacity is <1 or >1024)throw new ArgumentOutOfRangeException(nameof(capacity));this.capacity=capacity;}
    public int Count=>entries.Count;
    public bool TryGet(string input,string conditions,bool bypass,out T value,out OcrCacheMissReason reason)
    {
        var key=(input,conditions);
        if(!bypass && entries.TryGetValue(key,out var found)) {
            recent.Remove(key);recent.AddLast(key);value=found;reason=OcrCacheMissReason.None;return true;
        }
        value=default!;
        reason=bypass ? OcrCacheMissReason.Bypassed : evicted.Contains(key) ? OcrCacheMissReason.Evicted
            : entries.Keys.Any(k=>k.Input==input) ? OcrCacheMissReason.ConditionsChanged
            : entries.Count==0 ? OcrCacheMissReason.Empty : OcrCacheMissReason.InputNotCached;
        if(bypass)Forget(input,conditions);
        return false;
    }
    public void Remember(string input,string conditions,T value)
    {
        var key=(input,conditions);entries[key]=value;recent.Remove(key);recent.AddLast(key);evicted.Remove(key);
        if(entries.Count>capacity) {
            var oldest=recent.First!.Value;recent.RemoveFirst();entries.Remove(oldest);
            evicted.Remove(oldest);evicted.AddLast(oldest);
            if(evicted.Count>capacity*4)evicted.RemoveFirst();
        }
    }
    public void Forget(string input,string conditions)
    {var key=(input,conditions);entries.Remove(key);recent.Remove(key);evicted.Remove(key);}
    public void Clear(){entries.Clear();recent.Clear();evicted.Clear();}
}
