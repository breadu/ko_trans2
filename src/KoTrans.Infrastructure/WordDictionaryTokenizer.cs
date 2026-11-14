using System.Collections.Generic;
using System.Text.RegularExpressions;
using KoTrans.Core;
using NMeCab;
using NMeCab.Specialized;

namespace KoTrans.Infrastructure;

// Word boundaries and conjugation joining adapted from MovieMalhae, with no project dependency.
public sealed class WordDictionaryTokenizer : IDisposable
{
    private static readonly Regex English = new(@"(?<![\p{L}\p{N}\p{M}])[A-Za-z]+(?:['’\-‑][A-Za-z]+)*(?![\p{L}\p{N}\p{M}])", RegexOptions.CultureInvariant);
    private readonly object sync = new();
    private MeCabIpaDicTagger? tagger;
    private bool disposed;
    public IReadOnlyList<DictionaryWord> Tokenize(string text, string language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (text.Length > 16000) throw new InvalidDataException("단어 사전은 16,000자 이하의 원문을 지원합니다.");
        if (language == "en") return English.Matches(text).Where(m => m.Length <= 160)
            .Select(m => new DictionaryWord(m.Index, m.Length, m.Value, m.Value.Replace('’', '\''))).ToArray();
        if (language != "ja") return [];
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            tagger ??= MeCabIpaDicTagger.Create(Path.Combine(AppContext.BaseDirectory, "IpaDic"));
            var words = new List<DictionaryWord>();
            // The analyzer's UTF-8 bridge cannot safely parse surrogate pairs. Keep offsets exact.
            int start = 0;
            for (int i = 0; i <= text.Length; i++) {
                if (i < text.Length && !char.IsSurrogate(text[i]) && !char.IsControl(text[i])) continue;
                if (i > start) Analyze(text.Substring(start, i - start), start, words, token);
                if (i < text.Length && char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                start = i + 1;
            }
            return words;
        }
    }
    private void Analyze(string text, int offset, List<DictionaryWord> result, CancellationToken token)
    {
        var nodes = tagger!.Parse(text);
        for (int i = 0; i < nodes.Length; i++) {
            token.ThrowIfCancellationRequested();
            var root = nodes[i]; int start = root.EPos - root.Surface.Length;
            if (!Exact(text, root) || root.PartsOfSpeech == "記号" || !Query(root.Surface)) continue;
            int end = root.EPos, last = i;
            if (root.Stat == MeCabNodeStat.Nor && root.PartsOfSpeech is "動詞" or "形容詞" or "助動詞") {
                while (last + 1 < nodes.Length && Exact(text, nodes[last + 1])
                    && nodes[last + 1].EPos - nodes[last + 1].Surface.Length == end && CanJoin(root, nodes[last], nodes[last + 1]))
                    end = nodes[++last].EPos;
            }
            string surface = text[start..end];
            string lemma = root.Stat == MeCabNodeStat.Nor && Query(root.OriginalForm) ? root.OriginalForm : surface;
            if (surface.Length <= 160) result.Add(new(offset + start, end - start, surface, lemma));
            i = last;
        }
    }
    private static bool Query(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 160 && text.Any(char.IsLetter) && !text.Any(char.IsControl);
    private static bool Exact(string text, MeCabIpaDicNode node) => node.Surface.Length > 0 && node.EPos <= text.Length
        && node.EPos >= node.Surface.Length && text.AsSpan(node.EPos - node.Surface.Length, node.Surface.Length).SequenceEqual(node.Surface);
    private static bool CanJoin(MeCabIpaDicNode root, MeCabIpaDicNode previous, MeCabIpaDicNode next)
    {
        if (next.Stat != MeCabNodeStat.Nor) return false;
        var form = previous.Inflection;
        if (next.PartsOfSpeech == "助詞") return next.PartsOfSpeechSection1 == "接続助詞" && next.Surface is "て" or "で"
            && form.StartsWith("連用", StringComparison.Ordinal) && previous.PartsOfSpeech is "動詞" or "形容詞" or "助動詞";
        if (next.PartsOfSpeech != "助動詞") return false;
        return next.OriginalForm switch {
            "た" or "だ" => next.ConjugatedForm == "特殊・タ" && form.StartsWith("連用", StringComparison.Ordinal),
            "ます" => previous.PartsOfSpeech == "動詞" && form.StartsWith("連用", StringComparison.Ordinal),
            "ない" => previous.PartsOfSpeech == "動詞" && form.StartsWith("未然", StringComparison.Ordinal) || previous.PartsOfSpeech == "形容詞" && form == "連用テ接続",
            "ん" => previous.OriginalForm == "ます" && form.StartsWith("未然", StringComparison.Ordinal),
            "です" => previous.OriginalForm == "ん" && root.PartsOfSpeech == "動詞", _ => false
        };
    }
    public void Dispose() { lock (sync) { disposed = true; tagger?.Dispose(); tagger = null; } }
}
