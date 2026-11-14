namespace KoTrans.Core;

// Observation is limited to the final text row and the following row. A detector
// may enclose both text and a small indicator, so its boxes are not the boundary
// of what can be observed. This proposes metadata, never removes source pixels.
public sealed record TerminalMarkerSample(PixelRect Bounds,OcrLine Parent,MarkerLocation Location,MarkerVisual Visual);
public static class TerminalMarkerScanner
{
    public static IReadOnlyList<TerminalMarkerSample> Scan(ImageFrame frame,IReadOnlyList<OcrLine> lines,bool proposeJoinedNeutral=false)
    {
        var rows=lines.Where(l=>IsBodyRow(l)).OrderBy(l=>l.Bounds.Y).ToArray();
        if(rows.Length==0 || rows.Sum(l=>l.Text.Count(Japanese))<4)return [];
        double h=rows.Select(l=>l.Bounds.Height).Order().ElementAt(rows.Length/2);
        var last=rows.LastOrDefault(l=>l.Bounds.Height>=h*.7);if(last is null || !TryCompletedPrefix(last.Text,out var prefix))return [];
        // A short wrapped final row can hold the closing punctuation. The total
        // context, rather than four Japanese glyphs on this row, supplies evidence.
        int left=rows.Min(l=>l.Bounds.X),right=rows.Max(l=>l.Bounds.X+l.Bounds.Width);
        var areas=new[]{Clip(frame,new(last.Bounds.X+Math.Max(0,last.Bounds.Width-(int)(h*2)),last.Bounds.Y,
            (int)(h*5),last.Bounds.Height)),Clip(frame,new(left,last.Bounds.Y+last.Bounds.Height,(int)(right-left+h*3),(int)(h*1.7)))};
        var output=new List<TerminalMarkerSample>();
        foreach(bool chromatic in new[]{true,false})foreach(var area in areas)foreach(var core in Components(frame,area,chromatic)) {
            if(core.Width<h*.15 || core.Height<h*.12 || core.Width>h*1.4 || core.Height>h*(chromatic?.75:.95))continue;
            int pad=Math.Max(2,(int)(h*.08));var region=Clip(frame,new(core.X-pad,core.Y-pad,core.Width+2*pad,core.Height+2*pad));
            if(region.Width>h*1.65 || region.Height>h*1.2 || !region.IsValid)continue;
            // Only the native OCR caller may request a joined neutral proposal.
            // It must recognize the proposed body crop and verify the complete
            // text before giving that proposal to learning or removing anything.
            // Frame-only callers cannot use this exception to hide body pixels.
            if(!chromatic && lines.Any(l=>l.Confidence>=.8f && l.Text.Any(Japanese)
                && (l.Text.Count(Japanese)>=2 || l.Bounds.Height>=h*.85) && GameMarkerGeometry.Intersects(region,l.Bounds)
                && !(proposeJoinedNeutral && ReferenceEquals(l,last) && core.Y<last.Bounds.Y+last.Bounds.Height)))continue;
            MarkerAnchor anchor;PixelRect parentBounds=last.Bounds;double x,y;
            if(core.Y<last.Bounds.Y+last.Bounds.Height) {
                if(core.X<last.Bounds.X+last.Bounds.Width-h*1.7)continue;
                int end=LastNeutralColumn(frame,last.Bounds,core.X-Math.Max(5,(int)(h*.22)));
                if(end<last.Bounds.X+(int)(h*.6))continue;
                int bodyEnd=end+2;
                if(region.X-bodyEnd<h*.1 || region.X-bodyEnd>h*3)continue;
                parentBounds=parentBounds with {Width=bodyEnd-parentBounds.X};anchor=MarkerAnchor.LineEnd;
                x=(region.X-bodyEnd)/h;y=(region.Y-(last.Bounds.Y+last.Bounds.Height))/h;
                if(y< -1.1 || y>.6)continue;
            } else {
                if(region.Y<last.Bounds.Y+last.Bounds.Height+1 || region.X<left || region.X+region.Width>right+h*2)continue;
                if(!chromatic && Math.Min(Math.Abs(region.X-left),Math.Abs(region.X-last.Bounds.X-last.Bounds.Width))>h*1.2)continue;
                // A real following text row must never be treated as an indicator.
                if(rows.Any(l=>GameMarkerGeometry.Intersects(region,l.Bounds)))continue;
                anchor=MarkerAnchor.BlockBottom;x=(region.X-left)/Math.Max(h,right-left);y=(region.Y-last.Bounds.Y-last.Bounds.Height)/h;
                if(y>1.6)continue;
            }
            if(rows.Any(l=>l!=last && GameMarkerGeometry.Intersects(region,l.Bounds)))continue;
            var visual=ComponentVisual(frame,region,chromatic);if(!visual.HasShape)continue;
            var location=new MarkerLocation(anchor,x,y,region.Width/h,region.Height/h,parentBounds);
            output.Add(new(region,last with {Text=prefix,Bounds=parentBounds},location,visual));
        }
        // Rank by the two text-relative anchors, not a screen coordinate or a
        // game-specific shape. Background highlights must not crowd out the end
        // indicator. These remain proposals; animation/page evidence is required.
        var distinct=output.OrderBy(s=>s.Location.Anchor).ThenBy(s=>s.Location.X).Aggregate(new List<TerminalMarkerSample>(),(items,s)=> {
            if(!items.Any(p=>GameMarkerGeometry.Intersects(p.Bounds,s.Bounds)))items.Add(s);return items;
        });
        return distinct.OrderBy(s=>s.Location.Anchor==MarkerAnchor.BlockBottom
            ? s.Location.Y+Math.Min(Math.Abs(s.Bounds.X-left),Math.Abs(s.Bounds.X-last.Bounds.X-last.Bounds.Width))/h
            : Math.Abs(s.Location.X)+Math.Abs(s.Location.Y+.5)).Take(proposeJoinedNeutral?6:2).ToArray();
    }
    // A wrapped closing row can contain just one Japanese glyph plus punctuation.
    // Discovery, clean references and cycle recovery must agree on that row;
    // callers still verify surrounding context and the complete parent text.
    public static bool IsBodyRow(OcrLine line,float minimumConfidence=.8f)=>!line.Vertical && line.Confidence>=minimumConfidence
        && (line.Text.Count(Japanese)>=2 || line.Text.Any(Japanese) && TryCompletedPrefix(line.Text,out _));
    public static bool TryCompletedPrefix(string text,out string prefix)
    {
        text=text.Trim();int at=text.LastIndexOfAny(['。','！','？','」','』','）','…']);
        prefix=at<0?"":text[..(at+1)];
        var suffix=at<0?text:text[(at+1)..].Trim();
        return at>=0 && prefix.Any(Japanese) && suffix.Length<=4 && suffix.All(c=>char.IsAsciiLetterOrDigit(c)||char.IsPunctuation(c));
    }
    public static bool ConfirmsBody(string expected,OcrLine recognized)=>recognized.Confidence>=.85f
        && TryCompletedPrefix(recognized.Text,out var prefix) && prefix==recognized.Text.Trim()
        && string.Concat(expected.Where(c=>!char.IsWhiteSpace(c)))==string.Concat(recognized.Text.Where(c=>!char.IsWhiteSpace(c)));
    public static bool RequiresBodyConfirmation(TerminalMarkerSample sample,OcrLine parent,bool hasSeparateNoise)
        => hasSeparateNoise || parent.Text!=sample.Parent.Text || GameMarkerGeometry.Intersects(parent.Bounds,sample.Bounds);
    // A separate indicator must not force an unchanged, freshly recognized body
    // through a second rectangular crop. Its perspective/padding can produce a
    // different transcription even though no body pixel is being removed.
    public static bool FreshBodyConfirms(TerminalMarkerSample sample,OcrLine parent)
        => sample.Parent.Bounds==parent.Bounds && !GameMarkerGeometry.Intersects(sample.Bounds,parent.Bounds)
            && ConfirmsBody(sample.Parent.Text,parent);
    public static bool SeparateNoise(TerminalMarkerSample sample,OcrLine line)=>GameMarkerGeometry.Intersects(line.Bounds,sample.Bounds)
        && (line.Text.Length<=4 && line.Text.All(c=>char.IsAsciiLetterOrDigit(c)||char.IsPunctuation(c))
            || !sample.Visual.Chromatic && line.Text.Length==1 && line.Bounds.Height<sample.Parent.Bounds.Height*.8);
    public static MarkerVisual ChromaticVisual(ImageFrame frame,PixelRect region)
        =>ComponentVisual(frame,region,true);
    public static MarkerVisual ComponentVisual(ImageFrame frame,PixelRect region,bool chromatic)
    {
        if(!GameMarkerGeometry.Inside(frame,region))return new(0,0,0,0,true);
        ulong mask=0;long red=0,green=0,blue=0;int count=0;
        for(int gy=0;gy<8;gy++)for(int gx=0;gx<8;gx++) {
            int x=region.X+Math.Min(region.Width-1,(2*gx+1)*region.Width/16),y=region.Y+Math.Min(region.Height-1,(2*gy+1)*region.Height/16);
            var p=frame.Pixels.Span;int i=y*frame.Stride+x*4;
            if(!Foreground(p[i+2],p[i+1],p[i],chromatic))continue;
            mask|=1UL<<(gy*8+gx);red+=p[i+2];green+=p[i+1];blue+=p[i];count++;
        }
        return new(mask,(int)(red/Math.Max(1,count)),(int)(green/Math.Max(1,count)),(int)(blue/Math.Max(1,count)),chromatic);
    }
    private static bool Japanese(char c)=>c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';
    private static bool Chromatic(int r,int g,int b)=>Math.Max(r,Math.Max(g,b))>=180 && Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))>=35 && (r*77+g*150+b*29)/256>=90;
    private static bool Foreground(int r,int g,int b,bool chromatic)=>chromatic?Chromatic(r,g,b):Math.Min(r,Math.Min(g,b))>=180 && Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<=35;
    private static PixelRect Clip(ImageFrame f,PixelRect r) {
        int x=Math.Clamp(r.X,0,f.Width),y=Math.Clamp(r.Y,0,f.Height);
        return new(x,y,Math.Max(0,(int)Math.Min(f.Width,(long)r.X+r.Width)-x),Math.Max(0,(int)Math.Min(f.Height,(long)r.Y+r.Height)-y));
    }
    private static int LastNeutralColumn(ImageFrame frame,PixelRect row,int limit)
    {
        int end=-1,count=0;var p=frame.Pixels.Span;
        for(int x=Math.Max(0,row.X);x<Math.Min(frame.Width,Math.Min(limit,row.X+row.Width));x++)for(int y=Math.Max(0,row.Y);y<Math.Min(frame.Height,row.Y+row.Height);y++) {
            int i=y*frame.Stride+x*4,low=Math.Min(p[i],Math.Min(p[i+1],p[i+2])),hi=Math.Max(p[i],Math.Max(p[i+1],p[i+2]));
            if(low<180 || hi-low>30)continue;count++;end=x;
        }
        return count>=20?end:-1;
    }
    private static IReadOnlyList<PixelRect> Components(ImageFrame frame,PixelRect area,bool chromatic)
    {
        if(!area.IsValid)return [];
        var mask=new bool[area.Width*area.Height];var p=frame.Pixels.Span;
        for(int y=0;y<area.Height;y++)for(int x=0;x<area.Width;x++) {
            int i=(area.Y+y)*frame.Stride+(area.X+x)*4;mask[y*area.Width+x]=Foreground(p[i+2],p[i+1],p[i],chromatic);
        }
        var regions=new List<PixelRect>();var queue=new Queue<int>();
        for(int index=0;index<mask.Length;index++) {
            if(!mask[index])continue;mask[index]=false;queue.Enqueue(index);int minX=index%area.Width,maxX=minX,minY=index/area.Width,maxY=minY,n=0;
            while(queue.Count>0) {
                int current=queue.Dequeue(),x=current%area.Width,y=current/area.Width;n++;
                minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++) {
                    int nx=x+dx,ny=y+dy;if(nx<0||ny<0||nx>=area.Width||ny>=area.Height)continue;int next=ny*area.Width+nx;
                    if(mask[next]) {mask[next]=false;queue.Enqueue(next);}
                }
            }
            if(n>=6)regions.Add(new(area.X+minX,area.Y+minY,maxX-minX+1,maxY-minY+1));
        }
        return regions;
    }
}
