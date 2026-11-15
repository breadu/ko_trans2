using KoTrans.Core;

namespace KoTrans.Ocr;

// One translation session, at most four rectangles, no text or image retention.
public sealed class DialogueLayoutHistory(Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private DialogueLayoutHint? hint;
    private string preset = "";
    private long observedAt;
    public void Reset() { hint = null; preset = ""; observedAt = 0; }
    public DialogueLayoutHint? Get(ImageFrame frame, OcrOptions options, bool freshRead = false)
    {
        if (!DialogueRegionSelection.Supported(options) || hint is not null && (hint.Width != frame.Width
            || hint.Height != frame.Height || preset != options.ModelPresetId || now() - observedAt > 15000)) Reset();
        return freshRead || options.SearchArea is not null || options.BypassOcrCaches ? null : hint;
    }
    public void Observe(ImageFrame frame, OcrResult result, OcrOptions options, bool fullSearch)
    {
        _ = Get(frame, options);
        if (!fullSearch || !DialogueRegionSelection.Supported(options)) return;
        if (result.Lines.Count == 0) { Reset(); return; }
        var anchor = result.Lines.MaxBy(l => (double)l.Bounds.Width / Math.Max(1, l.Bounds.Height))!.Bounds;
        if (hint is not null && !hint.MatchesAnchor(anchor)) Reset();
        // A recovered/cached observation must never renew its own supporting history.
        if (result.DialogueHintUsed || result.ReusedFromCache) return;
        var rows = result.Lines.Where(l => !l.Vertical && l.Confidence >= .9f && !string.IsNullOrWhiteSpace(l.Text)
            && l.Bounds.Width >= l.Bounds.Height * 2).Select(l => l.Bounds).OrderBy(b => b.Y).ToArray();
        if (rows.Length is < 2 or > 4) return;
        for (int i = 1; i < rows.Length; i++) {
            var a = rows[i - 1]; var b = rows[i];
            if (b.Height < a.Height * .8 || b.Height > a.Height * 1.25 || Math.Abs(b.X - a.X) > a.Height * 2
                || b.Y - a.Y < a.Height || b.Y - a.Y > a.Height * 2.5) return;
        }
        var next = new DialogueLayoutHint(frame.Width, frame.Height, rows);
        if (!DialogueLayoutHint.Valid(next)) return;
        hint = next; preset = options.ModelPresetId; observedAt = now();
    }
}
