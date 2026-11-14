namespace KoTrans.Core;

public sealed record ExplanationRequest(string Text, string Language, TranslationOptions Options);
public sealed record ExplanationResult(StudyResult Study, TimeSpan Elapsed, int HttpAttempts = 0, bool FromCache = false);
public interface IExplanationService
{
    Task<ExplanationResult> ExplainAsync(ExplanationRequest request, CancellationToken token);
}
public sealed record ReadingToken(string Surface, string? Hiragana, bool HasKanji);
public sealed record JapaneseReading(string Text, IReadOnlyList<ReadingToken> Tokens, string Notice);
