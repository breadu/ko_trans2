namespace KoTrans.Core;

// Geometry only, attached to the exact OCR request for CPU/GPU and QA replay.
// Never a source of text and never persisted in user profiles.
public sealed record DialogueLayoutHint(int Width, int Height, IReadOnlyList<PixelRect> Rows)
{
    public static bool Valid(DialogueLayoutHint? hint) => hint is null || hint.Width is > 0 and <= 32768
        && hint.Height is > 0 and <= 32768 && hint.Rows is { Count: >= 2 and <= 4 }
        && hint.Rows.All(b => b.IsValid && b.X >= 0 && b.Y >= 0
            && (long)b.X + b.Width <= hint.Width && (long)b.Y + b.Height <= hint.Height);

    public bool Supports(PixelRect row, PixelRect anchor)
    {
        if (!Valid(this) || Math.Abs(row.X - anchor.X) > anchor.Height * 3 || !Overlaps(row, anchor)) return false;
        for (int i = 0; i < Rows.Count; i++) {
            var priorAnchor = Rows[i];
            if (!NearBaseline(anchor, priorAnchor) || Math.Abs(anchor.X - priorAnchor.X) > priorAnchor.Height * 2
                || !Overlaps(anchor, priorAnchor)) continue;
            for (int j = 0; j < Rows.Count; j++) if (j != i) {
                var priorRow = Rows[j];
                if (NearBaseline(row, priorRow) && Math.Abs(row.X - priorRow.X) <= priorRow.Height * .5
                    && Math.Abs((Center(row) - Center(anchor)) - (Center(priorRow) - Center(priorAnchor))) <= priorAnchor.Height * .5)
                    return true;
            }
        }
        return false;
    }
    public bool MatchesAnchor(PixelRect current) => Rows.Any(prior => NearBaseline(current, prior)
        && Math.Abs(current.X - prior.X) <= prior.Height * 2 && Overlaps(current, prior));
    private static double Center(PixelRect b) => b.Y + b.Height / 2d;
    private static bool NearBaseline(PixelRect current, PixelRect prior) => current.Height >= prior.Height * .8
        && current.Height <= prior.Height * 1.25 && Math.Abs(Center(current) - Center(prior)) <= prior.Height * .35;
    private static bool Overlaps(PixelRect a, PixelRect b) => Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)
        >= Math.Min(a.Width, b.Width) * .5;
}
