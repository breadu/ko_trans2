using KoTrans.Core;
using OpenCvSharp;

namespace KoTrans.Ocr;

// A skewed detector fragment can include background above a short horizontal row.
// Retry uncertain overlaps or a vertically inflated dialogue box only when the
// pixels establish a single glyph row. Its true height also separates scenery.
internal static class OverlappingRowRecovery
{
    internal static List<OcrLine> Recover(Mat image, List<OcrLine> lines, OcrOptions options,
        Func<PixelRect, (string Text, float Confidence)> recognize)
    {
        if (options.Language != "ja" || options.ReadingMode != "ADV" || options.Vertical) return lines;
        var remaining = lines.ToList(); var output = lines.ToList(); int attempts = 0;
        while (remaining.Count > 0 && attempts < 2) {
            var group = new List<OcrLine> { remaining[0] }; remaining.RemoveAt(0);
            bool added;
            do {
                var neighbors = remaining.Where(l => group.Any(g => Overlaps(l, g))).ToArray();
                added = neighbors.Length > 0;
                foreach (var line in neighbors) { group.Add(line); remaining.Remove(line); }
            } while (added);
            bool overlap = group.Count >= 2 && group.Any(l => l.Confidence < .85);
            bool inflated = group.Count == 1 && JapaneseAdvRecovery.ReliableBody(group[0])
                && group[0].Bounds.Width / (double)group[0].Text.Length < group[0].Bounds.Height * .65;
            if ((!overlap && !inflated) || !group.Any(l => Japanese(l.Text) >= 1)) continue;
            int x = group.Min(l => l.Bounds.X), y = group.Min(l => l.Bounds.Y);
            var bounds = new PixelRect(x, y, group.Max(l => l.Bounds.X + l.Bounds.Width) - x,
                group.Max(l => l.Bounds.Y + l.Bounds.Height) - y);
            if (FindRow(image, bounds) is not { } crop) continue;
            attempts++;
            var result = recognize(crop);
            // An already accurate sentence cannot gain 0.02 confidence near 1.
            // Exact fresh agreement validates the tighter geometry independently
            // of changing text, so scenery is not promoted to a following row.
            if (inflated && TextProcessing.Normalize(result.Text, options.Language) == group[0].Text
                && result.Confidence >= .9 && result.Confidence >= group[0].Confidence - .02) {
                output.Remove(group[0]); output.Add(group[0] with {Bounds=crop}); continue;
            }
            if (result.Confidence < .9 || result.Confidence < group.Average(l => l.Confidence) + (overlap ? .05 : .02)
                || result.Text.Length is < 2 or > 80 || Japanese(result.Text) < 2) continue;
            if (inflated && TextProcessing.CharacterErrorRate(group[0].Text, result.Text) > .15) continue;
            foreach (var line in group) output.Remove(line);
            output.Add(new(TextProcessing.Normalize(result.Text, options.Language), result.Confidence, crop));
        }
        return output;
    }

    private static int Japanese(string text) => text.Count(c => c is >= '\u3041' and <= '\u3096'
        or >= '\u30A1' and <= '\u30FA' or >= '\u3400' and <= '\u9FFF');
    private static bool Overlaps(OcrLine a, OcrLine b)
    {
        if (a.Vertical || b.Vertical) return false;
        var x = a.Bounds; var y = b.Bounds;
        return Math.Min(x.Height, y.Height) >= Math.Max(x.Height, y.Height) * .6
            && Math.Min(x.X + x.Width, y.X + y.Width) - Math.Max(x.X, y.X) > Math.Min(x.Width, y.Width) * .25
            && Math.Min(x.Y + x.Height, y.Y + y.Height) - Math.Max(x.Y, y.Y) > Math.Min(x.Height, y.Height) * .65;
    }

    internal static PixelRect? FindRow(Mat image, PixelRect bounds)
    {
        if (!bounds.IsValid || bounds.X < 0 || bounds.Y < 0 || bounds.Width < bounds.Height * 2
            || bounds.X + (long)bounds.Width > image.Width || bounds.Y + (long)bounds.Height > image.Height) return null;
        using var mask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.Black);
        for (int y = 0; y < bounds.Height; y++) for (int x = 0; x < bounds.Width; x++) {
            var p = image.At<Vec3b>(bounds.Y + y, bounds.X + x);
            int low = Math.Min(p.Item0, Math.Min(p.Item1, p.Item2)), high = Math.Max(p.Item0, Math.Max(p.Item1, p.Item2));
            if (low >= 180 && high - low <= 35) mask.Set(y, x, (byte)255);
        }
        using var labels = new Mat(); using var stats = new Mat(); using var centres = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centres);
        var glyphs = new List<PixelRect>();
        for (int i = 1; i < count; i++) {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3);
            // Clipped patches, broad highlights and thin dashes cannot establish a text baseline.
            if (x == 0 || y == 0 || x + w >= bounds.Width || y + h >= bounds.Height
                || stats.At<int>(i, 4) < 8 || h < 8 || h > bounds.Height * .7 || w < 2 || w > h * 1.5) continue;
            glyphs.Add(new(x, y, w, h));
        }
        var rows = glyphs.Select(anchor => glyphs.Where(g => Math.Min(g.Height, anchor.Height) >= Math.Max(g.Height, anchor.Height) * .6
            && Math.Abs(g.Y + g.Height - anchor.Y - anchor.Height) <= Math.Max(g.Height, anchor.Height) * .2).ToArray())
            .Where(row => row.Length >= 3 && row.Max(g => g.X + g.Width) - row.Min(g => g.X) >= bounds.Width * .55).ToArray();
        if (rows.Length == 0) return null;
        var selected = rows.MaxBy(row => row.Length)!;
        int height = selected.Select(g => g.Height).Order().ElementAt(selected.Length / 2);
        int bottom = selected.Max(g => g.Y + g.Height), top = selected.Min(g => g.Y);
        // Never collapse two visible lines into one retry crop.
        if (rows.Any(row => Math.Abs(row.Max(g => g.Y + g.Height) - bottom) > height * .7)) return null;
        if (glyphs.Any(g => g.Height >= height * .65 && Math.Abs(g.Y + g.Height - bottom) > height * .7)) return null;
        int padding = Math.Max(2, (int)Math.Ceiling(height * .2));
        top = Math.Max(0, top - padding); bottom = Math.Min(bounds.Height, bottom + padding);
        if (bottom - top > bounds.Height * .75) return null;
        return new(bounds.X, bounds.Y + top, bounds.Width, bottom - top);
    }
}
