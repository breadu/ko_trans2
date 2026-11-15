namespace KoTrans.Core;

// Explicit allowlist: no Profile serialization, paths, credentials or conversation history.
public sealed record GameQaProfile(CaptureMode CaptureMode, TranslationProvider Provider, string TranslationModel,
    bool ShowOriginal, double OverlayFontSize, double OverlayOpacity, bool OverlayClickThrough)
{
    public static GameQaProfile From(Profile profile) => new(profile.CaptureMode, profile.Translation.Provider,
        profile.Translation.Model, profile.ShowOriginal, profile.OverlayFontSize, profile.OverlayOpacity, profile.OverlayClickThrough);
}
public sealed record OcrModelIdentity(string Name, string Version, string Sha256, string DictionarySha256, string ContractSha256);
public sealed record OcrRunIdentity(OcrModelIdentity Detector, OcrModelIdentity Recognizer, bool AllowUpscale, int Threads)
{
    public string EngineId { get; init; } = "paddle";
    public string ModelPresetId { get; init; } = "default";
    public string ImplementationVersion { get; init; } = OcrAlgorithmRevision.Current;
    public IReadOnlyList<OcrModelIdentity> AdditionalModels { get; init; } = [];
    // Missing in old QA means the original IEEE/subnormal policy, never the new one.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FlushDenormalsToZero { get; init; }
}
public sealed record GameQaTranslation(string Status, string? Text = null, double ApiMs = 0);
public sealed record GameQaInput(int Width, int Height, FramePixelFormat PixelFormat, int SourceWidth, int SourceHeight, PixelRect? Crop);
public sealed record GameQaRecord(int SchemaVersion, Guid RequestId, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc,
    string OcrStatus, OcrOptions Options, GameQaProfile Profile, GameQaInput Input, OcrResult? Ocr,
    string? PipelineText, GameQaTranslation Translation, double? CaptureMs, string? InputSha256 = null, string? AlgorithmVersion = null)
{
    // Native OCR covers SearchArea. The prefix was verified unchanged in this
    // full input frame and reused, not inferred anew.
    public IReadOnlyList<OcrLine> NvlPrefix { get; init; } = [];
}
public static class OcrAlgorithmRevision { public const string Current = "ocr-layout-v25-dialogue-start-tolerance-20261115"; }
public sealed record GameQaSnapshot(ImageFrame Frame, GameQaRecord Record);
