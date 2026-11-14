using System.Text;
using System.Text.RegularExpressions;

namespace KoTrans.Core;

public static class NvlText
{
    // Presentation follows visual rows, independent of the canonical string used
    // for page-prefix matching and API deduplication. Nearby baselines tolerate
    // detector jitter; separate boxes on the same row do not create extra lines.
    public static string DisplayLines(IReadOnlyList<OcrLine> lines)
        => string.Join("\n", VisualRows(lines).Select(row => string.Join(" ", row.OrderBy(l => l.Bounds.X)
            .Select(l => Regex.Replace(l.Text, @"\s+", " ").Trim()))));

    // NVL is a physical page. Paragraph/gap guesses must not put a slightly
    // taller right-hand fragment before the left-hand fragment of the same row.
    public static IReadOnlyList<OcrLine> MergeVisualRows(IReadOnlyList<OcrLine> lines,string language)
    {
        if(lines.Any(l=>l.Vertical)) return lines;
        return VisualRows(lines).Select(row => {
            var ordered=row.OrderBy(l=>l.Bounds.X).ToArray();
            int x=row.Min(l=>l.Bounds.X),y=row.Min(l=>l.Bounds.Y);
            return new OcrLine(string.Join(language=="en"?" ":"",ordered.Select(l=>l.Text)),row.Average(l=>l.Confidence),
                new(x,y,row.Max(l=>l.Bounds.X+l.Bounds.Width)-x,row.Max(l=>l.Bounds.Y+l.Bounds.Height)-y),false,0);
        }).ToArray();
    }

    private static IReadOnlyList<List<OcrLine>> VisualRows(IReadOnlyList<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.Bounds.Y + l.Bounds.Height / 2d).ThenBy(l => l.Bounds.X)) {
            var row = rows.LastOrDefault();
            var anchor = row?.First();
            if (anchor is null || Math.Abs(line.Bounds.Y + line.Bounds.Height / 2d - anchor.Bounds.Y - anchor.Bounds.Height / 2d)
                > Math.Max(2, Math.Min(line.Bounds.Height, anchor.Bounds.Height) / 2d)
                || line.Bounds.Y >= anchor.Bounds.Y + anchor.Bounds.Height || anchor.Bounds.Y >= line.Bounds.Y + line.Bounds.Height) {
                row = []; rows.Add(row);
            }
            row!.Add(line);
        }
        return rows;
    }
    public static string Canonical(string text, string language)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        if (language != "ja") return Regex.Replace(normalized, @"\s+", " ").Trim();
        // Ignore Japanese layout wrapping without joining embedded Latin words/names.
        return Regex.Replace(normalized, @"\s+", match => match.Index > 0 && match.Index+match.Length < normalized.Length
            && char.IsAscii(normalized[match.Index-1]) && char.IsAscii(normalized[match.Index+match.Length]) ? " " : "").Trim();
    }
    public static int LastCompleteEnd(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--) {
            if (text[i] is not ('.' or '。')) continue;
            int next = i+1; while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next < text.Length && "」』\"”’".Contains(text[next])) continue;
            if (text[i] == '.' && !IsSentencePeriod(text, i)) continue;
            return i+1;
        }
        return 0;
    }
    public static bool Complete(string text) { text = text.TrimEnd(); return text.Length > 0 && LastCompleteEnd(text) == text.Length; }

    // Translation blocks have broader endings than the conservative pixel/ROI reuse rule above.
    // Keep complete dialogue delimiters together; an inner period or quote cannot freeze an open dialogue.
    public static int LastTranslationCompleteEnd(string text) => TranslationEnds(text).LastOrDefault();
    public static IReadOnlyList<int> TranslationEnds(string text)
    {
        var ends = new List<int>();
        var closing = new Stack<char>();
        int end = 0, opening = -1;
        bool dialogue = false;
        void Accept(int position) {
            // A run such as ?! or a trailing closer belongs to the same ending.
            if (ends.Count > 0 && string.IsNullOrWhiteSpace(text[end..position])) ends[^1] = position+1;
            else ends.Add(position+1);
            end = position+1;
        }
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c is '\'' or '’' && i > 0 && i+1 < text.Length
                && char.IsAsciiLetterOrDigit(text[i-1]) && char.IsAsciiLetterOrDigit(text[i+1])) continue;
            if (closing.TryPeek(out var expected) && c == expected) {
                closing.Pop();
                if (closing.Count == 0 && dialogue && !string.IsNullOrWhiteSpace(text[(opening+1)..i])) Accept(i);
                continue;
            }
            char close = c switch {
                '「' => '」', '『' => '』', '[' => ']', '［' => '］', '【' => '】',
                '〈' => '〉', '《' => '》', '“' => '”', '‘' => '’', '"' => '"', '\'' => '\'', _ => '\0'
            };
            if (close != '\0') {
                if (closing.Count == 0) {
                    opening = i;
                    // Inline quotations such as He called it "hope" are still part of the surrounding sentence.
                    dialogue = string.IsNullOrWhiteSpace(text[end..i]);
                }
                closing.Push(close); continue;
            }
            if (closing.Count != 0) continue;
            if (c == '。' || c == '.' && IsSentencePeriod(text, i)
                || c is '！' or '？' || c is '!' or '?' && !(i > 0 && i+1 < text.Length
                    && char.IsAsciiLetterOrDigit(text[i-1]) && char.IsAsciiLetterOrDigit(text[i+1]))) Accept(i);
            // OCR may omit an opening quote. Include trailing closers after an already complete sentence.
            else if (end > 0 && "」』]］】〉》”’".Contains(c) && string.IsNullOrWhiteSpace(text[end..i])) Accept(i);
        }
        return ends;
    }
    public static bool TranslationComplete(string text)
    { text = text.TrimEnd(); return text.Length > 0 && LastTranslationCompleteEnd(text) == text.Length; }

    private static bool IsSentencePeriod(string text, int i)
    {
        if (i > 0 && text[i-1] == '.' || i+1 < text.Length && text[i+1] == '.') return false;
        if (i > 0 && i+1 < text.Length && char.IsDigit(text[i-1]) && char.IsDigit(text[i+1])) return false;
        if (i > 0 && char.IsAsciiLetter(text[i-1]) && (i+2 < text.Length && char.IsAsciiLetter(text[i+1]) && text[i+2] == '.'
            || i > 2 && text[i-2] == '.' && char.IsAsciiLetter(text[i-3]))) return false;
        if (i > 0 && i+1 < text.Length && char.IsAsciiLetter(text[i-1]) && char.IsAsciiLetter(text[i+1])) return false;
        int start = i; while (start > 0 && (char.IsAsciiLetter(text[start-1]) || text[start-1] == '.')) start--;
        return text[start..(i+1)].ToLowerInvariant() is not ("mr." or "mrs." or "ms." or "dr." or "prof." or "e.g." or "i.e." or "vs." or "etc." or "no.");
    }
}
