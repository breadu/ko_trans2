using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using KoTrans.Core;
using NMeCab.Specialized;

namespace KoTrans.Infrastructure;

// Lazy managed analyzer. Its dictionary is local; no Python process or network request.
public sealed class JapaneseReadingService : IDisposable
{
    private readonly object sync = new();
    private MeCabIpaDicTagger? tagger;
    private bool disposed;
    public JapaneseReading Read(string text, CancellationToken token = default)
    {
        ExplanationPrompt.ValidateInput(text);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
            tagger ??= MeCabIpaDicTagger.Create(Path.Combine(AppContext.BaseDirectory, "IpaDic"));
            // Keep explicit speaker labels untouched. Names often have uncertain readings.
            var label = Regex.Match(text, @"^([\[［【(（].+?[\]］】)）][:：]?|[^:：\s]{1,12}[:：])\s*");
            var result = new List<ReadingToken>(); var output = new StringBuilder();
            int offset = label.Length; output.Append(label.Value);
            if (label.Success) result.Add(new(label.Value, null, false));
            foreach (var node in tagger.Parse(text[offset..]))
            {
                token.ThrowIfCancellationRequested();
                int position = text.IndexOf(node.Surface, offset, StringComparison.Ordinal);
                if (position < offset) throw new TranslationException("일본어 읽기를 원문과 맞출 수 없습니다.");
                output.Append(text.AsSpan(offset, position - offset));
                bool kanji = node.Surface.Any(c => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');
                var reading = string.IsNullOrWhiteSpace(node.Reading) || node.Reading == "*" ? null : ToHiragana(node.Reading);
                // Unknown/proper names remain unassigned instead of presenting a dictionary guess as certain.
                if (node.Feature.Contains("固有名詞", StringComparison.Ordinal)) reading = null;
                result.Add(new(node.Surface, reading, kanji));
                output.Append(node.Surface);
                if (kanji && reading is not null) output.Append('（').Append(reading).Append('）');
                offset = position + node.Surface.Length;
            }
            output.Append(text.AsSpan(offset));
            return new(output.ToString(), result.AsReadOnly(), "로컬 IPA 사전의 읽기 후보입니다. 동형어·고유명사는 틀리거나 비어 있을 수 있습니다. AI 발음과 별도 정보입니다.");
        }
    }
    public static string ToHiragana(string value) => new(value.Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; tagger?.Dispose(); tagger = null; } }
}
