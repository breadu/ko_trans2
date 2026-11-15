using System.Text;
using KoTrans.Core;
using OpenCvSharp;

namespace KoTrans.Ocr;

// A detector may start in the middle of an elongated horizontal utterance.
// Recover only a connected row of visible light glyphs before that box. No
// preceding frame, inferred word, character substitution or new model is used.
internal static class DialoguePrefixRecovery
{
    internal static List<OcrLine> Recover(Mat image, List<OcrLine> lines, OcrOptions options,
        Func<Mat, (string Text, float Confidence)> recognize)
    {
        if (options.Language != "ja" || options.ReadingMode != "ADV" || options.Vertical) return lines;
        var output = lines.ToList(); int attempts = 0;
        var area = options.SearchArea ?? new PixelRect(0, 0, image.Width, image.Height);
        foreach (var line in lines) {
            if (attempts >= 2) break;
            if (line.Vertical || line.Confidence < .8 || line.Text.Length is < 3 or > 24 || Japanese(line.Text) == 0
                || FindPrefix(image, line.Bounds, area, lines.Where(l => l != line).Select(l => l.Bounds)) is not { } crop) continue;
            attempts++;
            using var source = new Mat(image, new Rect(crop.X, crop.Y, crop.Width, crop.Height));
            using var mask = new Mat(); using var prepared = new Mat();
            // Used only after a light glyph chain proves a missing prefix. Do not
            // binarize normal dialogue or erase colours from the capture itself.
            Cv2.InRange(source, new Scalar(180, 180, 180), Scalar.White, mask);
            Cv2.CvtColor(mask, prepared, ColorConversionCodes.GRAY2BGR);
            var result = recognize(prepared);
            string text = TextProcessing.Normalize(result.Text, options.Language);
            string tail = line.Text.Normalize(NormalizationForm.FormKC);
            string normalized = text.Normalize(NormalizationForm.FormKC);
            if (tail.Length < 3 || result.Confidence < .9 || result.Confidence < line.Confidence - .08
                || text.Length < line.Text.Length + 2 || text.Length > 80 || Japanese(text) < 2
                || !normalized.EndsWith(tail[^3..], StringComparison.Ordinal)) continue;
            output[output.IndexOf(line)] = line with {Text=text, Confidence=result.Confidence, Bounds=crop};
        }
        return output;
    }

    private static int Japanese(string text) => text.Count(c => c is >= '\u3041' and <= '\u3096'
        or >= '\u30A1' and <= '\u30FA' or >= '\u3400' and <= '\u9FFF');

    internal static PixelRect? FindPrefix(Mat image, PixelRect box, PixelRect area, IEnumerable<PixelRect> otherBoxes)
    {
        int h = box.Height;
        if (!box.IsValid || !area.IsValid || h < 12 || box.Width < h * 3 || box.X < area.X || box.Y < area.Y
            || box.X + (long)box.Width > area.X + area.Width || box.Y + (long)h > area.Y + area.Height
            || area.X < 0 || area.Y < 0 || area.X + (long)area.Width > image.Width || area.Y + (long)area.Height > image.Height) return null;
        int start = Math.Max(area.X, box.X - h * 12), end = box.X + box.Width;
        if (box.X - start < h * 1.5) return null;
        using var strip = new Mat(image, new Rect(start, box.Y, end - start, h));
        using var mask = new Mat();
        Cv2.InRange(strip, new Scalar(180, 180, 180), Scalar.White, mask);
        using var labels = new Mat(); using var stats = new Mat(); using var centres = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centres);
        var parts = new List<PixelRect>();
        for (int i = 1; i < count; i++) {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), height = stats.At<int>(i, 3);
            // Clipped scenery and broad highlights cannot extend a text row.
            if (x == 0 || y == 0 || y + height >= h || x + w >= end - start
                || stats.At<int>(i, 4) < 4 || w < 2 || height < 2 || height > h * .85 || w > h * 1.1) continue;
            bool glyph = height >= h * .35 && w <= height * 1.5;
            bool dash = w >= h * .4 && height <= h * .25 && y >= h * .2 && y + height <= h * .75;
            if (glyph || dash) parts.Add(new(start + x, box.Y + y, w, height));
        }
        if (parts.Count(p => p.X >= box.X && p.Height >= h * .35) < 2) return null;
        var prefix = new List<PixelRect>(); int left = box.X;
        foreach (var part in parts.Where(p => p.X < box.X).OrderByDescending(p => p.X)) {
            if (left - part.X - part.Width > h * .4) break;
            prefix.Add(part); left = Math.Min(left, part.X);
        }
        // Several consecutive glyphs, including two full-height glyphs, are
        // required. A lone label, punctuation or a distant name is not enough.
        if (prefix.Count < 3 || prefix.Count(p => p.Height >= h * .35) < 2 || box.X - left < h * 1.5) return null;
        left = Math.Max(start, left - Math.Max(2, (int)Math.Ceiling(h * .2)));
        var crop = new PixelRect(left, box.Y, end - left, h);
        if (otherBoxes.Any(b => b.X < box.X && b.X + b.Width > left
            && Math.Min(b.Y + b.Height, box.Y + h) - Math.Max(b.Y, box.Y) > Math.Min(b.Height, h) * .5)) return null;
        return crop;
    }
}
