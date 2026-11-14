using System.Text;
using System.Collections.Generic;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

internal sealed record NvlTranslationBlock(string Source, bool Complete);

// Presentation only. Never changes OCR rows, their reuse policy or the canonical page identity.
internal static class NvlTranslationLayout
{
    internal static IReadOnlyList<NvlTranslationBlock> Plan(string text, IReadOnlyList<OcrLine> lines, string language, int fixedLength)
    {
        var endings = NvlText.TranslationEnds(text);
        var breaks = new SortedSet<int>();
        var rows = NvlText.MergeVisualRows(lines, language).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToArray();
        string canonical = NvlText.Canonical(string.Join("\n", rows.Select(l => l.Text)), language);
        bool reliableRows = rows.Length > 0 && !rows.Any(l => l.Vertical || !l.Bounds.IsValid) && canonical == text;
        if (reliableRows) {
            double height = Median(rows.Select(l => (double)l.Bounds.Height));
            double left = rows.Min(l => l.Bounds.X), right = rows.Max(l => l.Bounds.X + (double)l.Bounds.Width);
            var gaps = rows.Zip(rows.Skip(1), (a, b) => Math.Max(0d, b.Bounds.Y - (a.Bounds.Y + (double)a.Bounds.Height))).Order().ToArray();
            double normalGap = gaps.Length > 1 ? Median(gaps.Take(Math.Max(1, gaps.Length / 2))) : 0;
            var prefix = new StringBuilder();
            for (int i = 0; i < rows.Length - 1; i++) {
                if (i > 0) prefix.Append('\n');
                prefix.Append(rows[i].Text);
                int offset = NvlText.Canonical(prefix.ToString(), language).Length;
                var previous = rows[i].Bounds; var next = rows[i+1].Bounds;
                double gap = next.Y - (previous.Y + (double)previous.Height);
                bool separated = gap > Math.Max(height * 1.75, normalGap * 1.8);
                bool shortLine = right - (previous.X + (double)previous.Width) > Math.Max(height * 2, (right-left) * .12);
                bool atLeft = Math.Abs(next.X - left) <= height * .75;
                bool indented = next.X - left >= height * .8 && next.X - previous.X >= height * .6;
                // A normal row at the right margin is wrapping, even if punctuation happens to end it.
                // Paragraph IDs alone are not evidence: detector regrouping can change them every frame.
                if (separated || shortLine && (indented || atLeft && endings.Contains(offset))) breaks.Add(offset);
            }
        }
        foreach (int end in endings) {
            if (end >= text.Length) continue;
            var next = text.AsSpan(end).TrimStart();
            // Closed standalone dialogue and a following dialogue are logical blocks even on the same physical row.
            if (!reliableRows || "」』]］】〉》”’\"'".Contains(text[end-1])
                || !next.IsEmpty && "「『[［【〈《“‘\"".Contains(next[0])) breaks.Add(end);
        }
        // Preserve the existing completed-prefix / provisional-suffix behavior.
        if (endings.LastOrDefault() is var completeEnd && completeEnd > fixedLength && completeEnd < text.Length) breaks.Add(completeEnd);
        breaks.RemoveWhere(i => i <= fixedLength || i >= text.Length);
        // Never send a tiny Latin/numeric marker-like tail as a standalone translation request.
        while (breaks.Count > 0 && NvlTranslationPage.IsShortAlphanumericTail(text[breaks.Max..])) breaks.Remove(breaks.Max);
        var blocks = new List<NvlTranslationBlock>();
        int start = fixedLength;
        foreach (int end in breaks.Append(text.Length)) {
            string source = text[start..end]; start = end;
            if (string.IsNullOrWhiteSpace(source)) continue;
            blocks.Add(new(source, end < text.Length || NvlText.TranslationComplete(source)));
        }
        return blocks;
    }
    private static double Median(IEnumerable<double> values)
    { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2]; }
}
