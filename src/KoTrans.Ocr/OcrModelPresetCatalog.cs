using KoTrans.Core;

namespace KoTrans.Ocr;

public sealed record OcrModelPresetDescriptor(string EngineId, string Id, string DisplayName,
    IReadOnlyList<string> Languages, string Description, string Detector = "", string Recognizer = "")
{
    public void Validate(OcrOptions options)
    {
        if (options.EngineId != EngineId || options.ModelPresetId != Id)
            throw new OcrInitializationException("OCR 모델 구성과 요청의 식별자가 다릅니다.");
        if (!Languages.Contains(options.Language))
            throw new OcrInitializationException($"{DisplayName}은 선택한 언어({options.Language})를 지원하지 않습니다.");
    }
}
public sealed record OcrModelPresetRegistration(OcrModelPresetDescriptor Descriptor,
    Func<string, OcrOptions, IOcrService> Create,
    Func<string, OcrOptions, OcrModelReadiness> CheckModels,
    Func<string, OcrOptions, OcrRunIdentity, string?, IOcrService>? Replay = null,
    Func<string, OcrOptions, string>? SelectionKey = null);

// Engine registrations supply their presets. Existing engines receive a compatible default.
public sealed class OcrModelPresetCatalog
{
    private readonly Dictionary<(string Engine, string Preset), OcrModelPresetRegistration> entries = [];
    public OcrModelPresetCatalog(OcrEngineCatalog engines)
    {
        foreach (var engine in engines.Engines)
        {
            var registration = engines.Get(engine.Id);
            var presets = registration.Presets ?? [new(new(engine.Id, "default", engine.DisplayName,
                engine.Languages, engine.Description), (root, _) => registration.Create(root), registration.CheckModels,
                registration.Replay is null ? null : (root, _, identity, comparison) => registration.Replay(root, identity, comparison))];
            foreach (var preset in presets)
                if (preset.Descriptor.EngineId != engine.Id || string.IsNullOrWhiteSpace(preset.Descriptor.Id)
                    || !entries.TryAdd((engine.Id, preset.Descriptor.Id), preset))
                    throw new ArgumentException("OCR 모델 구성 ID가 비어 있거나 중복되었거나 소속 엔진이 다릅니다.");
            if (!entries.ContainsKey((engine.Id, "default"))) throw new ArgumentException("기본 OCR 모델 구성이 필요합니다.");
        }
    }
    public IReadOnlyList<OcrModelPresetDescriptor> Presets => entries.Values.Select(p => p.Descriptor).ToArray();
    public OcrModelPresetRegistration Get(string engine, string preset) => entries.TryGetValue((engine, preset), out var entry) ? entry
        : throw new OcrInitializationException($"OCR 모델 구성 '{engine}/{preset}'을 사용할 수 없습니다. 지원되는 모델을 직접 선택하세요. 자동 대체하지 않습니다.");
}
