namespace KoTrans.Core;

// One catalog supplies provider capabilities to validation, WPF and credential storage.
// Never renumber existing enum values: saved profiles serialize them as JSON numbers.
public sealed record TranslationCredentialField(string Id, string Label);
public sealed record TranslationProviderInfo(
    TranslationProvider Provider, string DisplayName, bool RequiresModel,
    bool SupportsStudy, bool SupportsContext, bool SupportsStreaming,
    bool SupportsCharacterDictionary, IReadOnlyList<TranslationCredentialField> Credentials);

public static class TranslationProviderCatalog
{
    private static readonly TranslationCredentialField[] ApiKey = [new("ApiKey", "API 키")];
    public static IReadOnlyList<TranslationProviderInfo> All { get; } =
    [
        new(TranslationProvider.HyMtLocal, "로컬 AI 번역", false, false, true, false, true, []),
        new(TranslationProvider.Gemini, "Gemini", true, true, true, true, true, ApiKey),
        new(TranslationProvider.OpenAI, "OpenAI", true, true, true, true, true, ApiKey),
        new(TranslationProvider.Ollama, "Ollama", true, true, true, false, true, []),
        new(TranslationProvider.DeepL, "DeepL", false, false, true, false, false, ApiKey)
    ];

    public static TranslationProviderInfo Get(TranslationProvider provider)
        => All.FirstOrDefault(p => p.Provider == provider)
            ?? throw new InvalidDataException("지원하지 않는 번역 제공자입니다.");
}
