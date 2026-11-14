using KoTrans.Core;

namespace KoTrans.Ocr;

// A location names a role, not a language: v6 shares one multilingual rec directory.
public sealed record PaddleModelLocation(string Root, string Folder)
{
    public string Stamp()
    {
        static string FileStamp(string path) { var f = new FileInfo(path); return path + ":" + (f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "missing"); }
        string manifest = Path.GetFullPath(Path.Combine(Root, Folder, "manifest.json"));
        try {
            var spec = ModelCatalog.Inspect(Root, Folder);
            return FileStamp(manifest) + "|" + FileStamp(ModelCatalog.FilePath(Root, Folder, spec.File))
                + (spec.Kind == "recognition" ? "|" + FileStamp(ModelCatalog.FilePath(Root, Folder, spec.Dictionary)) : "");
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return FileStamp(manifest) + "|unavailable"; }
    }
    public void CheckPresence()
    {
        var spec = ModelCatalog.Inspect(Root, Folder);
        foreach (var file in spec.Kind == "recognition" ? new[] { spec.File, spec.Dictionary } : new[] { spec.File })
            if (!File.Exists(ModelCatalog.FilePath(Root, Folder, file))) throw new FileNotFoundException($"OCR 모델 파일 누락: {Folder}/{file}");
    }
}
public sealed record PaddleModelSelection(PaddleModelLocation Detector, PaddleModelLocation Recognizer)
{
    public static PaddleModelSelection Resolve(string root, OcrExecutionOptions execution, string language)
    {
        string recFolder = execution.RecognizerFolder ?? language;
        // Legacy default only: fast/ja does not imply a fast/en model exists.
        string recRoot = execution.RecognizerRoot is { } preferred
            && (execution.RecognizerFolder is not null || File.Exists(Path.Combine(preferred, language, "manifest.json"))) ? preferred : root;
        return new(new(execution.DetectorRoot ?? root, "det"), new(recRoot, recFolder));
    }
    public string Key => Detector.Stamp() + "|" + Recognizer.Stamp();
}
internal static class PaddleModelPresets
{
    // Creating the v6 directory must not redirect a legacy root to an empty paddle root.
    internal static string Root(string root) => Directory.Exists(Path.Combine(root, "paddle", "det")) || Directory.Exists(Path.Combine(root, "paddle", "fast"))
        ? Path.Combine(root, "paddle") : root;
    internal static string V6Root(string root) => Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("paddle", StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(root, "v6-medium") : Path.Combine(root, "paddle", "v6-medium");

    private static readonly Dictionary<string, Func<string, OcrExecutionOptions>> selections = [];
    internal static OcrExecutionOptions Execution(string root, string id) { _ = Registrations; return selections[id](root); }
    internal static IReadOnlyList<OcrModelPresetRegistration> Registrations { get; } = [
        Register("v6-medium-full", "PaddleOCR · v6 Medium 검출·인식 (기본)", "기본 OCR · v6 Medium 검출·인식 · 영어/일본어", "PP-OCRv6_medium_det", "PP-OCRv6_medium_rec",
            root => new(DetectorRoot: V6Root(root), RecognizerRoot: V6Root(root), RecognizerFolder: "rec")),
        Register("default", "PaddleOCR · 기존 고속", "기존 모델 경로와 고속 선택 정책 유지 · 영어/일본어", "기존 기본 정책", "기존 기본 정책",
            root => ModelCatalog.PreferredExecution(Root(root))),
        Register("v5-det-v6-medium-rec", "PaddleOCR · v5 검출 + v6 Medium 인식", "현재 기본 검출 + 다국어 v6 인식 · 별도 모델 필요", "기존 기본 정책", "PP-OCRv6_medium_rec",
            root => ModelCatalog.PreferredExecution(Root(root)) with { RecognizerRoot = V6Root(root), RecognizerFolder = "rec" })
    ];
    private static OcrModelPresetRegistration Register(string id, string name, string description, string detector, string recognizer, Func<string, OcrExecutionOptions> select)
    {
        OcrExecutionOptions Execution(string root) => select(root) with { ModelPresetId = id };
        selections.Add(id, Execution);
        PaddleModelSelection Selection(string root, OcrOptions options) => PaddleModelSelection.Resolve(Root(root), Execution(root), options.Language);
        OcrModelReadiness Check(string root, OcrOptions options)
        {
            try { var model = Selection(root, options); model.Detector.CheckPresence(); model.Recognizer.CheckPresence();
                return new(true, "로컬 모델 있음 · 첫 인식 때 SHA-256 및 ONNX 계약 검사"); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
                return new(false, $"{name}: 모델 또는 사전을 준비할 수 없습니다. {ex.Message} 자동 다운로드/대체하지 않습니다.");
            }
        }
        return new(new("paddle", id, name, ["en", "ja"], description, detector, recognizer),
            (root, _) => new PaddleOcrService(Root(root), Execution(root)), Check,
            (root, options, identity, comparison) => new PaddleOcrService(Root(root), Replay(root, options, identity, comparison, Execution(root))),
            (root, options) => Selection(root, options).Key);
    }
    // Replay resolves the saved identities, not whatever happens to be the preferred model today.
    internal static OcrExecutionOptions Replay(string root, OcrOptions options, OcrRunIdentity identity, string? comparison, OcrExecutionOptions? selected = null)
    {
        if (identity.EngineId != "paddle" || options.EngineId != "paddle" || options.ModelPresetId != identity.ModelPresetId)
            throw new InvalidDataException("QA 엔진/모델 구성 식별자가 다릅니다.");
        if (identity.ImplementationVersion != OcrAlgorithmRevision.Current || !identity.FlushDenormalsToZero)
            throw new InvalidDataException("저장 당시 OCR 구현/부동소수점 정책을 이 버전에서 정확하게 재현할 수 없습니다.");
        var legacy = Root(root);
        PaddleModelLocation Find(IEnumerable<PaddleModelLocation> locations, OcrModelIdentity expected)
        {
            foreach (var location in locations.Distinct()) {
                if (!File.Exists(Path.Combine(location.Root, location.Folder, "manifest.json"))) continue;
                if (OcrIdentity.From(ModelCatalog.Load(location.Root, location.Folder).Spec) == expected) return location;
            }
            throw new InvalidDataException("저장 당시 모델/사전/전처리 계약과 일치하는 모델이 없습니다.");
        }
        selected ??= selections.TryGetValue(options.ModelPresetId, out var select) ? select(root)
            : throw new OcrInitializationException("지원하지 않는 Paddle 모델 구성입니다.");
        var current = PaddleModelSelection.Resolve(legacy, selected, options.Language);
        bool LegacyLocation(PaddleModelLocation location) => Path.GetFullPath(location.Root).Equals(Path.GetFullPath(legacy), StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(location.Root).Equals(Path.GetFullPath(Path.Combine(legacy,"fast")), StringComparison.OrdinalIgnoreCase);
        IEnumerable<PaddleModelLocation> detLocations = LegacyLocation(current.Detector)
            ? [new(legacy,"det"), new(Path.Combine(legacy,"fast"),"det")] : [current.Detector];
        IEnumerable<PaddleModelLocation> recLocations = LegacyLocation(current.Recognizer)
            ? [new(legacy,options.Language), new(Path.Combine(legacy,"fast"),options.Language)] : [current.Recognizer];
        var det = comparison is null ? Find(detLocations, identity.Detector) : new PaddleModelLocation(comparison,"det");
        if (comparison is not null) ModelCatalog.Load(det.Root, det.Folder);
        var rec = Find(recLocations, identity.Recognizer);
        return new(det.Root, identity.AllowUpscale, identity.Threads, rec.Root, rec.Folder, options.ModelPresetId);
    }
}
