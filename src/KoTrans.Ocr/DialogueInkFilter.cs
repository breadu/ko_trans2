using KoTrans.Core;
using OpenCvSharp;

namespace KoTrans.Ocr;

// A dim background fold can be decoded as a short letter/number at normal row
// height. Only reject weak detached candidates when a bright dialogue baseline
// is established and the candidate has neither matching ink nor strong contrast.
internal static class DialogueInkFilter
{
    internal static List<OcrLine> Exclude(Mat image, List<OcrLine> lines, OcrOptions options)
    {
        if (options.Language != "ja" || options.ReadingMode != "ADV" || options.Vertical || options.KeepLocalParagraph)
            return lines;
        var suspects = lines.Where(l => !l.Vertical && l.Confidence < .8 && l.Text.Length is > 0 and <= 2
            && l.Text.All(char.IsAsciiLetterOrDigit) && Inside(image, l.Bounds)).ToArray();
        if (suspects.Length == 0) return lines;
        var bodies = lines.Where(l => JapaneseAdvRecovery.ReliableBody(l) && l.Confidence >= .9
            && HasBrightRow(image, l.Bounds)).ToArray();
        if (bodies.Length == 0) return lines;
        return lines.Where(l => !suspects.Contains(l) || !bodies.Any(b => DetachedBelow(l.Bounds, b.Bounds))
            || lines.Any(b => b != l && b.Confidence >= .8 && SameRow(l.Bounds, b.Bounds)
                && !NestedFragment(l.Bounds, b.Bounds))
            || !DimLowContrast(image, l.Bounds)).ToList();
    }

    private static bool Inside(Mat image, PixelRect b) => b.IsValid && b.X >= 0 && b.Y >= 0
        && b.X + (long)b.Width <= image.Width && b.Y + (long)b.Height <= image.Height;
    private static bool SameRow(PixelRect a, PixelRect b) =>
        Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) > Math.Min(a.Height, b.Height) * .5;
    // A second recognition of a small piece of the same patch is not independent
    // evidence for a dialogue row. Keep that confident fragment itself; after the
    // weak scenery bridge is removed, normal layout can select the actual body.
    private static bool NestedFragment(PixelRect outer, PixelRect inner) => inner.Height < outer.Height * .55
        && Math.Max(0, Math.Min(outer.X + outer.Width, inner.X + inner.Width) - Math.Max(outer.X, inner.X))
        * (double)Math.Max(0, Math.Min(outer.Y + outer.Height, inner.Y + inner.Height) - Math.Max(outer.Y, inner.Y))
        >= inner.Width * (double)inner.Height * .8;
    private static bool DetachedBelow(PixelRect mark, PixelRect body) =>
        mark.Y >= body.Y + body.Height * 1.25 && mark.Y <= body.Y + body.Height * 3
        && mark.X >= body.X && mark.X + mark.Width <= body.X + body.Width
        && mark.Width <= body.Height * 2 && mark.Height <= body.Height * 1.6;

    private static bool HasBrightRow(Mat image, PixelRect b)
    {
        if (!Inside(image, b)) return false;
        using var mask = new Mat(b.Height, b.Width, MatType.CV_8UC1, Scalar.Black);
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) {
            var p = image.At<Vec3b>(b.Y + y, b.X + x);
            int low = Math.Min(p.Item0, Math.Min(p.Item1, p.Item2)), high = Math.Max(p.Item0, Math.Max(p.Item1, p.Item2));
            if (low >= 180 && high - low <= 35) mask.Set(y, x, (byte)255);
        }
        using var labels = new Mat(); using var stats = new Mat(); using var centres = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centres);
        var glyphs = new List<PixelRect>();
        for (int i = 1; i < count; i++) {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3);
            if (x <= 0 || y <= 0 || x + w >= b.Width || y + h >= b.Height || stats.At<int>(i, 4) < 8
                || h < Math.Max(8, b.Height * .35) || h > b.Height * .9 || w < 2 || w > h * 1.5) continue;
            glyphs.Add(new(x, y, w, h));
        }
        return glyphs.Any(anchor => {
            var row = glyphs.Where(g => Math.Min(g.Height, anchor.Height) >= Math.Max(g.Height, anchor.Height) * .6
                && Math.Abs(g.Y + g.Height - anchor.Y - anchor.Height) <= Math.Max(g.Height, anchor.Height) * .2).ToArray();
            return row.Length >= 3 && row.Max(g => g.X + g.Width) - row.Min(g => g.X) >= b.Width * .55;
        });
    }

    private static bool DimLowContrast(Mat image, PixelRect b)
    {
        var histogram = new int[256];
        for (int y = b.Y; y < b.Y + b.Height; y++) for (int x = b.X; x < b.X + b.Width; x++) {
            var p = image.At<Vec3b>(y, x);
            // Preserve bright coloured glyphs too, even if they differ from the body.
            if (Math.Max(p.Item0, Math.Max(p.Item1, p.Item2)) >= 180) return false;
            histogram[(p.Item0 * 29 + p.Item1 * 150 + p.Item2 * 77) >> 8]++;
        }
        int total = b.Width * b.Height, seen = 0, low = -1;
        for (int i = 0; i < histogram.Length; i++) {
            seen += histogram[i];
            if (low < 0 && seen >= Math.Max(1, total * .05)) low = i;
            // A dark but clearly drawn number/letter is not background evidence.
            if (seen >= total * .95) return i - low <= 60;
        }
        return false;
    }
}
