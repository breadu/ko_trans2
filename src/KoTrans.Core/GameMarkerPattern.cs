using System.Numerics;

namespace KoTrans.Core;

public enum MarkerLearningMode { Off, Observe, Apply }
public enum MarkerAnchor { LineEnd, BlockBottom }
// Visual/relative geometry only: never screenshots, dialogue, titles or executable paths.
public sealed record MarkerVisual(ulong Mask, int Red, int Green, int Blue,bool Chromatic=false)
{
    public bool HasShape => BitOperations.PopCount(Mask) is >= 4 and <= 56;
    public bool Similar(MarkerVisual other)
    {
        if(Chromatic!=other.Chromatic || Math.Abs(Red-other.Red)+Math.Abs(Green-other.Green)+Math.Abs(Blue-other.Blue)>72)return false;
        int difference=BitOperations.PopCount(Mask^other.Mask);
        // Tight component boxes can gain a pixel on either edge. Preserve the
        // silhouette's overlap instead of demanding identical resampling bits.
        return difference<=3 || Chromatic && difference<=8
            && BitOperations.PopCount(Mask&other.Mask)>=BitOperations.PopCount(Mask|other.Mask)*.7;
    }
}
public sealed record GameMarkerPattern(MarkerAnchor Anchor, double X, double Y, double Width, double Height,
    IReadOnlyList<MarkerVisual> Shapes,IReadOnlyList<MarkerPlacement>? Placements=null);
public sealed record MarkerPlacement(MarkerAnchor Anchor,double X,double Y,double Width,double Height);
public sealed record MarkerLocation(MarkerAnchor Anchor, double X, double Y, double Width, double Height, PixelRect Parent);

public static class GameMarkerGeometry
{
    public static bool ValidPatterns(IReadOnlyList<GameMarkerPattern>? patterns)=>patterns is null || patterns.Count<=12
        && patterns.All(p=>p is not null && Enum.IsDefined(p.Anchor) && double.IsFinite(p.X) && double.IsFinite(p.Y)
            && double.IsFinite(p.Width) && double.IsFinite(p.Height) && p.X is >= -1 and <= 3 && p.Y is >= -1.1 and <= 1.6
            && p.Width is >= .15 and <= 1.65 && p.Height is >= .2 and <= 1.2 && p.Shapes is {Count:>=1 and <=8}
            && p.Shapes.All(s=>s is not null && s.HasShape && s.Red is >= 0 and <= 255 && s.Green is >= 0 and <= 255 && s.Blue is >= 0 and <= 255)
            && (p.Placements is null || p.Placements.Count<=8 && p.Placements.All(l=>l is not null && Enum.IsDefined(l.Anchor)
                && double.IsFinite(l.X) && double.IsFinite(l.Y) && double.IsFinite(l.Width) && double.IsFinite(l.Height)
                && l.X is >= -1 and <=3 && l.Y is >= -1.1 and <=1.6 && l.Width is >=.15 and <=1.65 && l.Height is >=.2 and <=1.2)));
    public static bool Supported(OcrOptions options) => options.EngineId == "paddle" && options.Language == "ja" && !options.Vertical;
    public static bool CompletedBody(OcrLine line) => !line.Vertical && line.Confidence >= .85f
        && line.Text.Count(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff') >= 4
        && line.Text.TrimEnd().LastOrDefault() is '。' or '！' or '？' or '」' or '』' or '）' or '…';

    // Only separate, small regions outside a freshly recognized complete body qualify.
    // Joined suffixes and ordinary letters at body height deliberately stay untouched.
    public static MarkerLocation? Locate(PixelRect candidate, IReadOnlyList<OcrLine> lines, IReadOnlyList<PixelRect> boxes)
    {
        if (!candidate.IsValid || lines.Count == 0) return null;
        var body = lines.Where(l => !l.Vertical && l.Confidence >= .85f && l.Text.Length >= 4 && l.Bounds.Width >= l.Bounds.Height*2).ToArray();
        if (body.Length == 0) return null;
        var last = body.OrderBy(l => l.Bounds.Y+l.Bounds.Height).ThenBy(l => l.Bounds.X+l.Bounds.Width).Last();
        if (!CompletedBody(last)) return null;
        double h = body.Select(l => l.Bounds.Height).Order().ElementAt(body.Length/2);
        if (candidate.Height < h*.2 || candidate.Height > h*.85 || candidate.Width < h*.15 || candidate.Width > h*1.6) return null;
        if (body.Any(l => Intersects(candidate,l.Bounds))) return null;
        // Do not remove a piece touching another detector box (e.g. a detached glyph).
        if (boxes.Any(b => b != candidate && Intersects(Expand(candidate,Math.Max(1,(int)(h*.12))),b))) return null;
        double x = (candidate.X-(last.Bounds.X+last.Bounds.Width))/h, y=(candidate.Y-(last.Bounds.Y+last.Bounds.Height))/h;
        if (x is >= .15 and <= 3 && y is >= -1.1 and <= .6)
            return new(MarkerAnchor.LineEnd,x,y,candidate.Width/h,candidate.Height/h,last.Bounds);
        int left=body.Min(l=>l.Bounds.X),right=body.Max(l=>l.Bounds.X+l.Bounds.Width);
        if (y is >= .2 and <= 1.6 && candidate.X >= left && candidate.X+candidate.Width <= right+2*h)
            return new(MarkerAnchor.BlockBottom,(candidate.X-left)/Math.Max(h,right-left),y,candidate.Width/h,candidate.Height/h,last.Bounds);
        return null;
    }
    public static bool GeometryMatches(GameMarkerPattern pattern, MarkerLocation location) => PlacementMatches(new(pattern.Anchor,pattern.X,pattern.Y,pattern.Width,pattern.Height),location)
        || pattern.Placements?.Any(p=>PlacementMatches(p,location))==true;
    private static bool PlacementMatches(MarkerPlacement p,MarkerLocation location)=>p.Anchor==location.Anchor
        && Math.Abs(p.X-location.X)<=(p.Anchor==MarkerAnchor.BlockBottom ? .08 : .35) && Math.Abs(p.Y-location.Y)<=.3
        && Math.Abs(p.Width-location.Width)<=.25 && Math.Abs(p.Height-location.Height)<=.2;
    public static bool AppearanceMatches(GameMarkerPattern p,TerminalMarkerSample sample)=>GeometryMatches(p,sample.Location)
        && p.Shapes.Any(s=>s.Similar(sample.Visual));
    public static bool CanExclude(ImageFrame frame, PixelRect candidate, IReadOnlyList<OcrLine> freshBody,
        IReadOnlyList<PixelRect> boxes, IReadOnlyList<GameMarkerPattern>? patterns)
    {
        if (patterns is null || patterns.Count==0 || Locate(candidate,freshBody,boxes) is not { } location) return false;
        var shape=Visual(frame,candidate);
        return shape.HasShape && patterns.Take(12).Any(p=>GeometryMatches(p,location) && p.Shapes.Take(8).Any(s=>s.Similar(shape)));
    }
    public static bool Intersects(PixelRect a,PixelRect b) => a.X<(long)b.X+b.Width && b.X<(long)a.X+a.Width && a.Y<(long)b.Y+b.Height && b.Y<(long)a.Y+a.Height;
    private static PixelRect Expand(PixelRect r,int n)=>new(r.X-n,r.Y-n,r.Width+2*n,r.Height+2*n);
    public static MarkerVisual Visual(ImageFrame frame,PixelRect box)
    {
        if (!Inside(frame,box)) return new(0,0,0,0);
        var pixels=frame.Pixels.Span; long br=0,bg=0,bb=0;int border=0;
        for(int x=box.X;x<box.X+box.Width;x++) { Border(x,box.Y);Border(x,box.Y+box.Height-1); }
        for(int y=box.Y+1;y<box.Y+box.Height-1;y++) { Border(box.X,y);Border(box.X+box.Width-1,y); }
        // Local functions cannot capture Span; reads below use the immutable frame buffer.
        void Border(int x,int y) { var p=frame.Pixels.Span;int i=y*frame.Stride+x*4;bb+=p[i];bg+=p[i+1];br+=p[i+2];border++; }
        int r=(int)(br/Math.Max(1,border)),g=(int)(bg/Math.Max(1,border)),b=(int)(bb/Math.Max(1,border));
        ulong mask=0;long sr=0,sg=0,sb=0;int count=0;
        for(int gy=0;gy<8;gy++) for(int gx=0;gx<8;gx++) {
            int x=box.X+Math.Min(box.Width-1,(2*gx+1)*box.Width/16), y=box.Y+Math.Min(box.Height-1,(2*gy+1)*box.Height/16),i=y*frame.Stride+x*4;
            if(Math.Abs(pixels[i]-b)+Math.Abs(pixels[i+1]-g)+Math.Abs(pixels[i+2]-r)<100)continue;
            mask|=1UL<<(gy*8+gx);sb+=pixels[i];sg+=pixels[i+1];sr+=pixels[i+2];count++;
        }
        return new(mask,(int)(sr/Math.Max(1,count)),(int)(sg/Math.Max(1,count)),(int)(sb/Math.Max(1,count)));
    }
    public static bool Inside(ImageFrame frame,PixelRect r)=>r.IsValid && r.X>=0 && r.Y>=0 && (long)r.X+r.Width<=frame.Width && (long)r.Y+r.Height<=frame.Height;
}
