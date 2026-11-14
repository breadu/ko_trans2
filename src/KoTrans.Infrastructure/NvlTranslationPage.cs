using System.Collections.Generic;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// Owned by one pipeline, accessed under its sync lock. Only successful, complete
// blocks become immutable. A quiet but incomplete suffix is translated provisionally.
public sealed class NvlTranslationPage
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string FixedSource { get; private set; } = "";
    public string FixedTranslation { get; private set; } = "";
    public Guid? RestoredFrom { get; private set; }
    private string previous = "";
    public void Reset() { Id = Guid.NewGuid(); RestoredFrom = null; FixedSource = FixedTranslation = previous = ""; }
    public string Begin(string source, bool pageBreak)
    {
        RestoredFrom = null;
        // A visual clear can be an effect, not loss of accumulated dialogue. Only
        // reuse committed blocks when ALL previously accepted text returns exactly.
        // Keep a new page ID, but preserve its already translated prefix/line breaks.
        bool restore = pageBreak && FixedSource.Length > 0 && previous.Length > 0
            && source.StartsWith(previous, StringComparison.Ordinal);
        var oldId = Id; var oldSource = FixedSource; var oldTranslation = FixedTranslation;
        if (pageBreak || !source.StartsWith(FixedSource, StringComparison.Ordinal)
            || FixedSource.Length == 0 && previous.Length > 0 && !source.StartsWith(previous, StringComparison.Ordinal)
                && !previous.StartsWith(source, StringComparison.Ordinal)) Reset();
        if (restore) { FixedSource = oldSource; FixedTranslation = oldTranslation; RestoredFrom = oldId; }
        previous = source;
        return source[FixedSource.Length..];
    }
    public static IReadOnlyList<string> Blocks(string remainder)
        => NvlTranslationLayout.Plan(remainder, [], "ja", 0).Select(b => b.Source).ToArray();
    public static bool IsShortAlphanumericTail(string value)
    {
        var tail=value.Trim();
        return tail.Length is >= 1 and <= 4 && tail.All(c=>char.IsAsciiLetterOrDigit(c) || char.IsDigit(c)
            || c is >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ');
    }
    public static bool HasAmbiguousTail(string text)
    {
        int end=NvlText.LastTranslationCompleteEnd(text);
        return end>0 && IsShortAlphanumericTail(text[end..]);
    }
    // Replace the combined translation rather than appending an ambiguous suffix
    // to an already translated prefix. Keep the same page/history identity.
    public void ReopenFixedBlock() { FixedSource=FixedTranslation=""; }
    public string Compose(string active) => string.IsNullOrWhiteSpace(FixedTranslation) ? active.Trim()
        : string.IsNullOrWhiteSpace(active) ? FixedTranslation : FixedTranslation.TrimEnd() + "\n" + active.Trim();
    public void Commit(string source, string translation, bool paragraphBoundary = false)
    {
        if (!paragraphBoundary && !NvlText.TranslationComplete(source)) throw new InvalidOperationException("Only a completed block can be fixed.");
        FixedSource += source;
        FixedTranslation = Compose(translation);
    }
}
