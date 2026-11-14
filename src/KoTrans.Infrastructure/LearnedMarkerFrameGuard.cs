using System.Collections.Generic;
using System.Numerics;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public enum MarkerFrameDecision { Unavailable, Unchanged, Reused, BodyChanged, Uncertain, SizeChanged }
public enum MarkerFrameFailure { None, NoVerifiedFrame, SizeChanged, NoKnownPose, BodyGeometryChanged, UnknownSilhouette, OutsideChanged, UnsupportedPixelChanged, ProtectedTextOverlap, UncertainBackground, PartialCoverage, NoTerminalCandidate, PatternMismatch, MultipleKnownPoses }
public interface ILearnedMarkerFrameGate
{
    MarkerLearningStatus LearningStatus {get;}
    // Accepted by OCR; presentation/translation may still be waiting for quiet.
    void ObserveAcceptedFrame(ImageFrame frame,OcrResult result,bool fullCoverage);
    MarkerFrameDecision EvaluateMarkerFrame(ImageFrame baseline,ImageFrame current);
    void ResetMarkerFrames();
}

// Keeps masks/geometry only, never an additional screenshot. It can only reuse
// the exact accepted frame still owned by the pipeline (weak identity binding).
internal sealed class LearnedMarkerFrameGuard
{
    private sealed record Sprite(bool[] Support,ulong A,ulong B,ulong C,ulong D,ulong E,ulong F,ulong G,ulong H,ulong Detail);
    private WeakReference<ImageFrame>? accepted;
    private OcrLine[] rows=[];
    private PixelRect envelope;
    private TerminalMarkerSample? marker;
    private Sprite? original;
    private ulong lastPixels;
    private bool active;
    private readonly HashSet<ulong> verifiedEmpty=[];
    internal MarkerFrameFailure Failure {get;private set;}=MarkerFrameFailure.NoVerifiedFrame;
    internal bool IsReady=>active;
    internal int NoisePixels {get;private set;}
    private readonly List<(ulong A,ulong B,ulong C,ulong D,ulong E,ulong F,ulong G,ulong H,ulong Detail)> verifiedSilhouettes=[];
    internal void Reset() {active=false;accepted=null;rows=[];marker=null;original=null;NoisePixels=0;Failure=MarkerFrameFailure.NoVerifiedFrame;}
    internal void Observe(ImageFrame frame,OcrResult result,IReadOnlyList<GameMarkerPattern> patterns,bool fullCoverage)
    {
        NoisePixels=0;
        var candidates=TerminalMarkerScanner.Scan(frame,result.Lines);
        // Native OCR already verified proposals before returning them. Layout
        // filtering and the two-candidate discovery limit must not discard that
        // evidence and start unrelated background discovery again.
        var samples=(result.TerminalMarkers??[]).Concat(candidates)
            .Where(s=>GameMarkerGeometry.Inside(frame,s.Bounds) && GameMarkerGeometry.Inside(frame,s.Parent.Bounds)
                && patterns.Any(p=>GameMarkerGeometry.AppearanceMatches(p,s)))
            .DistinctBy(s=>s.Bounds).ToArray();
        // An invisible state also needs fresh full OCR. Only an unchanged body,
        // unchanged outside pixels and no detected text in this envelope qualify.
        var emptyFailure=MarkerFrameFailure.None;
        if(fullCoverage && patterns.Count>0 && samples.Length==0 && marker is not null && original is not null
            && accepted is not null && accepted.TryGetTarget(out var prior) && prior.Width==frame.Width && prior.Height==frame.Height && prior.PixelFormat==frame.PixelFormat
            && ConfirmEmpty(prior,frame,result,out emptyFailure)) {
            // Body pixels are stable, with only bounded one-level rounding.
            // OCR can nevertheless disagree on
            // a glyph or punctuation when the detector's padding changes with
            // the blink. Do not require equality of that noisy transcription.
            ulong key=PixelKey(frame);if(verifiedEmpty.Count==8)verifiedEmpty.Clear();verifiedEmpty.Add(key);
            original=new(new bool[envelope.Width*envelope.Height],0,0,0,0,0,0,0,0,0);
            accepted=new(frame);lastPixels=key;active=true;Failure=MarkerFrameFailure.None;return;
        }
        Reset();if(!fullCoverage){Failure=MarkerFrameFailure.PartialCoverage;return;}if(patterns.Count==0)return;
        if(samples.Length!=1){Failure=emptyFailure!=MarkerFrameFailure.None?emptyFailure:candidates.Count==0 ? MarkerFrameFailure.NoTerminalCandidate
            : samples.Length==0 ? MarkerFrameFailure.PatternMismatch : MarkerFrameFailure.MultipleKnownPoses;return;}
        var sample=samples[0];
        var parent=(result.Candidates??result.Lines).FirstOrDefault(l=>l.Bounds.X==sample.Parent.Bounds.X && l.Bounds.Y==sample.Parent.Bounds.Y);
        if(parent is null || !TerminalMarkerScanner.ConfirmsBody(sample.Parent.Text,parent)){Failure=MarkerFrameFailure.BodyGeometryChanged;return;}
        if(!result.Lines.Any(l=>GameMarkerGeometry.Intersects(l.Bounds,parent.Bounds) && l.Text.Contains(parent.Text,StringComparison.Ordinal))) {
            Failure=MarkerFrameFailure.BodyGeometryChanged;return;
        }
        rows=result.Lines.Select(l=>l.Bounds==parent.Bounds && l.Text==parent.Text?sample.Parent:l).ToArray();
        int h=sample.Parent.Bounds.Height,pad=Math.Clamp(h/2,3,48);
        int left=Math.Max(0,sample.Bounds.X-pad),top=Math.Max(0,sample.Bounds.Y-pad);
        int right=Math.Min(frame.Width,sample.Bounds.X+sample.Bounds.Width+pad),bottom=Math.Min(frame.Height,sample.Bounds.Y+sample.Bounds.Height+pad);
        if(sample.Location.Anchor==MarkerAnchor.LineEnd)left=Math.Max(left,sample.Parent.Bounds.X+sample.Parent.Bounds.Width+Math.Max(2,h/12));
        else top=Math.Max(top,sample.Parent.Bounds.Y+sample.Parent.Bounds.Height+2);
        // Clip the observation margin away from previous/adjacent real rows.
        foreach(var row in rows) {
            var b=row.Bounds;if(b.X>=right || b.X+b.Width<=left)continue;
            if(b.Y+b.Height<=sample.Bounds.Y)top=Math.Max(top,b.Y+b.Height+2);
            else if(b.Y>=sample.Bounds.Y+sample.Bounds.Height)bottom=Math.Min(bottom,b.Y-2);
        }
        envelope=new(left,top,right-left,bottom-top);
        if(!envelope.IsValid || envelope.Width>192 || envelope.Height>192 || !Contains(envelope,sample.Bounds)
            || rows.Any(l=>GameMarkerGeometry.Intersects(envelope,l.Bounds))) {Reset();Failure=MarkerFrameFailure.ProtectedTextOverlap;return;}
        var sprite=Describe(frame,sample);if(sprite is null){Reset();Failure=MarkerFrameFailure.UncertainBackground;return;}
        var signature=(sprite.A,sprite.B,sprite.C,sprite.D,sprite.E,sprite.F,sprite.G,sprite.H,sprite.Detail);
        if(!verifiedSilhouettes.Contains(signature)) {
            if(verifiedSilhouettes.Count==16)verifiedSilhouettes.RemoveAt(0);
            verifiedSilhouettes.Add(signature);
        }
        original=sprite;marker=sample;accepted=new(frame);lastPixels=PixelKey(frame);active=true;Failure=MarkerFrameFailure.None;
    }
    internal MarkerFrameDecision Evaluate(ImageFrame baseline,ImageFrame current,IReadOnlyList<GameMarkerPattern> patterns)
    {
        NoisePixels=0;
        if(!active || accepted is null || !accepted.TryGetTarget(out var bound) || !ReferenceEquals(bound,baseline) || marker is null || original is null)return MarkerFrameDecision.Unavailable;
        if(baseline.Width!=current.Width || baseline.Height!=current.Height || baseline.PixelFormat!=current.PixelFormat)return Reject(MarkerFrameDecision.SizeChanged,MarkerFrameFailure.SizeChanged);
        var boundSample=marker with {Visual=TerminalMarkerScanner.ComponentVisual(current,marker.Bounds,marker.Visual.Chromatic)};
        var samples=boundSample.Visual.HasShape && patterns.Any(p=>GameMarkerGeometry.AppearanceMatches(p,boundSample))
            ? new[]{boundSample}
            : TerminalMarkerScanner.Scan(current,rows).Where(s=>Contains(envelope,s.Bounds)
                && patterns.Any(p=>GameMarkerGeometry.AppearanceMatches(p,s))).ToArray();
        Sprite? sprite;
        if(samples.Length==0 && verifiedEmpty.Contains(PixelKey(current)))
            sprite=new(new bool[envelope.Width*envelope.Height],0,0,0,0,0,0,0,0,0);
        else {
            if(samples.Length!=1)return Reject(MarkerFrameDecision.Uncertain,MarkerFrameFailure.NoKnownPose);
            var sample=samples[0];
            if(sample.Parent.Bounds!=marker.Parent.Bounds || sample.Location.Anchor!=marker.Location.Anchor)return Reject(MarkerFrameDecision.BodyChanged,MarkerFrameFailure.BodyGeometryChanged);
            sprite=Describe(current,sample);
            if(sprite is null || !verifiedSilhouettes.Contains((sprite.A,sprite.B,sprite.C,sprite.D,sprite.E,sprite.F,sprite.G,sprite.H,sprite.Detail)))return Reject(MarkerFrameDecision.Uncertain,MarkerFrameFailure.UnknownSilhouette);
        }
        int rounding=0;
        for(int y=0;y<current.Height;y++) {
            var a=baseline.Pixels.Span.Slice(y*baseline.Stride,baseline.Width*4);
            var b=current.Pixels.Span.Slice(y*current.Stride,current.Width*4);
            if(a.SequenceEqual(b))continue;
            for(int x=0;x<current.Width;x++) {
                int i=x*4;if(a[i]==b[i] && a[i+1]==b[i+1] && a[i+2]==b[i+2]
                    && (current.PixelFormat==FramePixelFormat.Bgrx32 || a[i+3]==b[i+3]))continue;
                bool inside=x>=envelope.X && x<envelope.X+envelope.Width && y>=envelope.Y && y<envelope.Y+envelope.Height;
                if(inside) {
                    int at=(y-envelope.Y)*envelope.Width+x-envelope.X;
                    if(original.Support[at] || sprite.Support[at])continue;
                }
                // Even inside the envelope, unrelated new strokes are not ignored.
                if(RoundingOnly(a,b,i,current.PixelFormat) && ++rounding<=NoiseBudget(current))continue;
                return Reject(MarkerFrameDecision.BodyChanged,inside ? MarkerFrameFailure.UnsupportedPixelChanged : MarkerFrameFailure.OutsideChanged);
            }
        }
        NoisePixels=rounding;
        ulong key=PixelKey(current);bool changed=key!=lastPixels;lastPixels=key;
        return changed ? MarkerFrameDecision.Reused : MarkerFrameDecision.Unchanged;
    }
    // Not an image-similarity threshold: at most one RGB level, exact alpha,
    // and at most 0.1% of pixels (8 minimum, 256 maximum). Always compare with
    // the OCR-verified baseline so successive small changes cannot accumulate.
    private static int NoiseBudget(ImageFrame frame)=>Math.Clamp((int)((long)frame.Width*frame.Height/1000),8,256);
    private static bool RoundingOnly(ReadOnlySpan<byte> a,ReadOnlySpan<byte> b,int i,FramePixelFormat format)
        => Math.Abs(a[i]-b[i])<=1 && Math.Abs(a[i+1]-b[i+1])<=1 && Math.Abs(a[i+2]-b[i+2])<=1
            && (format==FramePixelFormat.Bgrx32 || a[i+3]==b[i+3]);
    internal static bool OutsideStable(ImageFrame before,ImageFrame after,PixelRect area)
    {
        if(before.Width!=after.Width || before.Height!=after.Height || before.PixelFormat!=after.PixelFormat)return false;
        int rounding=0;
        for(int y=0;y<after.Height;y++) {
            var a=before.Pixels.Span.Slice(y*before.Stride,before.Width*4);
            var b=after.Pixels.Span.Slice(y*after.Stride,after.Width*4);
            if(a.SequenceEqual(b))continue;
            for(int x=0;x<after.Width;x++) {
                if(x>=area.X && x<area.X+area.Width && y>=area.Y && y<area.Y+area.Height)continue;
                int i=x*4;
                if(a[i]==b[i] && a[i+1]==b[i+1] && a[i+2]==b[i+2]
                    && (after.PixelFormat==FramePixelFormat.Bgrx32 || a[i+3]==b[i+3]))continue;
                if(!RoundingOnly(a,b,i,after.PixelFormat) || ++rounding>NoiseBudget(after))return false;
            }
        }
        return true;
    }
    private ulong PixelKey(ImageFrame frame)
    {
        ulong value=14695981039346656037;var p=frame.Pixels.Span;
        for(int y=envelope.Y;y<envelope.Y+envelope.Height;y++)for(int x=envelope.X;x<envelope.X+envelope.Width;x++) {
            int i=y*frame.Stride+x*4;value=(value^p[i])*1099511628211;value=(value^p[i+1])*1099511628211;value=(value^p[i+2])*1099511628211;
            if(frame.PixelFormat==FramePixelFormat.Bgra32)value=(value^p[i+3])*1099511628211;
        }
        return value;
    }
    private MarkerFrameDecision Reject(MarkerFrameDecision reason,MarkerFrameFailure failure){active=false;Failure=failure;return reason;}
    private Sprite? Describe(ImageFrame frame,TerminalMarkerSample sample)
    {
        int size=envelope.Width*envelope.Height;var foreground=new bool[size];var visited=new bool[size];var queue=new Queue<int>();
        var pixels=frame.Pixels.Span;
        // Contrast with the local border includes the icon's dark outline. A
        // large/connected background still fails the border/area checks below.
        int minR=255,minG=255,minB=255,maxR=0,maxG=0,maxB=0;
        for(int y=0;y<envelope.Height;y++)for(int x=0;x<envelope.Width;x++)if(x==0||y==0||x==envelope.Width-1||y==envelope.Height-1) {
            int i=(envelope.Y+y)*frame.Stride+(envelope.X+x)*4;
            minB=Math.Min(minB,pixels[i]);maxB=Math.Max(maxB,pixels[i]);
            minG=Math.Min(minG,pixels[i+1]);maxG=Math.Max(maxG,pixels[i+1]);
            minR=Math.Min(minR,pixels[i+2]);maxR=Math.Max(maxR,pixels[i+2]);
        }
        for(int y=0;y<envelope.Height;y++)for(int x=0;x<envelope.Width;x++) {
            int i=(envelope.Y+y)*frame.Stride+(envelope.X+x)*4,r=pixels[i+2],g=pixels[i+1],b=pixels[i];
            int hi=Math.Max(r,Math.Max(g,b)),low=Math.Min(r,Math.Min(g,b));
            bool chromatic=hi>=180 && hi-low>=35 && (r*77+g*150+b*29)/256>=90;
            bool neutral=low>=170 && hi-low<=60;
            bool contrast=r<minR-8 || r>maxR+8 || g<minG-8 || g>maxG+8 || b<minB-8 || b>maxB+8;
            bool inside=x+envelope.X>=sample.Bounds.X && x+envelope.X<sample.Bounds.X+sample.Bounds.Width
                && y+envelope.Y>=sample.Bounds.Y && y+envelope.Y<sample.Bounds.Y+sample.Bounds.Height;
            int at=y*envelope.Width+x;
            // A neutral component has already been separated from real text by
            // the scanner. Do not flood through the colourful photo behind it.
            // Its outline is covered below, including EVERY native-resolution
            // edge; this does not turn the surrounding rectangle into a mask.
            foreground[at]=sample.Visual.Chromatic ? chromatic||neutral||contrast : neutral&&inside;
            if((sample.Visual.Chromatic?chromatic:neutral) && inside) {visited[at]=true;queue.Enqueue(at);}
        }
        if(queue.Count==0)return null;
        int minX=envelope.Width,minY=envelope.Height,maxX=-1,maxY=-1,count=0;
        while(queue.Count>0) {
            int at=queue.Dequeue(),x=at%envelope.Width,y=at/envelope.Width;count++;
            minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);
            if(x==0 || y==0 || x==envelope.Width-1 || y==envelope.Height-1)return null;
            // Light components of the same icon can be separated by its dark
            // outline. Join across that outline; unrelated joined strokes still
            // change the verified silhouette and therefore require OCR.
            for(int dy=-3;dy<=3;dy++)for(int dx=-3;dx<=3;dx++) {
                int nx=x+dx,ny=y+dy;if(nx<0||ny<0||nx>=envelope.Width||ny>=envelope.Height)continue;int next=ny*envelope.Width+nx;
                if(foreground[next]&&!visited[next]){visited[next]=true;queue.Enqueue(next);}
            }
        }
        if(count<6 || count>size*.6)return null;
        ulong[] bits=new ulong[8];int w=maxX-minX+1,h=maxY-minY+1;
        for(int gy=0;gy<16;gy++)for(int gx=0;gx<16;gx++) {
            int x=minX+Math.Min(w-1,(2*gx+1)*w/32),y=minY+Math.Min(h-1,(2*gy+1)*h/32),bit=gy*16+gx;
            if(visited[y*envelope.Width+x])bits[bit/64]|=1UL<<(bit%64);
            // Internal contrast is part of a verified pose too. A new glyph
            // painted inside the icon must not pass on outer silhouette alone.
            int i=(envelope.Y+y)*frame.Stride+(envelope.X+x)*4;
            int j=(envelope.Y+y)*frame.Stride+(envelope.X+Math.Min(envelope.Width-1,x+1))*4;
            int k=(envelope.Y+Math.Min(envelope.Height-1,y+1))*frame.Stride+(envelope.X+x)*4;
            int light=(pixels[i]*29+pixels[i+1]*150+pixels[i+2]*77)>>8;
            int horizontal=(pixels[j]*29+pixels[j+1]*150+pixels[j+2]*77)>>8;
            int vertical=(pixels[k]*29+pixels[k+1]*150+pixels[k+2]*77)>>8;
            if(Math.Abs(light-horizontal)>24 || Math.Abs(light-vertical)>24
                || frame.PixelFormat==FramePixelFormat.Bgra32 && (Math.Abs(pixels[i+3]-pixels[j+3])>24 || Math.Abs(pixels[i+3]-pixels[k+3])>24))
                bits[4+bit/64]|=1UL<<(bit%64);
        }
        // Outline width follows the glyph scale, including enlarged pixel art.
        // All of this support's native-resolution edges are verified below.
        int outline=Math.Clamp((int)Math.Ceiling(sample.Bounds.Height*.15),2,8);
        var support=(bool[])visited.Clone();
        for(int y=0;y<envelope.Height;y++)for(int x=0;x<envelope.Width;x++)if(visited[y*envelope.Width+x])
            for(int dy=-outline;dy<=outline;dy++)for(int dx=-outline;dx<=outline;dx++) {
                int nx=x+dx,ny=y+dy;if(nx>=0 && ny>=0 && nx<envelope.Width && ny<envelope.Height)support[ny*envelope.Width+nx]=true;
            }
        // A fixed sampling grid misses strokes between samples on enlarged icons.
        // Include every foreground/edge position at native resolution. Only a
        // bounded fingerprint is retained; uniform colour animation still works.
        ulong detail=14695981039346656037;
        detail=(detail^(uint)w)*1099511628211;detail=(detail^(uint)h)*1099511628211;
        int detailLeft=Math.Max(0,minX-outline),detailTop=Math.Max(0,minY-outline);
        int detailRight=Math.Min(envelope.Width-2,maxX+outline),detailBottom=Math.Min(envelope.Height-2,maxY+outline);
        for(int y=detailTop;y<=detailBottom;y++)for(int x=detailLeft;x<=detailRight;x++) {
            int i=(envelope.Y+y)*frame.Stride+(envelope.X+x)*4;
            int j=i+4,k=i+frame.Stride; // The connected sprite never touches the envelope border.
            int light=(pixels[i]*29+pixels[i+1]*150+pixels[i+2]*77)>>8;
            int horizontal=(pixels[j]*29+pixels[j+1]*150+pixels[j+2]*77)>>8;
            int vertical=(pixels[k]*29+pixels[k+1]*150+pixels[k+2]*77)>>8;
            uint feature=visited[y*envelope.Width+x]?1u:0u;
            if(Math.Abs(light-horizontal)>24)feature|=2;
            if(Math.Abs(light-vertical)>24)feature|=4;
            if(frame.PixelFormat==FramePixelFormat.Bgra32) {
                if(Math.Abs(pixels[i+3]-pixels[j+3])>24)feature|=8;
                if(Math.Abs(pixels[i+3]-pixels[k+3])>24)feature|=16;
            }
            detail=(detail^feature)*1099511628211;
        }
        return new(support,bits[0],bits[1],bits[2],bits[3],bits[4],bits[5],bits[6],bits[7],detail);
    }
    private static bool Contains(PixelRect outer,PixelRect inner)=>inner.X>=outer.X && inner.Y>=outer.Y && inner.X+inner.Width<=outer.X+outer.Width && inner.Y+inner.Height<=outer.Y+outer.Height;
    private bool ConfirmEmpty(ImageFrame prior,ImageFrame frame,OcrResult result,out MarkerFrameFailure failure)
    {
        failure=MarkerFrameFailure.OutsideChanged;
        if(!OutsideStable(prior,frame,envelope))return false;
        var lines=result.Lines.Concat(result.Candidates??result.Lines);
        if(lines.Any(l=>OverlapsUnverifiedText(l))) {
            // A detector box can acquire empty right padding when the marker
            // disappears. Only the same complete body, with bounded padding,
            // can use this stronger pixel proof. Other text remains protected.
            failure=MarkerFrameFailure.ProtectedTextOverlap;
            if(lines.Any(l=>OverlapsUnverifiedText(l,true)))return false;
            if(!verifiedEmpty.Contains(PixelKey(frame)) && !KnownMarkerDisappeared(prior,frame,out failure))return false;
        }
        failure=MarkerFrameFailure.None;return true;
    }
    private bool KnownMarkerDisappeared(ImageFrame prior,ImageFrame frame,out MarkerFrameFailure failure)
    {
        failure=MarkerFrameFailure.UnknownSilhouette;
        if(marker is not {} known || original is null)return false;
        if(!original.Support.Any(b=>b)) {
            // A newly OCR-checked empty pose may differ only by capture
            // rounding. Compare the ENTIRE frame, with no excluded rectangle.
            failure=MarkerFrameFailure.UnsupportedPixelChanged;
            if(!verifiedEmpty.Contains(PixelKey(prior)) || !OutsideStable(prior,frame,default))return false;
            failure=MarkerFrameFailure.None;return true;
        }
        var visual=TerminalMarkerScanner.ComponentVisual(frame,known.Bounds,known.Visual.Chromatic);
        int knownBits=BitOperations.PopCount(known.Visual.Mask);
        if(knownBits<6 || BitOperations.PopCount(known.Visual.Mask&visual.Mask)*4>knownBits)return false;
        // Every changed native-resolution pixel must belong to the previously
        // verified sprite support. Never ignore the enclosing rectangle.
        int rounding=0;
        for(int y=0;y<frame.Height;y++)for(int x=0;x<frame.Width;x++) {
            int i=y*prior.Stride+x*4,j=y*frame.Stride+x*4;
            var a=prior.Pixels.Span.Slice(i,4);var b=frame.Pixels.Span.Slice(j,4);
            if(a[0]==b[0] && a[1]==b[1] && a[2]==b[2] && (frame.PixelFormat==FramePixelFormat.Bgrx32 || a[3]==b[3]))continue;
            if(x>=envelope.X && x<envelope.X+envelope.Width && y>=envelope.Y && y<envelope.Y+envelope.Height
                && original.Support[(y-envelope.Y)*envelope.Width+x-envelope.X])continue;
            if(RoundingOnly(a,b,0,frame.PixelFormat) && ++rounding<=NoiseBudget(frame))continue;
            failure=MarkerFrameFailure.UnsupportedPixelChanged;return false;
        }
        failure=MarkerFrameFailure.None;return true;
    }
    private bool OverlapsUnverifiedText(OcrLine line,bool verifiedDisappearance=false)
    {
        if(!GameMarkerGeometry.Intersects(line.Bounds,envelope))return false;
        if(marker is not {Location.Anchor:MarkerAnchor.LineEnd} known)return true;
        var body=known.Parent.Bounds;var box=line.Bounds;double margin=body.Height*.25;
        // A blink changes detector padding around the same complete sentence.
        // Larger padding needs the separate native-pixel disappearance proof;
        // exact fresh body text and unchanged outside pixels are always required.
        double rightMargin=verifiedDisappearance?body.Height*.5:margin;
        int edgePadding=verifiedDisappearance?Math.Clamp(body.Height/16,1,3):0;
        return !TerminalMarkerScanner.ConfirmsBody(known.Parent.Text,line)
            || Math.Abs(box.X-body.X)>margin || Math.Abs(box.Y-body.Y)>margin || Math.Abs(box.Height-body.Height)>margin
            || box.X+box.Width>known.Bounds.X+edgePadding || box.X+box.Width-body.X-body.Width>rightMargin;
    }
}
