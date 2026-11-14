using KoTrans.Core;

namespace KoTrans.Ocr;

// Conservative actual OCR crop: every outside RGB pixel must still match the last recognized frame.
// New text anywhere outside the padded area therefore forces a full search (not just a sampled ROI).
public sealed class OcrRegionTracker
{
    private readonly TextRegionTracker tracker = new();
    private ImageFrame? baseline;
    private int croppedRuns;
    public void Reset() { baseline = null; tracker.Reset(); croppedRuns = 0; }
    public PixelRect? Select(ImageFrame frame, bool manual)
    {
        if (manual || baseline is null || baseline.Width != frame.Width || baseline.Height != frame.Height || croppedRuns >= 4) return null;
        var region = tracker.GetRegion(frame.Width, frame.Height);
        if (region is not { } box || (long)box.Width * box.Height > (long)frame.Width * frame.Height * .7) return null;
        var fullSize = OcrExecutionOptions.DetectionSize(frame.Width, frame.Height, 960, true);
        var cropSize = OcrExecutionOptions.DetectionSize(box.Width, box.Height, 960, true);
        if (cropSize.Width * cropSize.Height >= fullSize.Width * fullSize.Height * .75) return null;
        var a = baseline.Pixels.Span; var b = frame.Pixels.Span;
        for (int y = 0; y < frame.Height; y++)
        {
            var first = a.Slice(y * baseline.Stride, frame.Width * 4); var next = b.Slice(y * frame.Stride, frame.Width * 4);
            if (y >= box.Y && y < box.Y + box.Height)
            {
                if (!SameRgb(first[..(box.X * 4)], next[..(box.X * 4)]) || !SameRgb(first[((box.X + box.Width) * 4)..], next[((box.X + box.Width) * 4)..])) return null;
            }
            else if (!SameRgb(first, next)) return null;
        }
        return box;
    }
    private static bool SameRgb(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.SequenceEqual(b)) return true;
        for (int i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) return false;
        return true;
    }
    public void Observe(ImageFrame frame, OcrResult result, bool cropped)
    {
        baseline = frame;
        if (cropped) croppedRuns++;
        else { croppedRuns = 0; tracker.Observe(result, frame.Width, frame.Height); }
    }
    public static ImageFrame Crop(ImageFrame frame, PixelRect box)
    {
        if (!box.IsValid || box.X < 0 || box.Y < 0 || (long)box.X + box.Width > frame.Width || (long)box.Y + box.Height > frame.Height) throw new ArgumentOutOfRangeException(nameof(box));
        int stride = checked(box.Width * 4); var bytes = new byte[checked(stride * box.Height)];
        for (int y = 0; y < box.Height; y++) frame.Pixels.Span.Slice((y + box.Y) * frame.Stride + box.X * 4, stride).CopyTo(bytes.AsSpan(y * stride));
        return new(box.Width, box.Height, stride, bytes, frame.PixelFormat);
    }
    public static bool NeedsFullSearch(OcrResult result, PixelRect crop, bool fullFrameCoordinates = false)
        => result.Lines.Count == 0 || result.DetectedBoxes.Concat(result.Lines.Select(l => l.Bounds)).Any(box =>
            box.X <= (fullFrameCoordinates ? crop.X : 0) + 3 || box.Y <= (fullFrameCoordinates ? crop.Y : 0) + 3
            || box.X + box.Width >= (fullFrameCoordinates ? crop.X : 0) + crop.Width - 4
            || box.Y + box.Height >= (fullFrameCoordinates ? crop.Y : 0) + crop.Height - 4);
    public static OcrResult Restore(OcrResult result, PixelRect crop, ImageFrame full, OcrOptions options)
    {
        PixelRect Shift(PixelRect box) => box with { X = box.X + crop.X, Y = box.Y + crop.Y };
        // NVL lines already exclude visual annotations/continuation markers. Raw
        // diagnostic candidates must not reintroduce them when restoring a crop.
        var visible = options.ReadingMode == "NVL" ? result.Lines : result.Candidates ?? result.Lines;
        return result with { Lines = LayoutProcessor.Arrange(visible.Select(line => line with { Bounds = Shift(line.Bounds) }).ToList(), options, full.Width, full.Height),
            Candidates = (result.Candidates ?? result.Lines).Select(line => line with { Bounds = Shift(line.Bounds) }).ToArray(),
            Characters = result.Characters.Select(c => c with { Bounds = Shift(c.Bounds) }).ToArray(),
            DetectedBoxes = result.DetectedBoxes.Select(Shift).ToArray(),
            TerminalMarkers = result.TerminalMarkers?.Select(s => s with { Bounds = Shift(s.Bounds),
                Parent = s.Parent with { Bounds = Shift(s.Parent.Bounds) }, Location = s.Location with { Parent = Shift(s.Location.Parent) } }).ToArray(),
            MarkerDiagnostics = result.MarkerDiagnostics?.Select(m => m with { Bounds = Shift(m.Bounds),
                ParentBounds = m.ParentBounds is {} parent ? Shift(parent) : null }).ToArray() };
    }
}
