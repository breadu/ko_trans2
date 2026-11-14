using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public enum MarkerRecoveryFailure { None, NotObserved, IneligibleResult, NoPreviousFrame, ObservationExpired, SizeChanged,
    OutsideChanged, BodyMismatch, UnknownPose, TooManyStates, NoTextRows, IncompleteBody, UnlocalizedChange,
    NoKnownPlacement, ShapeMismatch, ProtectedText, NoEmptyPhase, WaitingForCycle, PartialCoverage, UnknownFrame,
    WaitingForRepeatedCycle, IndicatorText, IndicatorBounds, MissingBody, LayoutMismatch }

// Use the fully recognized marker-off frame as the text reference. Detector
// padding, layout joins and OCR jitter do not redefine unchanged body pixels.
// Two return cycles certify exact whole-frame states; no rectangle is masked.
// Retain one existing reference frame in session memory, bounded text and
// fingerprints. Never copy/persist images; Reset releases the reference.
internal sealed class LearnedMarkerCycleRecovery
{
    private static readonly int[] PositionOffsets=[0,-1,1,-2,2],SizeOffsets=[0,-2,2,-4,4];
    private sealed class Cycle(TerminalMarkerSample sample,PixelRect area,MarkerVisual off,ImageFrame frame,OcrResult reference,long started)
    {
        internal readonly TerminalMarkerSample Sample=sample;
        internal readonly PixelRect Area=area;
        internal readonly MarkerVisual Off=off;
        internal readonly int Width=frame.Width,Height=frame.Height,Stride=frame.Stride;
        internal readonly FramePixelFormat Format=frame.PixelFormat;
        internal readonly ImageFrame Reference=frame;
        internal readonly string Source=reference.Text;
        internal readonly OcrLine[] Lines=reference.Lines.ToArray(),Candidates=(reference.Candidates??reference.Lines).ToArray();
        internal readonly HashSet<string> OutsideStates=[OutsideKey(frame,sample.Bounds)];
        internal long Started=started;
        internal readonly HashSet<string> States=[],Verified=[];
        internal readonly Dictionary<string,int> PoseReturns=[];
        internal string LastPose="";
        internal bool Confident,Counted;
    }
    private WeakReference<ImageFrame>? previous,bound;
    private OcrResult? previousResult;
    private long previousAt;
    private Cycle? cycle;
    private string lastReused="";
    internal bool IsReady=>bound?.TryGetTarget(out _)==true && cycle is {Verified.Count:>0};
    internal long ConfirmedCycles {get;private set;}
    internal MarkerRecoveryFailure Failure {get;private set;}=MarkerRecoveryFailure.NotObserved;
    internal void Reset(){previous=bound=null;previousResult=null;cycle=null;lastReused="";Failure=MarkerRecoveryFailure.NotObserved;}
    internal OcrResult Observe(ImageFrame frame,OcrResult result,IReadOnlyList<GameMarkerPattern> patterns,long now,bool fullSearch=true,PixelRect? searchArea=null)
    {
        var original=result;
        if(!fullSearch && !CoversPriorFrame(frame,result,searchArea)) {Reset();Failure=MarkerRecoveryFailure.PartialCoverage;return result;}
        if(patterns.Count==0 || !GameMarkerGeometry.ValidPatterns(patterns) || result.Lines.Count is 0 or >32
            || (result.Candidates??result.Lines).Count>32 || result.Text.Length>4096
            || (result.Candidates??result.Lines).Any(l=>l.Text.Length>512)) {Reset();Failure=MarkerRecoveryFailure.IneligibleResult;return result;}
        if(cycle is {} c) {
            var observation=ValidateObservation(result,c.Sample,out int noise);
            Failure=now-c.Started>Math.Min(90000,ObservationBudget(result)*5) && c.Verified.Count==0?MarkerRecoveryFailure.ObservationExpired:
                !SameDimensions(c,frame)||!GameMarkerGeometry.Inside(frame,c.Area)?MarkerRecoveryFailure.SizeChanged:
                !OutsideStable(c,frame)?MarkerRecoveryFailure.OutsideChanged:observation;
            if(Failure!=MarkerRecoveryFailure.None) {
                cycle=null;bound=null;
            } else {
                string key=Key(frame);
                if(!c.States.Contains(key) && !KnownPose(c,frame,original)) {
                    Reset();Failure=MarkerRecoveryFailure.UnknownPose;return original;
                }
                if(c.States.Count==8 && !c.States.Contains(key)){Reset();Failure=MarkerRecoveryFailure.TooManyStates;return original;}
                if(!c.Confident && HasConfidentNoise(original,c.Sample.Bounds)) {
                    c.Confident=true;c.Verified.Clear();c.Started=now;bound=null;
                    // Earlier low-confidence observations do not satisfy the
                    // stronger proof for a newly confident recognized glyph.
                    c.PoseReturns.Clear();c.LastPose="";
                }
                string pose=AreaKey(frame,c.Area);
                if(c.PoseReturns.TryGetValue(pose,out int returns) && c.LastPose!=pose) {
                    c.PoseReturns[pose]=++returns;
                    if(returns >= 2) {
                        if(!c.Counted){ConfirmedCycles++;c.Counted=true;}
                        c.Verified.UnionWith(c.States);
                    }
                }
                c.States.Add(key);c.OutsideStates.Add(OutsideKey(frame,c.Sample.Bounds));
                c.PoseReturns.TryAdd(pose,0);c.LastPose=pose;
                if(c.Verified.Contains(key))result=result with {Lines=c.Lines,Candidates=c.Candidates,
                    LearnedMarkerExclusions=result.LearnedMarkerExclusions+noise};
                else Failure=MarkerRecoveryFailure.WaitingForRepeatedCycle;
            }
        }
        if(cycle is null && previousResult is {} priorResult && now-previousAt<=ObservationBudget(result)
            && previous?.TryGetTarget(out var prior)==true && Compatible(prior,frame)
            && Find(prior,priorResult,frame,original,patterns) is {} found) {
            cycle=new(found.Sample,found.Area,found.Off,found.ReferenceFrame,found.ReferenceResult,now);
            cycle.OutsideStates.Add(OutsideKey(prior,found.Sample.Bounds));cycle.OutsideStates.Add(OutsideKey(frame,found.Sample.Bounds));
            cycle.States.Add(Key(prior));cycle.States.Add(Key(frame));
            cycle.PoseReturns.TryAdd(AreaKey(prior,found.Area),0);cycle.LastPose=AreaKey(frame,found.Area);cycle.PoseReturns.TryAdd(cycle.LastPose,0);
            cycle.Confident=HasConfidentNoise(priorResult,found.Sample.Bounds)||HasConfidentNoise(original,found.Sample.Bounds);
            Failure=MarkerRecoveryFailure.WaitingForRepeatedCycle;
        }
        else if(cycle is null && (previous?.TryGetTarget(out _)!=true || previousResult is null))Failure=MarkerRecoveryFailure.NoPreviousFrame;
        previous=new(frame);previousResult=original;previousAt=now;return result;
    }
    private bool CoversPriorFrame(ImageFrame frame,OcrResult result,PixelRect? searchArea)
    {
        // A tracked ROI can still read every known character. Prove coverage
        // against the last fully covered result, not just its displayed text.
        // Unread pixels must match exactly, so chained ROIs cannot hide changes
        // or accumulate capture rounding outside their search areas.
        if(searchArea is not {} area || !GameMarkerGeometry.Inside(frame,area)
            || previousResult is not {} priorResult || previous is null || !previous.TryGetTarget(out var prior) || !Compatible(prior,frame))return false;
        static IEnumerable<PixelRect> TextBounds(OcrResult r)=>r.Lines.Concat(r.Candidates??r.Lines).Select(l=>l.Bounds)
            .Concat(r.Characters.Select(c=>c.Bounds));
        return TextBounds(priorResult).Concat(TextBounds(result)).All(box=>Contains(area,box))
            && OutsideKey(prior,area)==OutsideKey(frame,area);
    }
    internal void Bind(ImageFrame frame,OcrResult result,bool fullCoverage)
    {
        bound=null;
        if(fullCoverage && cycle is {} c && SameDimensions(c,frame) && c.Verified.Contains(Key(frame)) && result.Text==c.Source) {
            bound=new(frame);lastReused=Key(frame);Failure=MarkerRecoveryFailure.None;
        }
        else if(cycle is not null && !fullCoverage)Failure=MarkerRecoveryFailure.PartialCoverage;
    }
    internal MarkerFrameDecision Evaluate(ImageFrame baseline,ImageFrame current)
    {
        if(cycle is not {} c || bound?.TryGetTarget(out var accepted)!=true || !ReferenceEquals(accepted,baseline))return MarkerFrameDecision.Unavailable;
        if(!Compatible(baseline,current)){Reset();Failure=MarkerRecoveryFailure.SizeChanged;return MarkerFrameDecision.SizeChanged;}
        string key=Key(current);
        if(!c.Verified.Contains(key)) {
            // A new pose always needs fresh OCR and a return to a known state.
            // Bounded one-level capture rounding retains evidence only; this
            // unknown full-frame fingerprint still requires fresh OCR.
            if(!OutsideStable(c,current)){Reset();Failure=MarkerRecoveryFailure.OutsideChanged;}
            else Failure=MarkerRecoveryFailure.UnknownFrame;
            return MarkerFrameDecision.Unavailable;
        }
        bool changed=key!=lastReused;lastReused=key;
        Failure=MarkerRecoveryFailure.None;
        return changed?MarkerFrameDecision.Reused:MarkerFrameDecision.Unchanged;
    }
    private static bool KnownPose(Cycle c,ImageFrame frame,OcrResult result)
    {
        var visual=TerminalMarkerScanner.ComponentVisual(frame,c.Sample.Bounds,c.Sample.Visual.Chromatic);
        bool empty=EmptyAt(result,c.Sample.Bounds);
        return empty && visual.Mask==c.Off.Mask || Match(c.Sample.Visual,visual,c.Off)!=int.MaxValue;
    }
    private static bool SameDimensions(Cycle c,ImageFrame f)=>c.Width==f.Width && c.Height==f.Height && c.Stride==f.Stride && c.Format==f.PixelFormat;
    // The envelope is only for counting return poses. Its padding must never
    // hide new text: compare every pixel outside the actual learned shape box.
    private static bool OutsideStable(Cycle c,ImageFrame frame)=>c.OutsideStates.Contains(OutsideKey(frame,c.Sample.Bounds))
        || LearnedMarkerFrameGuard.OutsideStable(c.Reference,frame,c.Sample.Bounds);
    private (TerminalMarkerSample Sample,PixelRect Area,MarkerVisual Off,ImageFrame ReferenceFrame,OcrResult ReferenceResult)? Find(ImageFrame before,OcrResult old,ImageFrame current,OcrResult result,IReadOnlyList<GameMarkerPattern> patterns)
    {
        var rows=(result.Candidates??result.Lines).Where(l=>TerminalMarkerScanner.IsBodyRow(l,.85f)
            && GameMarkerGeometry.Inside(current,l.Bounds)).OrderBy(l=>l.Bounds.Y).ToArray();
        Failure=MarkerRecoveryFailure.NoTextRows;
        if(rows.Length==0 || rows.Sum(l=>l.Text.Count(Japanese))<4)return null;
        double h=rows.Select(l=>l.Bounds.Height).Order().ElementAt(rows.Length/2);
        Failure=MarkerRecoveryFailure.IncompleteBody;
        var parent=rows.Last();if(!TerminalMarkerScanner.ConfirmsBody(parent.Text,parent))return null;
        Failure=MarkerRecoveryFailure.UnlocalizedChange;
        if(Change(before,current,(int)Math.Ceiling(h*2)) is not {} change)return null;
        int left=rows.Min(l=>l.Bounds.X),right=rows.Max(l=>l.Bounds.X+l.Bounds.Width);
        (TerminalMarkerSample Sample,PixelRect Area,MarkerVisual Off,ImageFrame ReferenceFrame,OcrResult ReferenceResult)? best=null;int score=int.MaxValue;
        Failure=MarkerRecoveryFailure.NoKnownPlacement;
        // Locate the actual blinking pixels. A learned sprite can move between
        // the end of a row and the following row without changing its identity.
        // Stored offsets are hints for learning, not an absolute search centre.
        foreach(var pattern in patterns.Take(12)) {
            foreach(int dx in PositionOffsets)foreach(int dy in PositionOffsets)
            foreach(int dw in SizeOffsets)foreach(int dh in SizeOffsets) {
                var box=new PixelRect(change.X+dx,change.Y+dy,change.Width+dw,change.Height+dh);
                if(!GameMarkerGeometry.Inside(current,box) || !Contains(box,change) || Locate(box,parent.Bounds,left,right,h) is not {} location
                    || !SizeMatches(pattern,location))continue;
                int pad=Math.Clamp((int)(h*.3),3,32),ax=Math.Max(0,box.X-pad),ay=Math.Max(0,box.Y-pad);
                if(location.Anchor==MarkerAnchor.LineEnd)ax=Math.Max(ax,parent.Bounds.X+parent.Bounds.Width+2);
                else ay=Math.Max(ay,parent.Bounds.Y+parent.Bounds.Height+2);
                var area=new PixelRect(ax,ay,Math.Min(current.Width,box.X+box.Width+pad)-ax,Math.Min(current.Height,box.Y+box.Height+pad)-ay);
                if(area.Width>192 || area.Height>192 || !Contains(area,box) || !Contains(area,change)
                    || rows.Any(l=>GameMarkerGeometry.Intersects(l.Bounds,area)))continue;
                // Change already checked every pixel and bounded the total
                // rounding noise. Avoid rescanning the full frame per placement.
                if(Failure==MarkerRecoveryFailure.NoKnownPlacement)Failure=MarkerRecoveryFailure.ShapeMismatch;
                foreach(var shape in pattern.Shapes.Take(8)) {
                    var a=TerminalMarkerScanner.ComponentVisual(before,box,shape.Chromatic);var b=TerminalMarkerScanner.ComponentVisual(current,box,shape.Chromatic);
                    bool oldEmpty=EmptyAt(old,box);
                    bool newEmpty=EmptyAt(result,box);
                    if(!oldEmpty && !newEmpty) {if(Failure==MarkerRecoveryFailure.ShapeMismatch)Failure=MarkerRecoveryFailure.NoEmptyPhase;continue;}
                    int ab=newEmpty?Match(shape,a,b):int.MaxValue,ba=oldEmpty?Match(shape,b,a):int.MaxValue;
                    int match=Math.Min(ab,ba);if(match==int.MaxValue)continue;
                    Failure=MarkerRecoveryFailure.ProtectedText;
                    int cost=match+Math.Abs(dx)+Math.Abs(dy)+Math.Abs(dw)+Math.Abs(dh);if(cost>=score)continue;
                    var sample=new TerminalMarkerSample(box,parent,location,shape);
                    Failure=ValidateObservation(result,sample,out _);if(Failure!=MarkerRecoveryFailure.None)continue;
                    Failure=ValidateObservation(old,sample,out _);if(Failure!=MarkerRecoveryFailure.None)continue;
                    // Use the complete off-frame result, including every other
                    // recognized row. Never trim/rebuild a displayed OCR string.
                    best=ab<=ba?(sample,area,b,current,result):(sample,area,a,before,old);score=cost;
                }
            }
        }
        return best;
    }
    private static bool SizeMatches(GameMarkerPattern pattern,MarkerLocation location)
    {
        if(Math.Abs(pattern.Width-location.Width)<=.25 && Math.Abs(pattern.Height-location.Height)<=.2)return true;
        foreach(var p in pattern.Placements??[])
            if(Math.Abs(p.Width-location.Width)<=.25 && Math.Abs(p.Height-location.Height)<=.2)return true;
        return false;
    }
    private static MarkerLocation? Locate(PixelRect box,PixelRect parent,int left,int right,double h)
    {
        double x=(box.X-parent.X-parent.Width)/h,y=(box.Y-parent.Y-parent.Height)/h;
        if(box.Width/h is <.15 or >1.65 || box.Height/h is <.2 or >1.2)return null;
        if(x is >=.1 and <=3 && y is >=-1.1 and <=.6)
            return new(MarkerAnchor.LineEnd,x,y,box.Width/h,box.Height/h,parent);
        if(y is >=.1 and <=1.6 && box.X>=left && box.X+box.Width<=right+2*h
            && Math.Min(Math.Abs(box.X-left),Math.Abs(box.X-parent.X-parent.Width))<=h*1.2)
            return new(MarkerAnchor.BlockBottom,(box.X-left)/Math.Max(h,right-left),y,box.Width/h,box.Height/h,parent);
        return null;
    }
    // Extra bright pixels are allowed ONLY when they are present in both phases.
    // At least half the known shape must visibly blink; unseen/different glyphs
    // and static highlights cannot satisfy this condition.
    private static int Match(MarkerVisual known,MarkerVisual on,MarkerVisual off)
    {
        if(Math.Abs(known.Red-on.Red)+Math.Abs(known.Green-on.Green)+Math.Abs(known.Blue-on.Blue)>72)return int.MaxValue;
        int missing=BitOperations.PopCount(known.Mask&~on.Mask),extra=BitOperations.PopCount(on.Mask&~off.Mask&~known.Mask);
        int visible=BitOperations.PopCount(on.Mask&~off.Mask&known.Mask);
        return missing<=3 && extra<=3 && visible>=Math.Max(4,(BitOperations.PopCount(known.Mask)+1)/2)?missing*3+extra*2:int.MaxValue;
    }
    private static bool EmptyAt(OcrResult result,PixelRect shape)=>!result.Lines.Concat(result.Candidates??result.Lines)
        .Any(l=>GameMarkerGeometry.Intersects(l.Bounds,shape));
    private static MarkerRecoveryFailure ValidateObservation(OcrResult result,TerminalMarkerSample sample,out int noiseCount)
    {
        noiseCount=0;
        if(result.Characters.Count>0)return MarkerRecoveryFailure.ProtectedText;
        var candidates=result.Candidates??result.Lines;
        var body=candidates.FirstOrDefault(l=>!l.Vertical && l.Confidence>=.85f && l.Text==sample.Parent.Text
            && Math.Abs(l.Bounds.X-sample.Parent.Bounds.X)<=2 && Math.Abs(l.Bounds.Y-sample.Parent.Bounds.Y)<=2);
        if(body is null || !TerminalMarkerScanner.ConfirmsBody(sample.Parent.Text,body))return MarkerRecoveryFailure.MissingBody;
        var noise=candidates.Where(l=>GameMarkerGeometry.Intersects(l.Bounds,sample.Bounds)).ToArray();
        double h=sample.Parent.Bounds.Height;
        foreach(var line in noise) {
            if(line.Vertical || line.Text.Length is 0 or >4 || !line.Text.All(c=>char.IsAsciiLetterOrDigit(c)||char.IsPunctuation(c)||char.IsSymbol(c)
                || c is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ'))return MarkerRecoveryFailure.IndicatorText;
            // An OCR box is an observation, not the exclusion mask. Permit
            // ordinary detection padding, but never a body-overlapping/large
            // text box. The output comes from the independently read off frame.
            int pad=(int)Math.Ceiling(h*.5);var shape=sample.Bounds;
            var vicinity=new PixelRect(shape.X-pad,shape.Y-pad,shape.Width+2*pad,shape.Height+2*pad);
            if(line.Bounds.Width>h*2 || line.Bounds.Height>h*1.5 || !Contains(vicinity,line.Bounds)
                || candidates.Any(l=>!ReferenceEquals(l,line) && l.Text.Any(Japanese) && GameMarkerGeometry.Intersects(l.Bounds,line.Bounds)))
                return MarkerRecoveryFailure.IndicatorBounds;
        }
        foreach(var line in result.Lines) {
            if(!GameMarkerGeometry.Intersects(line.Bounds,sample.Bounds))continue;
            if(candidates.Any(n=>n.Bounds==line.Bounds && n.Text==line.Text))continue;
            // Reject unexplained display suffixes. This verifies provenance;
            // it does not rebuild or filter the reference result's other text.
            var pieces=candidates.Where(l=>Contains(line.Bounds,l.Bounds)).OrderBy(l=>l.Bounds.X).ToArray();
            if(line.Vertical || pieces.Length==0 || pieces.Any(l=>l.Vertical))return MarkerRecoveryFailure.LayoutMismatch;
            int x=pieces.Min(l=>l.Bounds.X),y=pieces.Min(l=>l.Bounds.Y);
            var union=new PixelRect(x,y,pieces.Max(l=>l.Bounds.X+l.Bounds.Width)-x,pieces.Max(l=>l.Bounds.Y+l.Bounds.Height)-y);
            if(union!=line.Bounds || string.Concat(pieces.Select(l=>l.Text))!=line.Text)return MarkerRecoveryFailure.LayoutMismatch;
        }
        noiseCount=noise.Length;return MarkerRecoveryFailure.None;
    }
    private static bool HasConfidentNoise(OcrResult result,PixelRect area)=>(result.Candidates??result.Lines)
        .Any(l=>l.Confidence>=.8f && GameMarkerGeometry.Intersects(l.Bounds,area));
    private static PixelRect? Change(ImageFrame a,ImageFrame b,int limit)
    {
        int left=b.Width,top=b.Height,right=-1,bottom=-1,count=0,rounding=0;
        for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++) {
            int i=y*a.Stride+x*4,j=y*b.Stride+x*4;var first=a.Pixels.Span;var next=b.Pixels.Span;
            if(first[i]==next[j]&&first[i+1]==next[j+1]&&first[i+2]==next[j+2]
                && (b.PixelFormat==FramePixelFormat.Bgrx32||first[i+3]==next[j+3]))continue;
            if(Math.Abs(first[i]-next[j])<=1 && Math.Abs(first[i+1]-next[j+1])<=1 && Math.Abs(first[i+2]-next[j+2])<=1
                && (b.PixelFormat==FramePixelFormat.Bgrx32||first[i+3]==next[j+3])) {
                if(++rounding>Math.Clamp((long)b.Width*b.Height/1000,8,256))return null;
                continue;
            }
            count++;left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
            if(right-left+1>limit || bottom-top+1>limit)return null;
        }
        return count<6?null:new(left,top,right-left+1,bottom-top+1);
    }
    private static string Key(ImageFrame frame)=>Convert.ToHexString(SHA256.HashData(frame.Pixels.Span));
    // The proof needs several fresh OCR results. Slow inference must not expire
    // it by itself; idle gaps are still bounded and all content checks remain.
    private static long ObservationBudget(OcrResult result)=>(long)Math.Clamp(result.InferenceTime.TotalMilliseconds+5000,5000,30000);
    private static string AreaKey(ImageFrame frame,PixelRect area)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for(int y=area.Y;y<area.Y+area.Height;y++)hash.AppendData(frame.Pixels.Span.Slice(y*frame.Stride+area.X*4,area.Width*4));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string OutsideKey(ImageFrame frame,PixelRect area)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for(int y=0;y<frame.Height;y++) {
            var row=frame.Pixels.Span.Slice(y*frame.Stride,frame.Width*4);
            if(y<area.Y || y>=area.Y+area.Height)hash.AppendData(row);
            else {hash.AppendData(row[..(area.X*4)]);hash.AppendData(row[((area.X+area.Width)*4)..]);}
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static bool Japanese(char c)=>c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';
    private static bool Compatible(ImageFrame a,ImageFrame b)=>a.Width==b.Width && a.Height==b.Height && a.PixelFormat==b.PixelFormat;
    private static bool Contains(PixelRect a,PixelRect b)=>a.IsValid && b.IsValid && b.X>=a.X && b.Y>=a.Y && b.X+(long)b.Width<=a.X+(long)a.Width && b.Y+(long)b.Height<=a.Y+(long)a.Height;
}
