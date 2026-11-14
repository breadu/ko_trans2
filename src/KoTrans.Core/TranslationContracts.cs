using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace KoTrans.Core;

public enum CaptureMode { Region, Window, Clipboard }
public enum TranslationProvider { Gemini = 0, OpenAI = 1, Ollama = 2, DeepL = 3, HyMtLocal = 4 }
public enum DeepLApiPlan { Free, Pro }
public enum OllamaRequestFormat { Chat, TranslateGemma, HyMt }
public sealed record OllamaModelSettings(OllamaRequestFormat Format = OllamaRequestFormat.Chat, int TimeoutSeconds = 120, int KeepAliveMinutes = 10);
public enum PipelineState { Stopped, Starting, Watching, Recognizing, Translating, Error }

public sealed record TranslationOptions
{
    public TranslationProvider Provider { get; set; } = TranslationProvider.HyMtLocal;
    // Deliberately empty: the user selects the exact model; no silent substitution.
    public string Model { get; set; } = "";
    public string GeminiModel { get; set; } = "";
    public string OpenAIModel { get; set; } = "";
    public string OllamaModel { get; set; } = "";
    public LocalTranslationModel LocalModel { get; set; } = LocalTranslationModel.Standard7B;
    // Legacy model fields remain for existing settings. Additional providers use this slot.
    public ImmutableDictionary<string, string> AdditionalModels { get; set; } = ImmutableDictionary<string, string>.Empty;
    public DeepLApiPlan DeepLPlan { get; set; } = DeepLApiPlan.Free;
    public ImmutableDictionary<string, OllamaModelSettings> OllamaModels { get; set; } = ImmutableDictionary<string, OllamaModelSettings>.Empty;
    public OllamaModelSettings SettingsForOllama(string? id = null) => OllamaModels.TryGetValue(id ?? Model, out var value) ? value : new(TimeoutSeconds: Math.Max(120, TimeoutSeconds));
    [JsonIgnore] public int RequestTimeoutSeconds => Provider == TranslationProvider.Ollama ? SettingsForOllama().TimeoutSeconds : TimeoutSeconds;
    private void RememberModel()
    {
        switch (Provider) {
            case TranslationProvider.Gemini: GeminiModel = Model; break;
            case TranslationProvider.OpenAI: OpenAIModel = Model; break;
            case TranslationProvider.Ollama: OllamaModel = Model; break;
            default:
                if (TranslationProviderCatalog.Get(Provider).RequiresModel)
                    AdditionalModels = AdditionalModels.SetItem(Provider.ToString(), Model);
                break;
        }
    }
    public void SwitchProvider(TranslationProvider next)
    {
        var nextInfo = TranslationProviderCatalog.Get(next);
        RememberModel(); Provider = next;
        Model = next switch {
            TranslationProvider.Gemini => GeminiModel,
            TranslationProvider.OpenAI => OpenAIModel,
            TranslationProvider.Ollama => OllamaModel,
            _ => nextInfo.RequiresModel && AdditionalModels.TryGetValue(next.ToString(), out var saved) ? saved : ""
        };
    }
    public string OllamaAddress { get; set; } = "http://localhost:11434";
    public int TimeoutSeconds { get; set; } = 30;
    public void Validate()
    {
        TranslationProviderCatalog.Get(Provider);
        LocalTranslationModels.Get(LocalModel);
        if (!Enum.IsDefined(DeepLPlan) || AdditionalModels is null || AdditionalModels.Count > 32
            || AdditionalModels.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 80 || p.Key.Any(char.IsControl)
                || p.Value is null || p.Value.Length > 200 || p.Value.Any(char.IsControl))
            || new[] { Model, GeminiModel, OpenAIModel, OllamaModel }.Any(value => value is null || value.Length > 200 || value.Any(char.IsControl))
            || TimeoutSeconds is < 5 or > 300 || !Uri.TryCreate(OllamaAddress, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("모델·서버 주소·제한 시간을 확인하세요.");
        if (OllamaModels is null || OllamaModels.Count > 32 || OllamaModels.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 200 || p.Key.Any(char.IsControl)
            || p.Value is null || !Enum.IsDefined(p.Value.Format) || p.Value.TimeoutSeconds is < 5 or > 300 || p.Value.KeepAliveMinutes is < 0 or > 60))
            throw new InvalidDataException("Ollama 모델별 요청 형식·제한 시간을 확인하세요.");
        // The legacy active Model remains authoritative; migrate it only into its own provider slot.
        RememberModel();
    }
}
public sealed class TriggerOptions
{
    public bool Enabled { get; set; } = true;
    public string Keys { get; set; } = "Enter,Space";
    public string MouseButton { get; set; } = "Left";
    public string GamepadButtons { get; set; } = "Joy1,Joy2";
    public void Validate()
    {
        if (Keys is null || Keys.Length > 200 || GamepadButtons is null || GamepadButtons.Length > 200
            || MouseButton is not ("None" or "Left" or "Right" or "Middle" or "X1" or "X2"))
            throw new InvalidDataException("입력 트리거 설정이 올바르지 않습니다.");
    }
}
public sealed class HotkeyOptions
{
    public string ShowSettings { get; set; } = "F1";
    public string ToggleTranslation { get; set; } = "Shift+F1";
    public string Explain { get; set; } = "Shift+F2";
    public string StudyScrollUp { get; set; } = "Shift+PageUp";
    public string StudyScrollDown { get; set; } = "Shift+PageDown";
    public string SaveGameQa { get; set; } = "F8";
    public void UpgradeDefaultWindowKeys()
    {
        // Migrate only the unchanged legacy default pair. Preserve custom pairs
        // and avoid taking F1/Shift+F1 away from another configured command.
        if (ShowSettings != "F12" || ToggleTranslation != "Shift+F12") return;
        if (new[] { Explain, StudyScrollUp, StudyScrollDown, SaveGameQa }.Any(s =>
            string.Concat((s ?? "").Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant() is "F1" or "SHIFT+F1")) return;
        ShowSettings = "F1"; ToggleTranslation = "Shift+F1";
    }
    public void UpgradeGameQaKey()
    {
        if (SaveGameQa?.Split('+', StringSplitOptions.TrimEntries).Any(p => p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) == true)
            SaveGameQa = "F8";
    }
    public void UpgradeDefaultStudyKeys()
    {
        // Move unchanged old defaults individually, preserving custom assignments.
        // Do not take a target chord already used by another application command.
        Explain = Upgrade(Explain, "Alt+F12", "Shift+F2");
        StudyScrollUp = Upgrade(StudyScrollUp, "Alt+F10", "Shift+PageUp");
        StudyScrollDown = Upgrade(StudyScrollDown, "Alt+F11", "Shift+PageDown");

        string Upgrade(string current, string previous, string next) =>
            Normalize(current) == Normalize(previous)
            && !new[] { ShowSettings, ToggleTranslation, Explain, StudyScrollUp, StudyScrollDown, SaveGameQa }
                .Any(value => Normalize(value) == Normalize(next)) ? next : current;

        static string Normalize(string? value) => string.Concat((value ?? "").Where(c => !char.IsWhiteSpace(c)))
            .ToUpperInvariant().Replace("+PRIOR", "+PAGEUP").Replace("+NEXT", "+PAGEDOWN");
    }
    public void Validate()
    {
        var values = new[] { ShowSettings, ToggleTranslation, Explain, StudyScrollUp, StudyScrollDown, SaveGameQa };
        // Newly added defaults may overlap a valid Phase 3 custom key. Keep the settings readable;
        // registration reports conflicts and the editor requires distinct choices when applying.
        if (values.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 80) || ShowSettings == ToggleTranslation)
            throw new InvalidDataException("전역 단축키 설정을 확인하세요.");
    }
}
public sealed record CharacterEntry(string Name, string KoreanName, string Description);
public sealed record ConversationTurn(string Source, string Translation);
public sealed record TranslationRequest(string Text, string SystemPrompt, IReadOnlyList<ConversationTurn> History, TranslationOptions Options, System.Text.Json.JsonElement? OutputSchema = null, Action<string>? Progress = null, string? SourceLanguage = null, bool PreserveHistory = false, IReadOnlyList<CharacterEntry>? Characters = null, int ParagraphCount = 1);
public static class TranslationParagraphs
{
    public static string Instruction(int count) => count > 1
        ? $"The current source contains {count} paragraphs separated by blank lines. Translate all paragraphs in order, returning exactly {count} translated paragraphs separated by blank lines. Do not merge, omit or number paragraphs."
        : "";
}
public sealed record TranslationResult(string Text, TimeSpan Elapsed, int HttpAttempts = 1);
public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token);
}
public interface ITranslationDiagnostics { int HttpAttempts { get; } }
// Safe messages only; HTTP bodies, credentials and source text must not enter exception logs/UI.
public class TranslationException(string message) : Exception(message);
[Flags]
public enum PipelineStatusArea { None = 0, Input = 1, Capture = 2, Ocr = 4, Translation = 8 }
public sealed record PipelineUpdate(long Version, PipelineState State, string Message, string? Original = null,
    string? Translation = null, OcrResult? Ocr = null, double CaptureMs = 0, double ApiMs = 0, double TotalMs = 0,
    int OcrRequests = 0, int TranslationRequests = 0, int HttpAttempts = 0, int CacheHits = 0)
{
    // Activity is independent of the last accepted subtitle: OCR and AI can overlap.
    public bool IsBusy { get; init; }
    public bool Important { get; init; }
    // Presentation evidence only: starting work and cached/partial output are not AI recovery.
    public PipelineStatusArea FailureArea { get; init; }
    public PipelineStatusArea RecoveredAreas { get; init; }
    // Short user-facing notice for the subtitle overlay; Message retains diagnostic detail.
    public string? OverlayMessage { get; init; }
}
