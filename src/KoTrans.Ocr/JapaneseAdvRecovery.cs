using KoTrans.Core;

namespace KoTrans.Ocr;

// Session-local evidence only. A stationary label is not enough to reject text:
// the established dialogue below it must also have changed in the same capture.
public sealed class JapaneseAdvRecovery
{
    private ImageFrame? baseline;
    private int dialogueTop, lineHeight;

    public static bool Supports(OcrOptions options) => options.Language == "ja"
        && options.ReadingMode == "ADV" && !options.Vertical && options.SearchArea is null
        && !DialogueRegionSelection.Supported(options);

    internal static bool ReliableBody(OcrLine line) => !line.Vertical && line.Bounds.IsValid
        && line.Confidence >= .8 && line.Bounds.Width >= line.Bounds.Height * 4
        && line.Text.Count(c => c is >= '\u3041' and <= '\u3096' or >= '\u30A1' and <= '\u30FA'
            or >= '\u3400' and <= '\u9FFF') >= 4;

    internal static bool ShortLatin(OcrLine line) => !line.Vertical && line.Bounds.IsValid
        && line.Text.Length is > 0 and <= 12 && line.Text.Any(char.IsAsciiLetter)
        && line.Text.All(c => char.IsAsciiLetter(c) || c is ' ' or '.' or ',' or '!' or '?' or '\'');

    public void Observe(ImageFrame frame, OcrResult result)
    {
        if (baseline is not null && (frame.Width != baseline.Width || frame.Height != baseline.Height)) Reset();
        var body = result.Lines.Where(l => ReliableBody(l) && Inside(l.Bounds, frame)).ToArray();
        if (body.Length == 0) return;
        baseline = frame;
        dialogueTop = body.Min(l => l.Bounds.Y);
        lineHeight = body.Select(l => l.Bounds.Height).Order().ElementAt(body.Length / 2);
    }

    public PixelRect? Retry(ImageFrame frame, OcrResult result)
    {
        if (baseline is null) return null;
        if (frame.Width != baseline.Width || frame.Height != baseline.Height) { Reset(); return null; }
        // Do not suppress English within the dialogue, or a stationary first line
        // while later rows type in. Only detached short labels above it qualify.
        if (result.Lines.Count == 0 || result.Lines.Sum(l => l.Text.Length) > 16
            || result.Lines.Any(l => !ShortLatin(l) || !Inside(l.Bounds, frame)
                || l.Bounds.Y + l.Bounds.Height > dialogueTop - Math.Max(2, lineHeight / 10)
                || !SameRgb(baseline, frame, l.Bounds))) return null;
        int top = Math.Max(0, dialogueTop - Math.Max(2, lineHeight / 4));
        // Full width and all rows below the anchor allow longer text and top-down
        // growth; never return old text or limit recognition to the old line count.
        var area = new PixelRect(0, top, frame.Width, frame.Height - top);
        return SameRgb(baseline, frame, area) ? null : area;
    }

    public void Reset() { baseline = null; dialogueTop = lineHeight = 0; }
    private static bool Inside(PixelRect box, ImageFrame frame) => box.IsValid && box.X >= 0 && box.Y >= 0
        && (long)box.X + box.Width <= frame.Width && (long)box.Y + box.Height <= frame.Height;
    private static bool SameRgb(ImageFrame a, ImageFrame b, PixelRect area)
    {
        var oldPixels = a.Pixels.Span; var newPixels = b.Pixels.Span;
        for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++) {
                int oldIndex = y * a.Stride + x * 4, newIndex = y * b.Stride + x * 4;
                if (oldPixels[oldIndex] != newPixels[newIndex] || oldPixels[oldIndex + 1] != newPixels[newIndex + 1]
                    || oldPixels[oldIndex + 2] != newPixels[newIndex + 2]) return false;
            }
        return true;
    }
}
