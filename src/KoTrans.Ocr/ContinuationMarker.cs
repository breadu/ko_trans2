using KoTrans.Core;
using OpenCvSharp;

namespace KoTrans.Ocr;

// Conservative handling of a separately detected, differently coloured page-advance marker.
// Never strip a letter/digit from a recognized sentence or modify the captured pixels.
internal static class ContinuationMarker
{
    internal static IReadOnlyList<OcrLine> Exclude(IReadOnlyList<OcrLine> lines, Mat image, OcrOptions options, Action<OcrMarkerObservation>? observe = null)
    {
        if (options.Language != "ja" || options.ReadingMode is not ("ADV" or "NVL") || options.Vertical) {
            if (observe is not null) foreach (var line in lines.Where(l => l.Text.Length <= 4))
                observe(OcrMarkerDiagnostics.Describe(line, OcrMarkerStage.SeparateCandidate, OcrMarkerDecision.PolicyDisabled));
            return lines;
        }
        // Preserve the existing NVL letter rule. The numeric misread can also
        // occur in ADV profiles, but still needs every visual marker condition.
        return lines.Where(candidate => {
            if (options.ReadingMode == "ADV" && candidate.Text.Length==1 && char.IsAsciiLetter(candidate.Text[0])) {
                observe?.Invoke(OcrMarkerDiagnostics.Describe(candidate,OcrMarkerStage.SeparateCandidate,OcrMarkerDecision.AdvLetterPreserved)); return true;
            }
            var decision = Evaluate(candidate,lines,image,out var parent,out var components,out var neutral,out var overlap);
            // A nearby line is diagnostic context, never evidence for deleting the candidate.
            if(observe is not null && parent is null) parent=lines.Where(l=>l!=candidate && !l.Vertical
                && l.Bounds.X+l.Bounds.Width<=candidate.Bounds.X+candidate.Bounds.Width
                && Math.Abs(l.Bounds.Y-candidate.Bounds.Y)<=Math.Max(l.Bounds.Height,candidate.Bounds.Height))
                .OrderByDescending(l=>l.Bounds.X+l.Bounds.Width).FirstOrDefault();
            if (candidate.Text.Length <= 4 || decision is OcrMarkerDecision.ExcludedDifferentColour or OcrMarkerDecision.ExcludedChromaticCore)
                observe?.Invoke(OcrMarkerDiagnostics.Describe(candidate,OcrMarkerStage.SeparateCandidate,decision,parent,components,neutral,overlap));
            return decision is not (OcrMarkerDecision.ExcludedDifferentColour or OcrMarkerDecision.ExcludedChromaticCore);
        }).ToArray();
    }
    private static OcrMarkerDecision Evaluate(OcrLine candidate, IReadOnlyList<OcrLine> lines, Mat image,
        out OcrLine? related, out int? components, out int? neutral, out int? overlap)
    {
        related=null; components=neutral=overlap=null;
        bool single = candidate.Text.Length == 1 && (char.IsAsciiLetter(candidate.Text[0]) || char.IsDigit(candidate.Text[0]));
        bool shortNoise = candidate.Text.Length is >= 2 and <= 4
            && candidate.Text.All(c => char.IsAsciiLetterOrDigit(c) || char.IsPunctuation(c))
            && candidate.Bounds.Width >= candidate.Bounds.Height*1.35;
        if (candidate.Vertical || !(single || shortNoise)) return OcrMarkerDecision.NotShortCandidate;
        var c = candidate.Bounds;
        if (!Inside(c, image)) return OcrMarkerDecision.OutsideImage;
        if (shortNoise) { components=ChromaticCore(image,c).Count; if(components != 1) return OcrMarkerDecision.MultipleColourComponents; }
        var reason=OcrMarkerDecision.NoCompletedParent;
        foreach (var parent in lines) {
            if (parent == candidate || parent.Vertical || parent.Confidence < .8 || !CompletedJapanese(parent.Text)) continue;
            related=parent;
            reason=OcrMarkerDecision.GeometryMismatch;
            var b = parent.Bounds;
            if (!Inside(b, image) || b.Width < b.Height * 4 || c.Height < b.Height * .2 || c.Height > b.Height * .65
                || c.Width > b.Height * (shortNoise ? 1.3 : 1.0) || c.X < b.X + b.Width - (shortNoise ? b.Height*.4 : 0) || c.X - b.X - b.Width > b.Height * .9
                || c.Y < b.Y + b.Height * .15 || c.Y + c.Height > b.Y + b.Height * 1.1) continue;
            if(c.X < b.X+b.Width && (!shortNoise || candidate.Confidence >= .8)) {
                overlap=BrightNeutral(image,new(c.X,Math.Max(b.Y,c.Y),b.X+b.Width-c.X,Math.Min(b.Y+b.Height,c.Y+c.Height)-Math.Max(b.Y,c.Y)));
                if(overlap>3) {reason=OcrMarkerDecision.BodyOverlap;continue;}
            }
            int bodyWidth = Math.Min(b.Width, b.Height * 3);
            var tail = new PixelRect(b.X + b.Width - bodyWidth, b.Y, bodyWidth, b.Height);
            if (Foreground(image, c, out var symbol) && Foreground(image, tail, out var text) && DifferentColour(symbol, text)) return OcrMarkerDecision.ExcludedDifferentColour;
            neutral=BrightNeutral(image,tail);
            if (neutral>=30) { components=ChromaticCore(image,c).Count; if(components>0) return OcrMarkerDecision.ExcludedChromaticCore; }
            reason=OcrMarkerDecision.ColourUncertain;
        }
        return reason;
    }
    internal static bool CompletedJapanese(string text) => text.Length >= 4 && "。！？!?」』".Contains(text[^1])
        && text.Any(ch => ch is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF');

    // Detect a compact coloured terminal component even when DB unclip has joined
    // it to a neutral text box. The caller must re-recognize the retained body and
    // confirm Japanese closing punctuation before using this optional crop.
    internal static PixelRect? SeparateTail(Mat image, PixelRect box)
        => SeparateTail(image,box,out _);
    internal static PixelRect? SeparateTail(Mat image, PixelRect box, out OcrMarkerDecision reason)
    {
        reason=OcrMarkerDecision.InvalidBox;
        if (!Inside(box,image) || box.Height < 12 || box.Width < box.Height*4) return null;
        int start=Math.Max(box.X,box.X+box.Width-box.Height*2);
        var area=new PixelRect(start,box.Y,box.X+box.Width-start,box.Height);
        var components=ChromaticCore(image,area).Where(c => c.Height >= box.Height*.2 && c.Height <= box.Height*.65
            && c.Width <= box.Height*1.3 && c.X > box.X+box.Height*3
            && c.Y >= box.Y+box.Height*.1 && c.Y+c.Height <= box.Y+box.Height*.95).ToArray();
        reason=OcrMarkerDecision.NoUniqueTailComponent;
        if (components.Length != 1) return null;
        var marker=components[0];
        var body=new PixelRect(box.X,box.Y,marker.X-Math.Max(3,box.Height/4)-box.X,box.Height);
        reason=OcrMarkerDecision.InsufficientNeutralBody;
        if (!body.IsValid || BrightNeutral(image,body) < 30) return null;
        // Bright neutral lettering must end before the coloured component. A
        // coloured shape crossing/behind a real glyph is not a separable marker.
        var adjacent=new PixelRect(body.X+body.Width,box.Y,box.X+box.Width-body.X-body.Width,box.Height);
        reason=OcrMarkerDecision.NeutralInMargin;
        if (BrightNeutral(image,adjacent) > 3) return null;
        reason=OcrMarkerDecision.CropProposed;
        return body;
    }
    private static int BrightNeutral(Mat image,PixelRect r)
    {
        int count=0;
        for(int y=r.Y;y<r.Y+r.Height;y++) for(int x=r.X;x<r.X+r.Width;x++) {
            var p=image.At<Vec3b>(y,x); int low=Math.Min(p.Item0,Math.Min(p.Item1,p.Item2)),high=Math.Max(p.Item0,Math.Max(p.Item1,p.Item2));
            if(low>=180 && high-low<=30) count++;
        }
        return count;
    }
    private static IReadOnlyList<PixelRect> ChromaticCore(Mat image,PixelRect r)
    {
        using var mask=new Mat(r.Height,r.Width,MatType.CV_8UC1,Scalar.Black);
        for(int y=0;y<r.Height;y++) for(int x=0;x<r.Width;x++) {
            var p=image.At<Vec3b>(r.Y+y,r.X+x); int low=Math.Min(p.Item0,Math.Min(p.Item1,p.Item2)),high=Math.Max(p.Item0,Math.Max(p.Item1,p.Item2));
            if(high>=180 && high-low>=35 && Light(p)>=80) mask.Set(y,x,(byte)255);
        }
        using var labels=new Mat(); using var stats=new Mat(); using var centres=new Mat();
        int count=Cv2.ConnectedComponentsWithStats(mask,labels,stats,centres);
        var output=new List<PixelRect>();
        for(int i=1;i<count;i++) {
            int pixels=stats.At<int>(i,4), w=stats.At<int>(i,2),h=stats.At<int>(i,3);
            if(pixels<8 || pixels/(double)(w*h)<.15 || w<3 || h<3) continue;
            output.Add(new(r.X+stats.At<int>(i,0),r.Y+stats.At<int>(i,1),w,h));
        }
        return output;
    }
    private static bool Inside(PixelRect r, Mat image) => r.IsValid && r.X >= 0 && r.Y >= 0
        && (long)r.X + r.Width <= image.Width && (long)r.Y + r.Height <= image.Height;
    private static int Light(Vec3b p) => (p.Item0 * 29 + p.Item1 * 150 + p.Item2 * 77) >> 8;
    private static byte Median(List<byte> values) { values.Sort(); return values[values.Count / 2]; }
    private static bool Foreground(Mat image, PixelRect r, out Vec3b colour)
    {
        colour = default;
        var edge = new List<Vec3b>();
        for (int x = r.X; x < r.X + r.Width; x++) { edge.Add(image.At<Vec3b>(r.Y, x)); edge.Add(image.At<Vec3b>(r.Y + r.Height - 1, x)); }
        for (int y = r.Y + 1; y < r.Y + r.Height - 1; y++) { edge.Add(image.At<Vec3b>(y, r.X)); edge.Add(image.At<Vec3b>(y, r.X + r.Width - 1)); }
        var bg = new Vec3b(Median(edge.Select(p => p.Item0).ToList()), Median(edge.Select(p => p.Item1).ToList()), Median(edge.Select(p => p.Item2).ToList()));
        // An unreliable background estimate cannot justify deleting a detected letter.
        if (edge.Average(p => (Math.Abs(p.Item0-bg.Item0) + Math.Abs(p.Item1-bg.Item1) + Math.Abs(p.Item2-bg.Item2)) / 3d) > 12) return false;
        int light = Light(bg);
        var blue = new List<byte>(); var green = new List<byte>(); var red = new List<byte>();
        for (int y = r.Y; y < r.Y + r.Height; y++) for (int x = r.X; x < r.X + r.Width; x++) {
            var p = image.At<Vec3b>(y, x);
            int delta = Light(p) - light;
            if ((light < 128 ? delta >= 25 : delta <= -25)
                && Math.Abs(p.Item0-bg.Item0) + Math.Abs(p.Item1-bg.Item1) + Math.Abs(p.Item2-bg.Item2) >= 75) {
                blue.Add(p.Item0); green.Add(p.Item1); red.Add(p.Item2);
            }
        }
        double fraction = blue.Count / (double)(r.Width * r.Height);
        if (blue.Count < 6 || fraction is < .04 or > .7) return false;
        colour = new(Median(blue), Median(green), Median(red)); return true;
    }
    private static bool DifferentColour(Vec3b a, Vec3b b)
    {
        double sumA = a.Item0+a.Item1+a.Item2, sumB = b.Item0+b.Item1+b.Item2;
        if (sumA < 30 || sumB < 30) return false;
        double raw = Math.Pow(a.Item0-b.Item0,2) + Math.Pow(a.Item1-b.Item1,2) + Math.Pow(a.Item2-b.Item2,2);
        double chroma = Math.Pow(255*a.Item0/sumA-255*b.Item0/sumB,2)
            + Math.Pow(255*a.Item1/sumA-255*b.Item1/sumB,2) + Math.Pow(255*a.Item2/sumA-255*b.Item2/sumB,2);
        // Ignore simple brightness/antialiasing differences, including white vs grey text.
        return raw >= 50*50 && chroma >= 20*20;
    }
}
