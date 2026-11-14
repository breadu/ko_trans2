namespace KoTrans.Core;

// Session-local clean OCR geometry/text, never persisted in the learned pattern.
// This can propose a crop when a blinking icon corrupts the final punctuation.
// Every such proposal still requires fresh, exact recognition of the whole body.
public sealed record TerminalMarkerReference(int Width,int Height,IReadOnlyList<OcrLine> Lines)
{
    public static bool Valid(TerminalMarkerReference? value)=>value is null || value.Width is >0 and <=32768
        && value.Height is >0 and <=32768 && value.Lines is {Count:>0 and <=32}
        && value.Lines.All(l=>l is not null && l.Text is not null && l.Text.Length<=512 && float.IsFinite(l.Confidence)
            && l.Bounds.IsValid && l.Bounds.X>=0 && l.Bounds.Y>=0 && (long)l.Bounds.X+l.Bounds.Width<=value.Width
            && (long)l.Bounds.Y+l.Bounds.Height<=value.Height) && value.Lines.Sum(l=>l.Text.Length)<=4096;
    private static OcrLine[] Rows(IReadOnlyList<OcrLine> lines)=>lines.Where(l=>TerminalMarkerScanner.IsBodyRow(l))
        .OrderBy(l=>l.Bounds.Y).ThenBy(l=>l.Bounds.X).ToArray();
    public static TerminalMarkerReference? Create(ImageFrame frame,IReadOnlyList<OcrLine> lines)
    {
        var rows=Rows(lines);if(rows.Length==0)return null;
        var last=rows[^1];
        if(!TerminalMarkerScanner.TryCompletedPrefix(last.Text,out var prefix) || prefix!=last.Text.Trim())return null;
        var reference=new TerminalMarkerReference(frame.Width,frame.Height,rows);
        return Valid(reference)?reference:null;
    }
    public OcrLine? CurrentParent(ImageFrame frame,IReadOnlyList<OcrLine> lines)
    {
        if(!Valid(this) || frame.Width!=Width || frame.Height!=Height)return null;
        var current=Rows(lines);if(current.Length!=Lines.Count)return null;
        for(int i=0;i<current.Length;i++) {
            var a=Lines[i];var b=current[i];double h=a.Bounds.Height;
            if(Math.Abs(a.Bounds.X-b.Bounds.X)>h*.25 || Math.Abs(a.Bounds.Y-b.Bounds.Y)>h*.25
                || Math.Abs(a.Bounds.Height-b.Bounds.Height)>h*.25)return null;
            if(i<current.Length-1 && a.Text!=b.Text)return null;
        }
        var old=Lines[^1];var last=current[^1];
        // Only a small terminal transcription error is eligible. Appended text,
        // a different preceding row, or changes earlier in this sentence reject
        // the reference. The proposed crop then has to match old.Text EXACTLY.
        if(old.Text.Length<7 || last.Text==old.Text || Math.Abs(old.Text.Length-last.Text.Length)>2
            || !last.Text.StartsWith(old.Text[..^3],StringComparison.Ordinal)
            || Math.Abs(old.Bounds.Width-last.Bounds.Width)>old.Bounds.Height*2)return null;
        return last;
    }
}
