using System.Security.Cryptography;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// Owned by one pipeline's serial OCR worker. No frames, files, or cross-profile
// state are retained. Match the entire request, including ROI and marker inputs.
internal sealed class OcrResultCache
{
    internal const int Capacity = 32;
    private readonly ExactOcrCache<OcrResult> results = new(Capacity);
    internal int Count => results.Count;
    internal OcrCacheMissReason LastMissReason {get;private set;}

    internal async Task<OcrResult> RecognizeAsync(IOcrService service, ImageFrame frame, OcrOptions options,
        CancellationToken token, bool bypass, Action onMiss)
    {
        token.ThrowIfCancellationRequested();
        bypass |= options.BypassOcrCaches;
        string input = $"{frame.Width}:{frame.Height}:{frame.Stride}:{frame.PixelFormat}:"
            + Convert.ToHexString(SHA256.HashData(frame.Pixels.Span));
        string conditions = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options with {BypassOcrCaches=false})));
        if (results.TryGet(input,conditions,bypass,out var cached,out var reason)) {
            token.ThrowIfCancellationRequested();
            // Preserve recognition evidence for current learning/removal, but do
            // not report old model work as fresh inference or body verification.
            return cached with { ReusedFromCache = true, RecognitionCache = null, InferenceTime = TimeSpan.Zero,
                Attempts = new(0, 0), ModelLoadTime = TimeSpan.Zero, DetectionTime = TimeSpan.Zero,
                PreprocessTime = TimeSpan.Zero, DetectorInferenceTime = TimeSpan.Zero,
                DetectionPostprocessTime = TimeSpan.Zero, MergeTime = TimeSpan.Zero,
                RecognitionTime = TimeSpan.Zero, PostprocessTime = TimeSpan.Zero, DetectionPasses = 0,
                MarkerBodyChecks = 0, MarkerBodyCacheHits = 0 };
        }
        LastMissReason=reason;
        onMiss();
        var result = await service.RecognizeAsync(frame, options with {BypassOcrCaches=bypass}, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // A forced read replaces a previous answer. Empty/failed reads must not
        // leave that answer available, or freeze a temporary recognition failure.
        results.Forget(input,conditions);
        if (result.Lines.Count > 0) {
            results.Remember(input,conditions,result with { ReusedFromCache = false });
        }
        return result;
    }

    internal void Clear() { results.Clear(); LastMissReason=OcrCacheMissReason.None; }
}
