using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public sealed class HyMtTranslationService(ILocalModelRuntime runtime, LocalTranslationModel selection = LocalTranslationModel.Standard7B) : ITranslationService
{
    // A conservative starting size, not a token estimate. The worker still checks
    // the real tokenizer/input budget and EOG; only explicit limits permit splitting.
    internal const int ChunkCharacters = 240;
    private const int MaxAttempts = 256;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
    {
        if (request.OutputSchema is not null) throw new TranslationException("로컬 AI 번역은 문장 해설을 지원하지 않습니다.");
        // Validate before partitioning so a control token cannot straddle two chunks.
        HyMtPrompt.ValidateSource(request.Text);
        token.ThrowIfCancellationRequested();
        var history = request.History.TakeLast(4).ToList();
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        int attempts = 0;
        try {
            string result = await TranslateText(request.Text, request.ParagraphCount).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return new(result, clock.Elapsed, 0);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            throw new TranslationException("로컬 번역 시간이 초과되었습니다. 원문 영역을 줄인 뒤 다시 시도해 주세요.");
        }

        async Task<string> TranslateText(string source, int paragraphs)
        {
            timeout.Token.ThrowIfCancellationRequested();
            // Don't ask 1.8B to align a multi-paragraph response. Own that order here,
            // including short NVL batches, and keep single visual wraps together.
            // Standard7B retains its existing batching and prompt behavior.
            if (source.Length > ChunkCharacters || selection == LocalTranslationModel.Lightweight18B
                && LocalTranslationChunks.HasMultipleParagraphs(source))
                return await TranslateSplit(source, ChunkCharacters).ConfigureAwait(false);
            IReadOnlyList<ConversationTurn> context = history.ToArray();
            while (true) {
                timeout.Token.ThrowIfCancellationRequested();
                if (++attempts > MaxAttempts)
                    throw new TranslationException("로컬 번역을 나누어 처리했지만 완료하지 못했습니다. 더 짧은 대사 영역을 지정해 주세요.");
                string prompt = HyMtPrompt.Build(source, context, selection, request.Characters, paragraphs);
                using var partTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                partTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                try {
                    var result = await runtime.TranslateAsync(prompt, partTimeout.Token, selection).ConfigureAwait(false);
                    partTimeout.Token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(result.Text))
                        throw new TranslationException("로컬 번역 결과를 확인할 수 없습니다. 다시 시도해 주세요.");
                    // Request-local originals only. The caller publishes/caches the complete
                    // result atomically; a failed/cancelled page cannot commit half its history.
                    history.Add(new(source, ""));
                    if (history.Count > 4) history.RemoveAt(0);
                    return result.Text.Trim();
                }
                catch (LocalTranslationLimitException ex) {
                    if (ex.Limit == LocalTranslationLimit.Input && context.Count > 0) {
                        // Preserve recent context first; a previous full NVL page may itself
                        // exceed the small model window. Never truncate the current source.
                        context = ReduceContext(context);
                        continue;
                    }
                    if (source.Length < 48) throw;
                    return await TranslateSplit(source, source.Length / 2).ConfigureAwait(false);
                }
            }
        }

        async Task<string> TranslateSplit(string source, int maxCharacters)
        {
            var parts = LocalTranslationChunks.Split(source, maxCharacters, request.Characters);
            if (parts.Count == 0 || parts.Count == 1 && parts[0].Source.Length >= source.Length)
                throw new LocalTranslationLimitException(LocalTranslationLimit.Output);
            var output = new StringBuilder();
            foreach (var part in parts) {
                string result = await TranslateText(part.Source, 1).ConfigureAwait(false);
                // Only real input paragraph boundaries create output paragraph breaks.
                // Subdividing a long sentence or a visual row must not create new paragraphs.
                if (output.Length > 0) output.Append(part.Separator);
                output.Append(Regex.Replace(result, @"\s+", " ").Trim());
            }
            return output.ToString();
        }
    }

    private static IReadOnlyList<ConversationTurn> ReduceContext(IReadOnlyList<ConversationTurn> history)
    {
        if (history.Count > 1) return history.Skip(1).ToArray();
        string latest = history[0].Source;
        if (latest.Length <= 128) return [];
        int start = latest.Length - Math.Min(512, latest.Length / 2);
        // Use text-element boundaries so UTF-16 pairs/combining sequences remain intact.
        start = StringInfo.ParseCombiningCharacters(latest).FirstOrDefault(i => i >= start, latest.Length);
        return start == latest.Length ? [] : [new(latest[start..], "")];
    }
}

internal sealed record LocalTranslationChunk(string Source, string Separator);

internal static class LocalTranslationChunks
{
    internal static bool HasMultipleParagraphs(string source)
        => Paragraphs(source).Skip(1).Any();

    private static IEnumerable<string> Paragraphs(string source)
        => Regex.Split(source.Replace("\r\n", "\n"), @"\n\s*\n").Where(p => !string.IsNullOrWhiteSpace(p));

    internal static IReadOnlyList<LocalTranslationChunk> Split(string source, int maxCharacters, IReadOnlyList<CharacterEntry>? characters = null)
    {
        var result = new List<LocalTranslationChunk>();
        foreach (string paragraph in Paragraphs(source)) {
            string rest = paragraph.Trim();
            string separator = result.Count == 0 ? "" : "\n\n";
            while (rest.Length > maxCharacters) {
                int end = FindEnd(rest, maxCharacters, characters);
                // A single oversized text element cannot be cut safely.
                if (end <= 0) throw new TranslationException("로컬 번역에 사용할 원문 길이와 내용을 확인하세요.");
                result.Add(new(rest[..end].TrimEnd(), separator));
                rest = rest[end..].TrimStart();
                separator = " ";
            }
            if (rest.Length > 0) result.Add(new(rest, separator));
        }
        return result;
    }

    private static int FindEnd(string text, int maximum, IReadOnlyList<CharacterEntry>? characters)
    {
        int minimum = Math.Max(1, maximum / 3);
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        // Keep dictionary names whole even when a sentence must be split mid-line.
        var protectedCuts = new HashSet<int>();
        foreach (var name in CharacterNameHints.Resolve(text, characters).Select(h => h.SourceName))
            for (int start = 0; start < maximum;) {
                int at = text.IndexOf(name, start, StringComparison.Ordinal);
                if (at < 0 || at >= maximum) break;
                for (int cut = at + 1; cut < Math.Min(at + name.Length, maximum + 1); cut++) protectedCuts.Add(cut);
                start = at + name.Length;
            }
        bool Safe(int at) => !protectedCuts.Contains(at) && Array.BinarySearch(boundaries, at) >= 0;
        int sentence = NvlText.TranslationEnds(text).LastOrDefault(i => i >= minimum && i <= maximum && Safe(i));
        if (sentence > 0) return sentence;
        for (int i = maximum; i >= minimum; i--)
            if (char.IsWhiteSpace(text[i]) && Safe(i)) return i;
        return boundaries.LastOrDefault(i => i <= maximum && !protectedCuts.Contains(i));
    }
}
