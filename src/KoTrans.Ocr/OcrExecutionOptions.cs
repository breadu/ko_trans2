namespace KoTrans.Ocr;

// Diagnostic overrides, not additional end-user settings. Production keeps the measured safe default.
public sealed record OcrExecutionOptions(string? DetectorRoot = null, bool AllowUpscale = true, int Threads = 0, string? RecognizerRoot = null, string? RecognizerFolder = null, string ModelPresetId = "default")
{
    public int ThreadCount => Threads == 0 ? Math.Clamp(Environment.ProcessorCount / 2, 1, 4) : Threads;
    public void Validate() { if (Threads is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(Threads)); }
    public static (int Width, int Height) DetectionSize(int width, int height, int longSide, bool upscale)
    {
        if (width <= 0 || height <= 0 || longSide is < 32 or > 1920) throw new ArgumentOutOfRangeException(nameof(width));
        double ratio = longSide / (double)Math.Max(width, height);
        if (!upscale) ratio = Math.Min(1, ratio);
        return (Math.Max(32, (int)Math.Round(width * ratio / 32) * 32), Math.Max(32, (int)Math.Round(height * ratio / 32) * 32));
    }
}
