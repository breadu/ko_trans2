using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Globalization;
namespace KoTrans.Core;

public sealed record LocalWorkerRequest(string Id, string Type, string Prompt = "", int MaxTokens = 256);
public sealed record LocalWorkerReply(string Id, bool Ready = false, string? Text = null, string? Error = null, string? Backend = null, string? Stop = null, double Milliseconds = 0,
    int OutputTokens = 0, int InputTokens = 0, double FirstTokenMilliseconds = 0);
public enum LocalTranslationLimit { Input, Output }
public sealed class LocalTranslationLimitException(LocalTranslationLimit limit) : TranslationException(limit == LocalTranslationLimit.Input
    ? "로컬 번역의 입력 한도에 도달했습니다. 더 짧은 대사 영역을 지정해 주세요."
    : "로컬 번역이 출력 한도에 도달했습니다. 더 짧은 대사 영역을 지정해 주세요.")
{
    public LocalTranslationLimit Limit { get; } = limit;
}
public static class HyMtPrompt
{
    public const string Prefix = "<|startoftext|>", Suffix = "<|extra_0|>";
    private static readonly JsonSerializerOptions TermJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    public static string Build(string source, IReadOnlyList<ConversationTurn> history, LocalTranslationModel model = LocalTranslationModel.Standard7B,
        IReadOnlyList<CharacterEntry>? characters = null, int paragraphCount = 1)
        => Frame(BuildUserMessage(source, history, model, characters, paragraphCount), model);

    // Ollama owns the chat template. Share the trained instructions and bounded
    // source-only references without sending native model control tokens twice.
    public static string BuildUserMessage(string source, IReadOnlyList<ConversationTurn> history, LocalTranslationModel model = LocalTranslationModel.Standard7B,
        IReadOnlyList<CharacterEntry>? characters = null, int paragraphCount = 1)
    {
        var background = new StringBuilder();
        // Only accepted original lines are used. No invented scene or relationships.
        foreach (var turn in history.TakeLast(4))
        {
            if (HasControlToken(turn.Source)) continue;
            if (background.Length + turn.Source.Length + Environment.NewLine.Length > 4000) break;
            background.AppendLine(turn.Source);
        }
        return BuildMessageWithBackground(source, background.ToString().Trim(), model, characters, paragraphCount);
    }
    public static string BuildWithBackground(string source, string background, LocalTranslationModel model = LocalTranslationModel.Standard7B,
        IReadOnlyList<CharacterEntry>? characters = null, int paragraphCount = 1)
        => Frame(BuildMessageWithBackground(source, background, model, characters, paragraphCount), model);

    // HY-MT 1.5's contextual template is different from the bundled HY-MT2 prompt.
    // Keep instructions/references ABOVE the final translation task, with no headings
    // between that task and the complete current source. Ollama supplies native tokens.
    public static string BuildOllamaUserMessage(string source, IReadOnlyList<ConversationTurn> history,
        IReadOnlyList<CharacterEntry>? characters = null, int paragraphCount = 1)
    {
        ValidateSource(source);
        string background = RecentOllamaBackground(source, history);
        string references = (background.Length > 0 ? background + "\n\n" : "") + BuildTerms(source, characters);
        if (paragraphCount > 1) references += TranslationParagraphs.Instruction(paragraphCount) + "\n\n";
        return references + (references.Length > 0
            ? "参考上面的信息，把下面的文本翻译成韩语，注意不需要翻译上文，也不要额外解释："
            : "Translate the following segment into Korean, without additional explanation.") + "\n\n" + source;
    }

    private static string RecentOllamaBackground(string source, IReadOnlyList<ConversationTurn> history)
    {
        const int limit = 512;
        static string Comparable(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        string current = Comparable(source);
        // A history entry can be an entire accumulated NVL page, not one sentence.
        // Do not re-present the current block as both prior page and translation target.
        foreach (var turn in history.TakeLast(4).Reverse()) {
            string previous = turn.Source.Trim();
            if (previous.Length == 0 || HasControlToken(previous)) continue;
            string comparable = Comparable(previous);
            if (comparable.Length == 0 || current.Contains(comparable, StringComparison.Ordinal)
                || current.Length >= 8 && comparable.Contains(current, StringComparison.Ordinal)) continue;
            if (previous.Length <= limit) return previous;
            int start = previous.Length - limit;
            int end = NvlText.TranslationEnds(previous).FirstOrDefault(i => i >= start && i < previous.Length, -1);
            if (end >= 0) start = end;
            // Do not split a UTF-16 pair or combining sequence when no sentence fits.
            start = StringInfo.ParseCombiningCharacters(previous).FirstOrDefault(i => i >= start, previous.Length);
            return previous[start..].Trim();
        }
        return "";
    }

    private static string BuildMessageWithBackground(string source, string background, LocalTranslationModel model,
        IReadOnlyList<CharacterEntry>? characters, int paragraphCount)
    {
        LocalTranslationModels.Get(model);
        ValidateSource(source);
        if (background.Length > 4000 || HasControlToken(background))
            throw new TranslationException("로컬 번역에 사용할 이전 원문 길이와 내용을 확인하세요.");
        string body = BuildTerms(source, characters);
        if (!string.IsNullOrWhiteSpace(background))
            body += "[Background Information]\n" + background + "\n\n";
        // Keep the trained framing and original-only context. The lightweight model
        // needs an explicit completeness/order rule; the standard prompt is unchanged.
        body += (model == LocalTranslationModel.Lightweight18B
            ? "Translate ALL of [Source Text] into Korean, in its original order. Use [Background Information] only for understanding, never translate or repeat it. Output only the complete Korean translation."
            : "Please translate the following text into Korean, taking the provided background information into consideration. Translate only [Source Text], not [Background Information]. Output only the Korean translation.")
            + (paragraphCount > 1 ? "\n" + TranslationParagraphs.Instruction(paragraphCount) : "") + "\n\n[Source Text]\n" + source;
        return body;
    }

    private static string Frame(string body, LocalTranslationModel model) => model == LocalTranslationModel.Lightweight18B
        ? "<｜hy_begin▁of▁sentence｜><｜hy_User｜>" + body + "<｜hy_Assistant｜>" : Prefix + body + Suffix;

    private static bool HasControlToken(string text) => text.Contains("<|", StringComparison.Ordinal) || text.Contains("<｜", StringComparison.Ordinal);

    public static void ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 16000 || HasControlToken(source))
            throw new TranslationException("로컬 번역에 사용할 원문 길이와 내용을 확인하세요.");
    }

    // Share name resolution with cloud/Ollama requests. Only the written part of
    // each name enters the terminology map, never a full-name expansion or biography.
    private static string BuildTerms(string source, IReadOnlyList<CharacterEntry>? characters)
    {
        if (characters is not { Count: > 0 }) return "";
        var terms = new StringBuilder();
        foreach (var hint in CharacterNameHints.Resolve(source, characters))
        {
            if (HasControlToken(hint.SourceName) || HasControlToken(hint.KoreanName))
                throw new TranslationException("캐릭터 사전의 이름과 한국어 표기를 확인하세요.");
            terms.Append(JsonSerializer.Serialize(hint.SourceName, TermJson)).Append(" translates to ")
                .AppendLine(JsonSerializer.Serialize(hint.KoreanName, TermJson));
            if (terms.Length > 4000)
                throw new TranslationException("현재 원문에 해당하는 캐릭터 사전이 너무 큽니다. 이름과 한국어 표기를 짧게 정리해 주세요.");
        }
        return terms.Length == 0 ? "" : "Reference the following translations:\n" + terms.ToString().TrimEnd() + "\n\n";
    }

}
