using System.Text.RegularExpressions;

namespace KoTrans.Core;

public sealed record CharacterNameHint(string SourceName, string KoreanName);

// Resolve only names present in this source. Never generate aliases by splitting
// unspaced Japanese names at an assumed character count, or use translated history.
public static class CharacterNameHints
{
    private static readonly Regex HanWords = new("[\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFAFF々]+", RegexOptions.CultureInvariant);
    private static readonly string[] Honorifics = ["先生", "様", "君", "氏"];

    public static IReadOnlyList<CharacterNameHint> Resolve(string source, IReadOnlyList<CharacterEntry>? characters)
    {
        if (characters is not { Count: > 0 } || string.IsNullOrEmpty(source)) return [];
        var groups = characters.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name.Trim(), StringComparer.Ordinal).OrderByDescending(g => g.Key.Length).ToArray();
        var occupied = new bool[source.Length];
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            bool found = false;
            foreach (int at in Occurrences(source, group.Key)) {
                if (occupied.AsSpan(at, group.Key.Length).Contains(true)) continue;
                occupied.AsSpan(at, group.Key.Length).Fill(true); found = true;
            }
            if (!found) continue;
            var values = group.Select(c => c.KoreanName.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length != 1 || values[0].Length == 0)
                throw new TranslationException("캐릭터 사전의 같은 이름에는 하나의 한국어 표기를 지정해 주세요.");
            hints[group.Key] = values[0];
        }

        // A whole Han word in the current sentence identifies a written prefix or
        // suffix. Paired Korean words supply its spelling; middle fragments and
        // ambiguous mappings are not inferred. A single Han character needs an
        // attached honorific or explicit speaker separator to count as a name.
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var han = HanWords.Matches(source).Cast<Match>().Select(m => (Text: m.Value, At: m.Index)).ToList();
        foreach (var match in han.ToArray()) foreach (string suffix in Honorifics)
            if (match.Text.EndsWith(suffix, StringComparison.Ordinal) && match.Text.Length - suffix.Length >= 1)
                han.Add((match.Text[..^suffix.Length], match.At));

        void Offer(string part, string korean, IEnumerable<int> positions)
        {
            if (part.Length == 0 || hints.ContainsKey(part) || !positions.Any(at => !occupied.AsSpan(at, part.Length).Contains(true)
                && (part.Length >= 2 || IsHan(part[0]) && HasNameSuffix(source, at + part.Length)))) return;
            if (!candidates.TryGetValue(part, out var values)) candidates[part] = values = new(StringComparer.Ordinal);
            values.Add(korean);
        }
        foreach (var entry in characters)
        {
            string name = entry.Name.Trim();
            var korean = Words(entry.KoreanName);
            if (korean.Length != 2) continue;
            var parts = Words(name);
            if (parts.Length == 2) {
                foreach (int i in new[] { 0, 1 })
                    Offer(parts[i], korean[i], parts[i].All(IsHan) ? han.Where(p => p.Text == parts[i]).Select(p => p.At) : Occurrences(source, parts[i]));
            }
            else if (parts.Length == 1 && name.Length >= 3 && name.All(IsHan)) {
                foreach (var part in han.Where(p => p.Text.Length < name.Length)) {
                    if (name.StartsWith(part.Text, StringComparison.Ordinal)) Offer(part.Text, korean[0], [part.At]);
                    if (name.EndsWith(part.Text, StringComparison.Ordinal)) Offer(part.Text, korean[1], [part.At]);
                }
            }
        }
        foreach (var pair in candidates.Where(p => p.Value.Count == 1)) hints.Add(pair.Key, pair.Value.Single());
        return hints.Select(p => new CharacterNameHint(p.Key, p.Value)).ToArray();
    }

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool HasNameSuffix(string source, int end) => new[] { "さん", "さま", "ちゃん", "くん", "先生", "様", "君", "氏", "：", ":" }
        .Any(suffix => source.AsSpan(end).StartsWith(suffix, StringComparison.Ordinal));
    private static bool IsHan(char c) => c is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or '々';
    private static bool IsLatinWord(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    private static IEnumerable<int> Occurrences(string source, string name)
    {
        if (name.Length == 0) yield break;
        for (int start = 0; start <= source.Length - name.Length;) {
            int at = source.IndexOf(name, start, StringComparison.Ordinal);
            if (at < 0) yield break;
            start = at + name.Length;
            if ((at > 0 && IsLatinWord(name[0]) && IsLatinWord(source[at - 1])) ||
                (start < source.Length && IsLatinWord(name[^1]) && IsLatinWord(source[start]))) continue;
            yield return at;
        }
    }
}
