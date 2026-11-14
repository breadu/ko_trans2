using KoTrans.Core;
namespace KoTrans.Infrastructure;

public static class StudyFrame
{
    // Exact visible RGB comparison: do not reuse OCR after any visible pixel change. Alpha/X is ignored.
    public static bool SamePixels(ImageFrame a, ImageFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        var x = a.Pixels.Span; var y = b.Pixels.Span;
        for (int row = 0; row < a.Height; row++)
            for (int col = 0; col < a.Width * 4; col += 4)
                if (!x.Slice(row * a.Stride + col, 3).SequenceEqual(y.Slice(row * b.Stride + col, 3))) return false;
        return true;
    }
}
