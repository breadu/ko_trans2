using KoTrans.Core;

namespace KoTrans.Ocr;

// A profile owns one tracker. Phase 3 must periodically request a full-frame probe,
// so a new dialogue outside the previous region cannot remain undiscovered.
public sealed class TextRegionTracker
{
    private PixelRect? region;
    private int width, height, misses;
    public void Reset() { region = null; width = height = misses = 0; }
    public PixelRect? GetRegion(int frameWidth, int frameHeight, bool fullFrameProbe = false)
    {
        if (frameWidth != width || frameHeight != height) Reset();
        return fullFrameProbe ? null : region;
    }
    public void Observe(OcrResult result, int frameWidth, int frameHeight)
    {
        if (!new PixelRect(0, 0, frameWidth, frameHeight).IsValid) throw new ArgumentException("Invalid frame size.");
        if (width != frameWidth || height != frameHeight) Reset();
        width = frameWidth; height = frameHeight;
        var boxes = result.Lines.Select(l => l.Bounds).Where(b => b.IsValid).ToArray();
        if (boxes.Length == 0) { if (++misses >= 2) region = null; return; }
        misses = 0;
        var scales = result.Lines.Select(l => l.Vertical ? l.Bounds.Width : l.Bounds.Height).Order().ToArray();
        int padding = scales[scales.Length / 2] * 2;
        int x = Math.Clamp(boxes.Min(b => b.X) - padding, 0, width - 1), y = Math.Clamp(boxes.Min(b => b.Y) - padding, 0, height - 1);
        int right = Math.Clamp(boxes.Max(b => b.X + b.Width) + padding, x + 1, width);
        int bottom = Math.Clamp(boxes.Max(b => b.Y + b.Height) + padding, y + 1, height);
        region = new(x, y, right - x, bottom - y);
    }
}
