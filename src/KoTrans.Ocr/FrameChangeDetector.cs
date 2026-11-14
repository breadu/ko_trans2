using KoTrans.Core;
namespace KoTrans.Ocr;

public enum FrameDecision { Unchanged, Stabilizing, Ready, Probe }
// One instance per capture/profile session. No polling/capture is performed here.
public sealed class FrameChangeDetector
{
    private byte[]? previous, accepted;
    private PixelRect? scope;
    private int width, height;
    private int stable;
    public int ChangedPixels { get; private set; }
    public void Reset() { previous = accepted = null; scope = null; stable = 0; }
    public FrameDecision Evaluate(ImageFrame frame, PixelRect? textRoi = null)
    {
        if (frame.Width != width || frame.Height != height || scope != textRoi) { Reset(); width = frame.Width; height = frame.Height; scope = textRoi; }
        var roi = textRoi ?? new(0, 0, frame.Width, frame.Height);
        if (!roi.IsValid || roi.X < 0 || roi.Y < 0 || roi.X + (long)roi.Width > frame.Width || roi.Y + (long)roi.Height > frame.Height) throw new ArgumentException("ROI is outside the frame.");
        // Dense grayscale comparison inside the selected area: no fixed-grid blind spots.
        var pixels = frame.Pixels.Span; var sample = new byte[checked(roi.Width * roi.Height)];
        for (int y = 0; y < roi.Height; y++) for (int x = 0; x < roi.Width; x++)
        {
            int index = (roi.Y + y) * frame.Stride + (roi.X + x) * 4;
            sample[y * roi.Width + x] = (byte)((pixels[index] * 29 + pixels[index + 1] * 150 + pixels[index + 2] * 77) >> 8);
        }
        static int Difference(byte[] a, byte[] b) { int n = 0; for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 18) n++; return n; }
        ChangedPixels = accepted is null ? sample.Length : Difference(sample, accepted);
        if (accepted is not null && ChangedPixels < 5) { previous = sample; stable = 0; return FrameDecision.Unchanged; }
        stable = previous is not null && Difference(sample, previous) < 5 ? stable + 1 : 0; previous = sample;
        if (stable < 1) return FrameDecision.Stabilizing;
        accepted = sample; return FrameDecision.Ready;
    }
}

public sealed class TextDuplicateFilter
{
    private string? previous;
    public void Reset() => previous = null;
    public bool IsDuplicate(string text) { var normalized = text.Trim(); bool duplicate = normalized == previous; previous = normalized; return duplicate; }
}
