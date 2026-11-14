using KoTrans.Core;
namespace KoTrans.Ocr;

// Virtual pixels only: never expand the capture/ROI or read beyond the supplied frame.
internal static class DetectionBorder
{
    internal static OcrPadding? Default(int width, int height)
    {
        const int margin = 16;
        return width > 0 && height > 0 && new PixelRect(0,0,width+margin*2,height+margin*2).IsValid
            ? new(margin,margin,margin,margin) : null;
    }
    internal static PixelRect? Restore(PixelRect box, OcrPadding padding, int width, int height)
    {
        long left = Math.Max(0,(long)box.X-padding.Left), top = Math.Max(0,(long)box.Y-padding.Top);
        long right = Math.Min(width,(long)box.X+box.Width-padding.Left), bottom = Math.Min(height,(long)box.Y+box.Height-padding.Top);
        return right > left && bottom > top ? new((int)left,(int)top,(int)(right-left),(int)(bottom-top)) : null;
    }
}
