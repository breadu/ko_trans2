using KoTrans.Core;

namespace KoTrans.Ocr;

public sealed record NvlOcrRegion(PixelRect Area, IReadOnlyList<OcrLine> Prefix);

// Only a retained image prefix can be reused. Re-read the last sentence's rows
// for inline continuation, wrapping and active-sentence fading.
public sealed class NvlOcrTracker
{
    public static OcrResult ArrangeRows(OcrResult result,OcrOptions options) => options.ReadingMode=="NVL" && !options.Vertical
        ? result with { Lines=NvlText.MergeVisualRows(result.Lines,options.Language) } : result;
    private ImageFrame? baseline;
    private OcrResult? previous;
    private NvlOcrRegion? selected;
    private string[]? rejectedPrefix;
    public bool PageBreakPending { get; private set; }
    public static bool Supports(OcrOptions options, OcrCapabilities capabilities) =>
        options.ReadingMode == "NVL" && !options.Vertical && capabilities.Roi && capabilities.LocalParagraph;
    public void Reset(bool pageBreak = false) { baseline = null; previous = null; selected = null; rejectedPrefix = null; PageBreakPending = pageBreak; }
    // A clipped/empty crop already required a full read. Until the retained
    // prefix changes, use that full read directly instead of retrying the same
    // unsuitable boundary on every animation frame. This never skips OCR.
    public void RejectRegion(NvlOcrRegion region)
    {
        rejectedPrefix = region.Prefix.Select(line => line.Text).ToArray();
        selected = null;
    }
    public bool ConsumePageBreak() { bool value = PageBreakPending; PageBreakPending = false; return value; }
    public void Observe(ImageFrame frame, OcrResult result)
    {
        if (result.Lines.Count == 0) { Reset(true); return; }
        baseline = frame; previous = result;
    }
    // Identical pixels in the complete union of both detections cannot be a new
    // glyph. Preserve an already read row rather than oscillating with detector
    // crop/CTC punctuation jitter. No pixel tolerance or text similarity heuristic.
    public OcrResult Stabilize(ImageFrame frame, OcrResult result)
    {
        if (baseline is null || previous is null || frame.Width != baseline.Width || frame.Height != baseline.Height || frame.PixelFormat!=baseline.PixelFormat) return result;
        return result with { Lines = result.Lines.Select(line => {
            var b=line.Bounds;
            var matches=previous.Lines.Where(old => old.Vertical==line.Vertical && old.Confidence>=.8
                && Math.Min(old.Bounds.X+old.Bounds.Width,b.X+b.Width)-Math.Max(old.Bounds.X,b.X) >= Math.Max(old.Bounds.Width,b.Width)*.8
                && Math.Min(old.Bounds.Y+old.Bounds.Height,b.Y+b.Height)-Math.Max(old.Bounds.Y,b.Y) >= Math.Max(old.Bounds.Height,b.Height)*.75).ToArray();
            if(matches.Length!=1) return line;
            var prior=matches[0]; var a=prior.Bounds;
            int x=Math.Min(a.X,b.X),y=Math.Min(a.Y,b.Y);
            var union=new PixelRect(x,y,Math.Max(a.X+a.Width,b.X+b.Width)-x,Math.Max(a.Y+a.Height,b.Y+b.Height)-y);
            if(!union.IsValid || x<0 || y<0 || x+(long)union.Width>frame.Width || y+(long)union.Height>frame.Height) return line;
            for(int row=y;row<y+union.Height;row++)
                if(!baseline.Pixels.Span.Slice(row*baseline.Stride+x*4,union.Width*4)
                    .SequenceEqual(frame.Pixels.Span.Slice(row*frame.Stride+x*4,union.Width*4))) return line;
            return line with { Text=prior.Text, Confidence=prior.Confidence, Bounds=union };
        }).ToArray() };
    }
    public NvlOcrRegion? Select(ImageFrame frame, bool full)
    {
        if (full) { selected = null; return null; }
        if (baseline is null || previous is null) return null;
        if (frame.Width != baseline.Width || frame.Height != baseline.Height) { Reset(true); return null; }
        var old = previous.Lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X).ToArray();
        if (old.Length == 0 || old.Any(l => l.Vertical)) return null;
        int tailStart = old.Length-1;
        while (tailStart > 0 && !NvlText.Complete(old[tailStart-1].Text)) tailStart--;
        // Detector unclip can overlap two physical rows. Never place the search
        // boundary through the next row merely because its predecessor is complete.
        while(tailStart>0 && old.Take(tailStart).Max(l=>l.Bounds.Y+l.Bounds.Height)>=old.Skip(tailStart).Min(l=>l.Bounds.Y)) tailStart--;
        var prefix = old.Take(tailStart).ToArray();
        int left = frame.Width, right = -1, top = frame.Height, bottom = -1, outside = 0;
        bool prefixChanged = false;
        var a = baseline.Pixels.Span; var b = frame.Pixels.Span;
        for (int y = 0; y < frame.Height; y++) for (int x = 0; x < frame.Width; x++) {
            int p = y * baseline.Stride + x * 4, q = y * frame.Stride + x * 4;
            if (Math.Abs(a[p]-b[q]) <= 18 && Math.Abs(a[p+1]-b[q+1]) <= 18 && Math.Abs(a[p+2]-b[q+2]) <= 18) continue;
            bool inOld = false, inPrefix = false;
            for (int i = 0; i < old.Length; i++) if (Contains(old[i].Bounds,x,y)) { inOld = true; if (i < prefix.Length) inPrefix = true; }
            if (!inOld && ++outside > (long)frame.Width*frame.Height*.20) { Reset(true); return null; }
            if (inPrefix) { prefixChanged = true; continue; }
            left = Math.Min(left,x); right = Math.Max(right,x); top = Math.Min(top,y); bottom = Math.Max(bottom,y);
        }
        if (outside > (long)frame.Width * frame.Height * .20) { Reset(true); return null; }
        if (prefixChanged) { selected = null; return null; } // No guessed glyph/colour equivalence.
        if (rejectedPrefix is not null) {
            if (prefix.Select(line => line.Text).SequenceEqual(rejectedPrefix)) return null;
            rejectedPrefix = null;
        }
        var tail = old.Skip(prefix.Length).ToArray();
        int frozenBottom = prefix.Length == 0 ? -1 : prefix.Max(l => l.Bounds.Y + l.Bounds.Height);
        if (right >= 0 && top <= frozenBottom) { selected = null; return null; } // Text elsewhere requires full reacquisition.
        left = Math.Min(left, tail.Min(l => l.Bounds.X)); right = Math.Max(right, tail.Max(l => l.Bounds.X+l.Bounds.Width)-1);
        top = Math.Min(top, tail.Min(l => l.Bounds.Y)); bottom = Math.Max(bottom, tail.Max(l => l.Bounds.Y+l.Bounds.Height)-1);
        int margin = Math.Max(20, old.Max(l => l.Bounds.Height));
        int x0 = Math.Max(0,left-margin), y0 = Math.Max(frozenBottom+1,Math.Max(0,top-margin));
        int x1 = Math.Min(frame.Width,right+margin+1), y1 = Math.Min(frame.Height,bottom+margin+1);
        var region = new PixelRect(x0,y0,x1-x0,y1-y0);
        // Repeated detector jitter must not shrink/move the same unfinished block's
        // search and rescale identical glyphs differently on every recognition.
        // The search can expand for new pixels; a changed frozen prefix resets it.
        if (selected is { } prior && prior.Prefix.SequenceEqual(prefix)) {
            int x=Math.Min(region.X,prior.Area.X), y=Math.Min(region.Y,prior.Area.Y);
            region = new(x,y,Math.Max(region.X+region.Width,prior.Area.X+prior.Area.Width)-x,
                Math.Max(region.Y+region.Height,prior.Area.Y+prior.Area.Height)-y);
        }
        if (!region.IsValid || region.Y <= frozenBottom || (long)region.Width*region.Height >= (long)frame.Width*frame.Height*.85) { selected = null; return null; }
        // A retained row can leave too little padding for the next row. This
        // crop would immediately fail the same edge check after native OCR.
        // Read the full frame once instead; never relax the clipping safeguard.
        if (tail.Any(line => TouchesSearchEdge(region, line.Bounds, frame))) { selected = null; return null; }
        return selected = new(region,prefix);
    }
    private static bool Contains(PixelRect r, int x, int y) => x >= r.X && x < r.X+r.Width && y >= r.Y && y < r.Y+r.Height;
    public static bool NeedsFullSearch(NvlOcrRegion region, OcrResult result, ImageFrame frame)
    {
        if (result.Lines.Count == 0) return true;
        return result.Lines.Any(l => TouchesSearchEdge(region.Area, l.Bounds, frame));
    }
    private static bool TouchesSearchEdge(PixelRect r, PixelRect b, ImageFrame frame) =>
        r.X > 0 && b.X <= r.X+2 || r.Y > 0 && b.Y <= r.Y+2
        || r.X+r.Width < frame.Width && b.X+b.Width >= r.X+r.Width-2
        || r.Y+r.Height < frame.Height && b.Y+b.Height >= r.Y+r.Height-2;
    public static OcrResult Merge(NvlOcrRegion region, OcrResult result, ImageFrame frame, OcrOptions options) => ArrangeRows(result with {
        // Both sets already passed the OCR service's ruby/marker filters. Their
        // bounds may now be unions of several detections, not individual glyph
        // rows. Filtering those unions again can mistake retained body text for
        // ruby of a tall union and make full/partial OCR alternate indefinitely.
        Lines = LayoutProcessor.Arrange(region.Prefix.Concat(result.Lines).ToArray(), options with { SuppressFurigana=false }, frame.Width, frame.Height),
        Candidates = region.Prefix.Concat(result.Candidates ?? result.Lines).ToArray(),
        ReusedLines = region.Prefix.Count, IncrementalArea = region.Area
    }, options);
}
