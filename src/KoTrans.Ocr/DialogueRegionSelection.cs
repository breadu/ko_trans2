using KoTrans.Core;

namespace KoTrans.Ocr;

// Select the paragraph before reading characters. The anchor stays fixed:
// accepting one neighbouring row must not pull distant labels into the crop.
internal static class DialogueRegionSelection
{
    internal static bool Supported(OcrOptions options) => options.EngineId == "paddle"
        && options.ModelPresetId is ("default" or "v6-medium-full" or "v5-det-v6-medium-rec")
        && options.Language == "ja" && options.ReadingMode == "ADV" && !options.Vertical
        && options.SuppressFurigana && !options.KeepLocalParagraph;

    internal static IReadOnlyList<T> Select<T>(IReadOnlyList<T> input, Func<T, PixelRect> bounds,
        Func<PixelRect, PixelRect, bool>? historySupports = null)
    {
        var rows = Rows(input.Where(item => bounds(item).IsValid).ToArray(), bounds);
        if (rows.Count == 0) return [];
        // Compare line length in units of character height. A large background
        // patch must not win merely because it occupies more screen pixels.
        var anchorRow = rows.MaxBy(row => { var box = Union(row.Select(bounds)); return (double)box.Width / box.Height; })!;
        var anchor = Union(anchorRow.Select(bounds));
        // Wrapped rows also establish the paragraph's start. A detached piece
        // to their left must not shift that start and discard the continuation.
        var neighbours = rows.Where(row => !ReferenceEquals(row, anchorRow)).Select(row => Union(row.Select(bounds)))
            .Where(box => box.Height >= anchor.Height * .65 && box.Height <= anchor.Height * 1.5
                && SeparateRows(box, anchor) && GapY(box, anchor) <= anchor.Height * 1.5).ToArray();
        int alignedStart = anchorRow.Select(bounds).Where(box => neighbours.Any(other => Math.Abs(box.X - other.X) <= anchor.Height))
            .Select(box => box.X).DefaultIfEmpty(anchor.X).Min();
        if (alignedStart - anchor.X > anchor.Height) {
            var aligned = anchorRow.Where(item => bounds(item).X >= alignedStart - anchor.Height).ToList();
            rows[rows.IndexOf(anchorRow)] = aligned; anchorRow = aligned; anchor = Union(aligned.Select(bounds));
        }
        var selected = new List<List<T>> { anchorRow };
        foreach (var row in rows.OrderBy(row => Math.Abs(Union(row.Select(bounds)).Y - anchor.Y))) {
            if (ReferenceEquals(row, anchorRow)) continue;
            var box = Union(row.Select(bounds));
            if (box.Height < anchor.Height * .65 || box.Height > anchor.Height * 1.5) continue;
            // A continuation needs its own baseline, not a side fragment
            // overlapping the space between two already selected lines.
            if (selected.Any(other => !SeparateRows(box, Union(other.Select(bounds))))) continue;
            if (selected.Any(other => GapY(box, Union(other.Select(bounds))) <= anchor.Height * 1.5)
                && (SharesParagraphColumn(box, anchor) || historySupports?.Invoke(box, anchor) == true))
                selected.Add(row);
        }
        return selected.SelectMany(row => row).OrderBy(item => bounds(item).Y).ThenBy(item => bounds(item).X).ToArray();
    }

    internal static IReadOnlyList<OcrLine> Arrange(IReadOnlyList<OcrLine> lines)
        => Rows(lines, line => line.Bounds).OrderBy(row => row.Min(line => line.Bounds.Y))
            .Select(row => new OcrLine(string.Concat(row.OrderBy(line => line.Bounds.X).Select(line => line.Text)),
                row.Average(line => line.Confidence), Union(row.Select(line => line.Bounds)))).ToArray();

    // Old geometry cannot certify today's text: the current anchor must also
    // have been read confidently without the supplementary history.
    internal static void ConfirmHistoryRows(List<OcrLine> lines, IReadOnlySet<OcrLine> historyLines, PixelRect? anchor)
    {
        if (anchor is { } bounds && !lines.Any(l => !historyLines.Contains(l) && l.Confidence >= .85f
            && Math.Abs(l.Bounds.Y + l.Bounds.Height / 2d - bounds.Y - bounds.Height / 2d) <= bounds.Height * .25))
            lines.RemoveAll(historyLines.Contains);
    }

    // A few pixels protect an edge glyph without widening the vertical band.
    // Do not cross the midpoint to another piece of the same physical row.
    internal static PixelRect WithEndMargin(PixelRect box, IReadOnlyList<PixelRect> others, int width)
    {
        int margin = Math.Clamp((int)Math.Ceiling(box.Height / 8d), 2, 6);
        int left = Math.Min(margin, box.X), right = Math.Min(margin, width - box.X - box.Width);
        foreach (var other in others) {
            if (other == box || Math.Min(box.Y + box.Height, other.Y + other.Height)
                - Math.Max(box.Y, other.Y) <= Math.Min(box.Height, other.Height) * .5) continue;
            if (other.X + other.Width <= box.X) left = Math.Min(left, (box.X - other.X - other.Width) / 2);
            if (other.X >= box.X + box.Width) right = Math.Min(right, (other.X - box.X - box.Width) / 2);
        }
        return box with { X = box.X - left, Width = box.Width + left + right };
    }

    private static List<List<T>> Rows<T>(IReadOnlyList<T> input, Func<T, PixelRect> bounds)
    {
        var rows = new List<List<T>>();
        foreach (var item in input.OrderBy(item => bounds(item).Y).ThenBy(item => bounds(item).X)) {
            var box = bounds(item);
            var matches = rows.Where(row => row.Any(other => SameRow(box, bounds(other)))).ToArray();
            var merged = new List<T> { item };
            foreach (var row in matches) { merged.AddRange(row); rows.Remove(row); }
            rows.Add(merged);
        }
        return rows;
    }

    private static bool SameRow(PixelRect a, PixelRect b)
    {
        double height = Math.Max(a.Height, b.Height);
        return height <= Math.Min(a.Height, b.Height) * 1.6
            // Partial vertical overlap is not a shared text baseline. Otherwise
            // a displaced side label moves the row's left edge and makes the
            // following, correctly aligned dialogue row look unrelated.
            && Math.Abs(a.Y + a.Height / 2d - b.Y - b.Height / 2d) < height * .125
            && Math.Max(0, Math.Max(a.X - b.X - b.Width, b.X - a.X - a.Width)) <= height;
    }

    private static bool SeparateRows(PixelRect a, PixelRect b)
        => Math.Abs(a.Y + a.Height / 2d - b.Y - b.Height / 2d) >= Math.Max(a.Height, b.Height) * .75;

    private static bool SharesParagraphColumn(PixelRect row, PixelRect anchor)
    {
        int startOffset = Math.Abs(row.X - anchor.X);
        if (startOffset <= anchor.Height) return true;
        // The anchor may start late when detection misses opening marks. A
        // continuation can establish a slightly earlier paragraph start if at
        // least half its horizontal span overlaps. Keep the original inward
        // limit: small background labels below the text must not enter the block.
        int overlap = Math.Min(row.X + row.Width, anchor.X + anchor.Width) - Math.Max(row.X, anchor.X);
        return row.X < anchor.X && startOffset <= anchor.Height * 2
            && overlap >= Math.Min(row.Width, anchor.Width) * .5;
    }

    private static double GapY(PixelRect a, PixelRect b)
        => Math.Max(0, Math.Max(a.Y - b.Y - b.Height, b.Y - a.Y - a.Height));

    private static PixelRect Union(IEnumerable<PixelRect> source)
    {
        var boxes = source.ToArray();
        int x = boxes.Min(box => box.X), y = boxes.Min(box => box.Y);
        return new(x, y, boxes.Max(box => box.X + box.Width) - x, boxes.Max(box => box.Y + box.Height) - y);
    }
}
