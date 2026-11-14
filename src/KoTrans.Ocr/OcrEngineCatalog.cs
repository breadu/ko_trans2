using KoTrans.Core;

namespace KoTrans.Ocr;

public sealed record OcrEngineRegistration(OcrEngineDescriptor Descriptor,
    Func<string, IOcrService> Create, Func<string, OcrOptions, OcrModelReadiness> CheckModels,
    Func<string, OcrRunIdentity, string?, IOcrService>? Replay = null,
    IReadOnlyList<OcrModelPresetRegistration>? Presets = null);

// The single composition point. Adding an engine never requires a UI/pipeline switch.
public sealed class OcrEngineCatalog
{
    private readonly Dictionary<string, OcrEngineRegistration> entries;
    public OcrEngineCatalog(IEnumerable<OcrEngineRegistration> registrations)
    {
        entries = new(StringComparer.Ordinal);
        foreach (var entry in registrations)
            if (string.IsNullOrWhiteSpace(entry.Descriptor.Id) || !entries.TryAdd(entry.Descriptor.Id, entry))
                throw new ArgumentException("OCR 엔진 ID가 비어 있거나 중복되었습니다.");
    }
    public IReadOnlyList<OcrEngineDescriptor> Engines => entries.Values.Select(e => e.Descriptor).ToArray();
    public OcrEngineRegistration Get(string id) => entries.TryGetValue(id, out var entry) ? entry
        : throw new OcrInitializationException($"저장된 OCR 엔진 '{id}'을 사용할 수 없습니다. 프로필에서 지원되는 엔진을 직접 선택하세요. 자동 대체하지 않습니다.");
    public static OcrEngineCatalog Default { get; } = new([PaddleEngine.Registration]);
}

public sealed class OcrServiceFactory(OcrEngineCatalog catalog, string modelsRoot, DirectMlWorkerConfiguration? directMl = null)
{
    public OcrEngineCatalog Catalog => catalog;
    public OcrModelPresetCatalog Presets { get; } = new(catalog);
    public void Validate(OcrOptions options)
    { catalog.Get(options.EngineId).Descriptor.Validate(options); Presets.Get(options.EngineId, options.ModelPresetId).Descriptor.Validate(options); }
    public string SelectionKey(OcrOptions options)
    { Validate(options); return Presets.Get(options.EngineId, options.ModelPresetId).SelectionKey?.Invoke(modelsRoot, options) ?? options.Language; }
    public OcrModelReadiness CheckModels(OcrOptions options)
    {
        try { Validate(options); return Presets.Get(options.EngineId, options.ModelPresetId).CheckModels(modelsRoot, options); }
        catch (Exception ex) when (ex is OcrInitializationException or IOException or InvalidDataException or UnauthorizedAccessException)
        { return new(false, ex.Message); }
    }
    public IOcrService Create(string id) => Create(new OcrOptions(catalog.Get(id).Descriptor.Languages[0], "ADV", false, EngineId: id));
    public IOcrService Create(OcrOptions options)
    {
        Validate(options); var preset = Presets.Get(options.EngineId, options.ModelPresetId);
        IOcrService service;
#if OCR_DIRECTML_WORKER
        _ = directMl;
        service = preset.Create(modelsRoot, options);
#else
        service = options.UseDirectMl && DirectMlProtocol.Supports(options)
            ? new DirectMlOcrService(() => preset.Create(modelsRoot, options with { UseDirectMl = false }),
                () => new DirectMlWorkerClient(modelsRoot, directMl ?? new()))
            : preset.Create(modelsRoot, options);
#endif
        return Wrap(catalog.Get(options.EngineId), service, preset.Descriptor);
    }
    // Existing DB/CTC model comparison Lab only. The application uses Create(id).
    public static IOcrService CreatePaddleDiagnostic(string root, OcrExecutionOptions execution) =>
        Wrap(PaddleEngine.Registration, PaddleEngine.CreateDiagnostic(root, execution));
    public static OcrExecutionOptions PreparePaddleReplay(string root, OcrOptions options, OcrRunIdentity identity, string? comparison = null)
        => PaddleModelPresets.Replay(root, options, identity, comparison);
    public IOcrService CreateReplay(OcrOptions options, OcrRunIdentity identity, string? comparisonDetector = null)
    {
        Validate(options);
        if (identity.EngineId != options.EngineId || identity.ModelPresetId != options.ModelPresetId) throw new InvalidDataException("QA 요청과 결과의 OCR 엔진이 다릅니다.");
        var entry = catalog.Get(options.EngineId);
        if (identity.ImplementationVersion != entry.Descriptor.ImplementationVersion)
            throw new OcrInitializationException("저장 당시 OCR 구현 버전을 현재 엔진에서 정확하게 재현할 수 없습니다.");
        var preset = Presets.Get(options.EngineId, options.ModelPresetId);
        if (preset.Replay is null) throw new OcrInitializationException($"{preset.Descriptor.DisplayName}의 정확한 QA 재현을 지원하지 않습니다.");
        IOcrService service;
#if OCR_DIRECTML_WORKER
        service = preset.Replay(modelsRoot, options, identity, comparisonDetector);
#else
        // An explicitly changed comparison detector is a CPU comparison, never a
        // newly/implicitly GPU-approved model. Exact replay keeps saved identities.
        service = options.UseDirectMl && DirectMlProtocol.Supports(options) && comparisonDetector is null
            ? new DirectMlOcrService(() => preset.Replay(modelsRoot, options with { UseDirectMl = false }, identity, null),
                () => new DirectMlWorkerClient(modelsRoot, directMl ?? new(), identity))
            : preset.Replay(modelsRoot, options, identity, comparisonDetector);
#endif
        return Wrap(entry, service, preset.Descriptor);
    }
    private static IOcrService Wrap(OcrEngineRegistration entry, IOcrService service, OcrModelPresetDescriptor? preset = null) => new RegisteredService(entry.Descriptor, service, preset);
    private sealed class RegisteredService(OcrEngineDescriptor descriptor, IOcrService service, OcrModelPresetDescriptor? preset) : IOcrService
    {
        public OcrExecutionStatus ExecutionStatus => service.ExecutionStatus;
        public event Action<OcrExecutionStatus>? ExecutionChanged { add => service.ExecutionChanged += value; remove => service.ExecutionChanged -= value; }
        public OcrState State => service.State;
        public OcrCapabilities Capabilities => descriptor.Capabilities;
        public bool CollectAttemptDiagnostics { set => service.CollectAttemptDiagnostics = value; }
        public Task InitializeAsync(OcrOptions options, CancellationToken token) { descriptor.Validate(options); preset?.Validate(options); return service.InitializeAsync(options, token); }
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            descriptor.Validate(options); preset?.Validate(options);
            var result = await service.RecognizeAsync(frame, options, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Models is { } identity && (identity.EngineId != descriptor.Id || identity.ImplementationVersion != descriptor.ImplementationVersion || identity.ModelPresetId != options.ModelPresetId))
                throw new InvalidDataException("OCR 구현과 실제 모델 식별 정보가 일치하지 않습니다.");
            return result;
        }
        public ValueTask DisposeAsync() => service.DisposeAsync();
    }
}

internal static class PaddleEngine
{
    internal static IOcrService CreateDiagnostic(string root, OcrExecutionOptions execution) => new PaddleOcrService(Root(root), execution);
    internal static readonly OcrEngineRegistration Registration = new(
        new("paddle", "PaddleOCR", OcrAlgorithmRevision.Current, ["en", "ja"], true, false,
            OcrCapabilities.Paddle, true, true, "기본 OCR · 영어/일본어 · ADV/NVL · 세로쓰기"),
        root => new PaddleOcrService(Root(root)),
        (root, options) => PaddleModelPresets.Registrations.Single(p => p.Descriptor.Id == "default").CheckModels(root, options),
        Presets: PaddleModelPresets.Registrations);
    internal static string Root(string root) => PaddleModelPresets.Root(root);
}
