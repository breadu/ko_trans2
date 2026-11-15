using System.Text;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// Correct model-generated newline notation after HTTP JSON decoding, never unescape JSON twice.
// Ambiguous literal/code/path text is preserved even if that leaves an occasional marker visible.
internal static class OpenAiTranslationText
{
    internal static string Normalize(string text, TranslationRequest request, bool complete)
    {
        if (request.OutputSchema is not null || request.Text.Contains('\\') || IsCodeLike(request.Text)
            || !text.Contains('\\') || IsCodeLike(text)) return text;

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            if (text[i] != '\\') { result.Append(text[i++]); continue; }
            int end = i + 1;
            while (end < text.Length && text[end] == '\\') end++;
            // Doubled backslashes are literal notation (including UNC paths).
            if (end - i > 1) { result.Append(text, i, end - i); i = end; continue; }

            var tail = text.AsSpan(i);
            int length = tail.StartsWith(@"\r\n", StringComparison.Ordinal) ? 4
                : tail.StartsWith(@"\n", StringComparison.Ordinal) ? 2 : 0;
            // Wait for the next delta before displaying a possible split escape or deciding its boundary.
            if (!complete && (tail.SequenceEqual(@"\") || tail.SequenceEqual(@"\r")
                || tail.SequenceEqual(@"\r\") || length > 0 && i + length == text.Length)) break;
            if (length == 0) { result.Append(text[i++]); continue; }

            char before = result.Length > 0 ? result[result.Length - 1] : '\0';
            char after = i + length < text.Length ? text[i + length] : '\0';
            // Identifiers/path segments and a quoted standalone marker are ambiguous, not prose breaks.
            bool literal = char.IsAsciiLetterOrDigit(before) || before is '_' or ':'
                || char.IsAsciiLetterOrDigit(after) || after == '_'
                || IsQuote(before) && IsQuote(after);
            if (literal) result.Append(text, i, length);
            else result.Append('\n');
            i += length;
        }
        return result.ToString();
    }

    private static bool IsCodeLike(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return text.Contains('`') || !trimmed.IsEmpty && trimmed[0] is '{' or '[';
    }
    private static bool IsQuote(char c) => c is '\'' or '"' or '“' or '”' or '‘' or '’' or '「' or '」' or '『' or '』';
}
