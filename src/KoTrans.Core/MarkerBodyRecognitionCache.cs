using System.Security.Cryptography;
using System.Buffers;

namespace KoTrans.Core;

// Native recognizer verification only. Exact crop bytes, dimensions and model
// identity must match; no fuzzy body comparison and no persisted dialogue/images.
public sealed class MarkerBodyRecognitionCache
{
    public const int Capacity=32;
    private readonly Dictionary<string,(string Text,float Confidence)> entries=[];
    private readonly Queue<string> order=[];
    public int Count=>entries.Count;
    public void Clear(){entries.Clear();order.Clear();}
    public static string? Key(ImageFrame frame,PixelRect crop,string model)
    {
        if(!GameMarkerGeometry.Inside(frame,crop) || (long)crop.Width*crop.Height>2_000_000)return null;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // The native BGR conversion discards alpha. Hash BGR bytes only, and do
        // not include stride padding or surrounding indicator/background pixels.
        var buffer=ArrayPool<byte>.Shared.Rent(crop.Width*3);
        try {
            for(int y=crop.Y;y<crop.Y+crop.Height;y++) {
                var row=frame.Pixels.Span.Slice(y*frame.Stride+crop.X*4,crop.Width*4);
                for(int x=0;x<crop.Width;x++){buffer[x*3]=row[x*4];buffer[x*3+1]=row[x*4+1];buffer[x*3+2]=row[x*4+2];}
                hash.AppendData(buffer.AsSpan(0,crop.Width*3));
            }
        } finally {ArrayPool<byte>.Shared.Return(buffer,clearArray:true);}
        return $"{model}:{crop.Width}:{crop.Height}:{Convert.ToHexString(hash.GetHashAndReset())}";
    }
    public bool TryGet(string? key,out (string Text,float Confidence) value)
    {value=default;return key is not null && entries.TryGetValue(key,out value);}
    public void Remember(string? key,(string Text,float Confidence) value)
    {
        if(key is null || value.Text.Length>512 || !float.IsFinite(value.Confidence))return;
        if(!entries.ContainsKey(key)) {
            if(entries.Count==Capacity)entries.Remove(order.Dequeue());
            order.Enqueue(key);
        }
        entries[key]=value;
    }
}
