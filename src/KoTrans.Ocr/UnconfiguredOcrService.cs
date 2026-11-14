using KoTrans.Core;
namespace KoTrans.Ocr;

// Explicitly unavailable in Phase 1. Never fabricate an OCR result.
public sealed class UnconfiguredOcrService : IOcrService
{
    public OcrState State => OcrState.NotConfigured;
    public Task InitializeAsync(OcrOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new NotSupportedException("PaddleOCR 추론 연결은 Phase 2에서 구현합니다."));
    }
    public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<OcrResult>(new NotSupportedException("OCR 모델이 연결되지 않았습니다."));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
