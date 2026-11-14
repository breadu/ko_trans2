using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KoTrans.Infrastructure;

// Keep transport formatting separate from the canonical source used by prefix matching and caches.
internal static class NvlTranslationBatch
{
    internal static string Source(IReadOnlyList<NvlTranslationBlock> blocks)
        => string.Join("\n\n", blocks.Select(b => b.Source.Trim()));

    internal static string[]? Split(string translation, int count)
    {
        string normalized = translation.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (count == 1) return [normalized];
        var paragraphs = Regex.Split(normalized, @"\n[\t ]*\n(?:[\t ]*\n)*");
        if (paragraphs.Length == 1) paragraphs = normalized.Split('\n');
        // Never guess by character length/punctuation or drop text to repair malformed model output.
        return paragraphs.Length == count && paragraphs.All(p => !string.IsNullOrWhiteSpace(p))
            ? paragraphs.Select(p => p.Trim()).ToArray() : null;
    }
}
