using KoTrans.Core;
using System.Text;
using System.Text.RegularExpressions;

namespace KoTrans.Ocr;

public static class TextProcessing
{
    public static (string Text, float Confidence) DecodeCtc(ReadOnlySpan<float> probabilities, int steps, int classes, IReadOnlyList<string> dictionary)
    {
        if (classes != dictionary.Count + 1 || probabilities.Length != checked(steps * classes)) throw new InvalidDataException("CTC 출력과 문자 사전 크기가 다릅니다.");
        var text = new StringBuilder(); int previous = -1, count = 0; float sum = 0;
        for (int t = 0; t < steps; t++)
        {
            int best = 0; float score = probabilities[t * classes];
            for (int c = 1; c < classes; c++) if (probabilities[t * classes + c] > score) { score = probabilities[t * classes + c]; best = c; }
            if (best != 0 && best != previous) { text.Append(dictionary[best - 1]); sum += score; count++; }
            previous = best;
        }
        return (text.ToString(), count == 0 ? 0 : sum / count);
    }
    public static string Normalize(string text, string language)
    {
        // Never globally substitute kanji/kana or punctuation without visual evidence.
        text = text.Trim().Replace("\r", "");
        return language == "en" ? Regex.Replace(text, "[ \t]+", " ") : text;
    }
    public static double CharacterErrorRate(string expected, string actual)
    {
        var a = expected.EnumerateRunes().ToArray(); var b = actual.EnumerateRunes().ToArray();
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1]; next[0] = i;
            for (int j = 1; j <= b.Length; j++) next[j] = Math.Min(Math.Min(next[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = next;
        }
        return (double)prev[b.Length] / Math.Max(1, a.Length);
    }
}

public static class LayoutProcessor
{
    private static double Overlap(int a, int sizeA, int b, int sizeB) => Math.Max(0, Math.Min(a + sizeA, b + sizeB) - Math.Max(a, b));
    public static bool ShouldJoinDetectionFragments(PixelRect a, PixelRect b)
    {
        bool vertical = a.Height > a.Width * 1.5;
        if (vertical != (b.Height > b.Width * 1.5)) return false;
        double thicknessA = vertical ? a.Width : a.Height, thicknessB = vertical ? b.Width : b.Height;
        if (Math.Min(thicknessA, thicknessB) < Math.Max(thicknessA, thicknessB) * .65) return false;
        double across = vertical ? Overlap(a.X, a.Width, b.X, b.Width) : Overlap(a.Y, a.Height, b.Y, b.Height);
        double along = vertical ? Overlap(a.Y, a.Height, b.Y, b.Height) : Overlap(a.X, a.Width, b.X, b.Width);
        // Unclip can make fragments of one line overlap by part of a glyph. Recrop
        // the union before CTC so that a clipped glyph isn't decoded twice.
        return along > 0 && across >= Math.Max(thicknessA, thicknessB) * .8;
    }
    public static bool IsRuby(PixelRect small, PixelRect body, bool vertical)
    {
        if (vertical) return small.Width < body.Width * .55 && small.X >= body.X + body.Width * .65
            && small.X - body.X - body.Width < body.Width * 1.5 && Overlap(small.Y, small.Height, body.Y, body.Height) > small.Height * .65;
        return small.Height < body.Height * .55 && small.Y + small.Height <= body.Y + body.Height * .3
            && body.Y - small.Y - small.Height < body.Height && Overlap(small.X, small.Width, body.X, body.Width) > small.Width * .65;
    }
    // Supplemental recognition-time evidence for boxes enlarged by DB unclip. Keep the
    // existing detector filter strict: this rule needs recognized text, not just size.
    private static bool IsExpandedHorizontalRuby(OcrLine small, OcrLine body)
    {
        if (small.Vertical || body.Vertical || small.Confidence < .8 || body.Confidence < .8
            || small.Text.Length is < 2 or > 8
            || !small.Text.All(c => c is >= '\u3041' and <= '\u3096' or '\u309D' or '\u309E' or '\u30FC')) return false;
        var s = small.Bounds; var b = body.Bounds;
        if (!s.IsValid || !b.IsValid || s.Height < b.Height * .55 || s.Height > b.Height * .70
            || s.Y + s.Height < b.Y - b.Height * .1 || s.Y + s.Height > b.Y + b.Height * .2
            || s.X < b.X || s.X + s.Width > b.X + b.Width || s.Width > b.Width * .3) return false;

        // Japanese with optional typographic double quotes. These narrow quotes
        // count as half a glyph; treating them as full-width shifts the kanji anchor.
        // Other Latin/mixed-width text still cannot be projected safely.
        var glyphs = body.Text.EnumerateRunes().ToArray();
        static bool Han(int c) => c is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F;
        static bool NarrowQuote(int c) => c is 0x201C or 0x201D;
        if (glyphs.Length is < 6 or > 160 || glyphs.Any(c => !Han(c.Value)
            && !NarrowQuote(c.Value) && c.Value is not (>= 0x3041 and <= 0x3096 or >= 0x30A1 and <= 0x30FA or 0x30FC or 0x3001 or 0x3002 or 0x300C or 0x300D))) return false;
        var offsets = new double[glyphs.Length+1];
        for (int i = 0; i < glyphs.Length; i++) offsets[i+1] = offsets[i] + (NarrowQuote(glyphs[i].Value) ? .5 : 1);
        double advance = b.Width / offsets[^1];
        if (advance < b.Height * .7 || advance > b.Height * 1.4
            || s.Width / (double)small.Text.Length > advance * .65) return false;
        // Require a multi-kanji anchor directly underneath; do not erase kana-only
        // headings/names, isolated readings, or a small independent sentence.
        for (int start = 0; start < glyphs.Length; start++) {
            if (!Han(glyphs[start].Value)) continue;
            int end = start + 1;
            while (end < glyphs.Length && Han(glyphs[end].Value)) end++;
            double left = b.X + offsets[start] * advance, right = b.X + offsets[end] * advance;
            double center = s.X + s.Width / 2d;
            if (end - start >= 2 && center >= left && center <= right
                && Math.Min(s.X + s.Width, right) - Math.Max(s.X, left) >= s.Width * .65) return true;
            start = end - 1;
        }
        return false;
    }
    public static IReadOnlyList<OcrLine> Arrange(IReadOnlyList<OcrLine> input, OcrOptions options, int imageWidth, int imageHeight)
    {
        var lines = input.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (options.Language == "ja" && options.SuppressFurigana)
            lines = lines.Where(l => !lines.Any(other => other != l
                && (IsRuby(l.Bounds, other.Bounds, other.Vertical) || IsExpandedHorizontalRuby(l, other)))).ToList();
        var groups = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
        {
            var matches = groups.Where(g => g.Any(b => Neighbors(line, b, options))).ToArray();
            if (matches.Length == 0) groups.Add([line]);
            else { matches[0].Add(line); foreach (var other in matches.Skip(1)) { matches[0].AddRange(other); groups.Remove(other); } }
        }
        if (options.ReadingMode == "ADV" && groups.Count > 1)
        {
            double Score(List<OcrLine> g)
            {
                double x = g.Average(l => l.Bounds.X + l.Bounds.Width / 2d) / imageWidth;
                return g.Sum(l => Math.Sqrt(l.Bounds.Width * (double)l.Bounds.Height) * l.Confidence)
                    * (1 - .5 * Math.Abs(2 * x - 1));
            }
            groups = [groups.MaxBy(Score)!];
        }
        groups = options.Vertical ? groups.OrderByDescending(g => g.Max(l => l.Bounds.X)).ToList() : groups.OrderBy(g => g.Min(l => l.Bounds.Y)).ThenBy(g => g.Min(l => l.Bounds.X)).ToList();
        var output = new List<OcrLine>();
        for (int paragraph = 0; paragraph < groups.Count; paragraph++)
        {
            var remaining = groups[paragraph].ToList();
            while (remaining.Count > 0)
            {
                var anchor = options.Vertical ? remaining.MaxBy(l => l.Bounds.X)! : remaining.MinBy(l => l.Bounds.Y)!;
                var row = remaining.Where(l => l.Vertical == anchor.Vertical && (anchor.Vertical
                    ? Overlap(l.Bounds.X, l.Bounds.Width, anchor.Bounds.X, anchor.Bounds.Width) > Math.Min(l.Bounds.Width, anchor.Bounds.Width) * .5
                    : Overlap(l.Bounds.Y, l.Bounds.Height, anchor.Bounds.Y, anchor.Bounds.Height) > Math.Min(l.Bounds.Height, anchor.Bounds.Height) * .5)).ToList();
                var ordered = row.OrderBy(l => anchor.Vertical ? l.Bounds.Y : l.Bounds.X).ToArray();
                int left = row.Min(l => l.Bounds.X), top = row.Min(l => l.Bounds.Y);
                var bounds = new PixelRect(left, top, row.Max(l => l.Bounds.X + l.Bounds.Width) - left, row.Max(l => l.Bounds.Y + l.Bounds.Height) - top);
                // Join fragments only within the same physical row/column. Never insert kana or punctuation.
                output.Add(new(string.Join(options.Language == "en" ? " " : "", ordered.Select(l => l.Text)),
                    row.Average(l => l.Confidence), bounds, anchor.Vertical, paragraph));
                foreach (var item in row) remaining.Remove(item);
            }
        }
        return output;
    }
    private static bool Neighbors(OcrLine a, OcrLine b, OcrOptions options)
    {
        if (a.Vertical != b.Vertical) return false;
        var x = a.Bounds; var y = b.Bounds;
        double scale = Math.Max(a.Vertical ? x.Width : x.Height, b.Vertical ? y.Width : y.Height);
        double horizontalGap = Math.Max(0, Math.Max(x.X - y.X - y.Width, y.X - x.X - x.Width));
        double verticalGap = Math.Max(0, Math.Max(x.Y - y.Y - y.Height, y.Y - x.Y - x.Height));
        if (!a.Vertical && options.Language == "ja" && options.ReadingMode == "ADV" && !options.Vertical
            && Overlap(x.Y, x.Height, y.Y, y.Height) <= Math.Min(x.Height, y.Height) * .6
            && (DetachedLabel(a, b) || DetachedLabel(b, a))) return false;
        return a.Vertical ? horizontalGap < scale * 1.8 && Overlap(x.Y, x.Height, y.Y, y.Height) > Math.Min(x.Height, y.Height) * .2
            : (verticalGap < scale * 1.4 && Overlap(x.X, x.Width, y.X, y.Width) > Math.Min(x.Width, y.Width) * .2)
                || (horizontalGap < scale * 1.5 && Overlap(x.Y, x.Height, y.Y, y.Height) > Math.Min(x.Height, y.Height) * .6);
    }
    // A tall/slanted decoration must not use its own oversized box to bridge
    // into a normal-size dialogue paragraph. Inline and same-size Latin stay.
    private static bool DetachedLabel(OcrLine label, OcrLine body) => JapaneseAdvRecovery.ShortLatin(label)
        && JapaneseAdvRecovery.ReliableBody(body) && label.Bounds.Height > body.Bounds.Height * 1.4;
}
