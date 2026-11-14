namespace KoTrans.Core;

public enum OcrState { NotConfigured, Loading, Ready, Failed }
public sealed record OcrOptions(string Language, string ReadingMode, bool Vertical, bool SuppressFurigana = true, PixelRect? SearchArea = null, int DetectionLongSide = 0, bool KeepLocalParagraph = false, string EngineId = "paddle", string ModelPresetId = "default", bool UseDirectMl = false, bool CollectMarkerDiagnostics = false, IReadOnlyList<GameMarkerPattern>? MarkerPatterns = null,bool CollectMarkerLearning=false,TerminalMarkerReference? MarkerReference=null,bool BypassOcrCaches=false);
public sealed record OcrLine(string Text, float Confidence, PixelRect Bounds, bool Vertical = false, int Paragraph = 0);
public sealed record OcrPadding(int Left, int Top, int Right, int Bottom);
public sealed record OcrAttemptCounts(int Gpu, int Cpu);
public sealed record OcrCharacter(string Text, float Confidence, PixelRect Bounds, int Line, bool Vertical);
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, TimeSpan InferenceTime)
{
    public bool ReusedFromCache { get; init; }
    public OcrRecognitionCacheStats? RecognitionCache {get;init;}
    public OcrAttemptCounts? Attempts { get; init; }
    public IReadOnlyList<OcrMarkerObservation>? MarkerDiagnostics { get; init; }
    public int MarkerObservations { get; init; }
    public int LearnedMarkerExclusions { get; init; }
    public int MarkerBodyChecks {get;init;}
    public int MarkerBodyCacheHits {get;init;}
    public IReadOnlyList<TerminalMarkerSample>? TerminalMarkers {get;init;}
    public OcrExecutionStatus Execution { get; init; } = OcrExecutionStatus.Cpu;
    public IReadOnlyList<OcrLine>? Candidates { get; init; }
    public IReadOnlyList<OcrCharacter> Characters { get; init; } = [];
    public OcrRunIdentity? Models { get; init; }
    public TimeSpan ModelLoadTime { get; init; }
    public TimeSpan DetectionTime { get; init; }
    public TimeSpan PreprocessTime { get; init; }
    public TimeSpan DetectorInferenceTime { get; init; }
    public TimeSpan DetectionPostprocessTime { get; init; }
    public TimeSpan MergeTime { get; init; }
    public int DetectorWidth { get; init; }
    public int DetectorHeight { get; init; }
    public TimeSpan RecognitionTime { get; init; }
    public TimeSpan PostprocessTime { get; init; }
    public int DetectionPasses { get; init; } = 1;
    public OcrPadding? DetectorPadding { get; init; }
    public int DetectedRegions { get; init; }
    public IReadOnlyList<PixelRect> DetectedBoxes { get; init; } = [];
    public int ReusedLines { get; init; }
    public PixelRect? IncrementalArea { get; init; }
    public string Text => string.Join("\n\n", Lines.GroupBy(l => l.Paragraph).Select(p => string.Join("\n", p.Select(l => l.Text))));
}

public interface IOcrService : IAsyncDisposable
{
    bool CollectAttemptDiagnostics { set { } }
    OcrExecutionStatus ExecutionStatus => OcrExecutionStatus.Cpu;
    event Action<OcrExecutionStatus>? ExecutionChanged { add { } remove { } }
    OcrCapabilities Capabilities => OcrCapabilities.Paddle;
    OcrState State { get; }
    Task InitializeAsync(OcrOptions options, CancellationToken cancellationToken);
    Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken cancellationToken);
}

public sealed record OcrExecutionStatus(string Backend, string Device = "", string Reason = "", string DeviceId = "")
{
    public static OcrExecutionStatus Cpu { get; } = new("CPU");
    public bool IsFallback => Backend == "CPU" && Reason.Length > 0;
    public string DisplayText => Backend == "DirectML" ? $"OCR: GPU 실행 중 (DirectML · {Device})"
        : Backend == "Preparing" ? "OCR: GPU 준비 중 (DirectML)"
        : Backend == "Error" ? "OCR: 모델 오류 · 파일을 확인하세요"
        : IsFallback ? "OCR: CPU 실행 중 (GPU 가속 실패)" : "OCR: CPU 실행 중";
}

// Sanitized model setup failures, distinct from an AI provider or capture failure.
public sealed class OcrInitializationException(string message) : Exception(message);
