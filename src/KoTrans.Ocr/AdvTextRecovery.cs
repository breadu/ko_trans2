using KoTrans.Core;
namespace KoTrans.Ocr;

// Per translation session. Normal tracking, one conditional retry, then the original result.
public sealed class AdvTextRecovery
{
    private PixelRect? recent;
    private int width, height, localRuns;
    private string previous = "", attempted = "";
    public static bool Supports(OcrOptions options) => options.Language == "en" && options.ReadingMode == "ADV" && !options.Vertical;
    private static PixelRect Bounds(IReadOnlyList<OcrLine> lines) {
        int x = lines.Min(l => l.Bounds.X), y = lines.Min(l => l.Bounds.Y);
        return new(x,y,lines.Max(l=>l.Bounds.X+l.Bounds.Width)-x,lines.Max(l=>l.Bounds.Y+l.Bounds.Height)-y);
    }
    public PixelRect? Select(ImageFrame frame, bool full) {
        if (width != frame.Width || height != frame.Height) { recent = null; previous = attempted = ""; localRuns = 0; width=frame.Width; height=frame.Height; }
        return full || localRuns >= 4 ? null : recent;
    }
    public static PixelRect? Around(ImageFrame frame, OcrResult result) {
        if (result.Lines.Count == 0) return null;
        var bounds=Bounds(result.Lines); int h=result.Lines.Select(l=>l.Bounds.Height).Order().ElementAt(result.Lines.Count/2);
        int x=Math.Max(0,bounds.X-4*h), y=Math.Max(0,bounds.Y-h);
        int right=Math.Min(frame.Width,bounds.X+bounds.Width+4*h), bottom=Math.Min(frame.Height,bounds.Y+bounds.Height+4*h);
        var region=new PixelRect(x,y,right-x,bottom-y);
        return region.IsValid && (long)region.Width*region.Height < (long)frame.Width*frame.Height*.85 ? region : null;
    }
    private static bool Overlaps(PixelRect a,PixelRect b) {
        long area=Math.Max(0,Math.Min(a.X+a.Width,b.X+b.Width)-Math.Max(a.X,b.X))*(long)Math.Max(0,Math.Min(a.Y+a.Height,b.Y+b.Height)-Math.Max(a.Y,b.Y));
        return area > Math.Min((long)a.Width*a.Height,(long)b.Width*b.Height)*.35;
    }
    public static OcrResult IncludeNearby(ImageFrame frame,OcrResult original,OcrResult candidates,PixelRect? scope=null) {
        if(original.Text.Length>32 || original.Lines.Count>2) return original;
        var region=scope??Around(frame,original); if(region is null) return original;
        var r=region.Value; int bottom=original.Lines.Count==0?r.Y:Bounds(original.Lines).Y+Bounds(original.Lines).Height;
        int scale=original.Lines.Count==0?32:original.Lines.Max(l=>l.Bounds.Height);
        var extra=(candidates.Candidates??candidates.Lines).Where(l =>
            l.Text.Length<=32 && (l.Confidence>=.8 || l.Confidence>=.6 && l.Text.All(c=>char.IsPunctuation(c)||char.IsWhiteSpace(c)))
            && l.Bounds.X+l.Bounds.Width/2>=r.X && l.Bounds.X+l.Bounds.Width/2<=r.X+r.Width
            && l.Bounds.Y+l.Bounds.Height/2>=Math.Max(r.Y,bottom-scale/2) && l.Bounds.Y+l.Bounds.Height<=r.Y+r.Height
            && l.Bounds.Height>=scale*.35 && l.Bounds.Height<=scale*1.8
            && !original.Lines.Any(old=>Overlaps(old.Bounds,l.Bounds))).ToArray();
        var replacements=original.Lines.Select(old => {
            if(old.Confidence>=.8) return old;
            return (candidates.Candidates??candidates.Lines).Where(l=>l.Text.Length<=32 && l.Confidence>=.85 && l.Confidence>=old.Confidence+.08 && Overlaps(old.Bounds,l.Bounds))
                .OrderByDescending(l=>l.Confidence).FirstOrDefault()??old;
        }).ToArray();
        if(extra.Length==0 && replacements.SequenceEqual(original.Lines)) return original;
        var lines=replacements.Concat(extra).ToArray();
        return candidates with { Lines=LayoutProcessor.Arrange(lines,new("en","NVL",false),frame.Width,frame.Height) };
    }
    public PixelRect? Retry(ImageFrame frame,OcrResult result,long inputEpoch) {
        if(result.Text.Length>32 || result.Lines.Count>2) return null;
        var area=Around(frame,result)??recent; if(area is null) return null;
        bool header=result.Lines.Count==2 && result.Lines.All(l=>l.Text.All(c=>char.IsLetter(c)||char.IsWhiteSpace(c)));
        bool weak=result.Lines.Count==0 || result.Lines.Any(l=>l.Confidence<.8) || header || previous.Length>result.Text.Length+40;
        if(!weak) return null;
        var r=area.Value; string key=$"{inputEpoch}:{result.Text}:{(r.X+r.Width/2)/128}:{(r.Y+r.Height/2)/128}";
        if(key==attempted) return null; attempted=key; return r;
    }
    public static bool Clipped(OcrResult result,PixelRect region) =>
        result.Lines.Count==0 || (result.Candidates??result.Lines).Select(l=>l.Bounds).Any(b=>
            b.X<=region.X+3 || b.Y<=region.Y+3 || b.X+b.Width>=region.X+region.Width-4 || b.Y+b.Height>=region.Y+region.Height-4);
    public void Observe(ImageFrame frame,OcrResult result,bool full) {
        width=frame.Width;height=frame.Height;localRuns=full?0:localRuns+1;
        var next=Around(frame,result); if(next is not null) recent=next;
        previous=result.Text;
    }
}
