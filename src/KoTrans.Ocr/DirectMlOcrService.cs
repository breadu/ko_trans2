using KoTrans.Core;

namespace KoTrans.Ocr;

internal sealed class DirectMlFailureException(string reason, Exception? inner = null) : Exception(reason, inner);

// One instance per profile/model/translation session. A GPU failure is sticky until
// a new explicit translation start; ordinary frame requests never restart a failed GPU.
internal sealed class DirectMlOcrService(Func<IOcrService> createCpu, Func<IOcrService> createGpu) : IOcrService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IOcrService? cpu, gpu;
    private bool failed, disposed;
    public bool CollectAttemptDiagnostics { private get; set; }
    public OcrState State => ExecutionStatus.Backend == "Error" ? OcrState.Failed : (failed ? cpu : gpu)?.State ?? OcrState.NotConfigured;
    public OcrExecutionStatus ExecutionStatus { get; private set; } = new("Preparing");
    public event Action<OcrExecutionStatus>? ExecutionChanged;
    private void Report(OcrExecutionStatus status) { if (ExecutionStatus == status) return; ExecutionStatus = status; ExecutionChanged?.Invoke(status); }
    public async Task InitializeAsync(OcrOptions options, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!failed) {
                try { gpu ??= createGpu(); await gpu.InitializeAsync(options, token).ConfigureAwait(false); Report(gpu.ExecutionStatus); return; }
                catch (DirectMlFailureException ex) { token.ThrowIfCancellationRequested(); await FallBack(ex).ConfigureAwait(false); }
                catch (OcrInitializationException) { Report(new("Error", Reason: "model-validation")); throw; }
            }
            token.ThrowIfCancellationRequested(); cpu ??= createCpu();
            await cpu.InitializeAsync(options with { UseDirectMl = false }, token).ConfigureAwait(false);
        } finally { gate.Release(); }
    }
    public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
    {
        // Bad input and explicit cancellation are never classified as GPU failures.
        DirectMlProtocol.ValidateInput(frame, options);
        await gate.WaitAsync(token).ConfigureAwait(false);
        int gpuAttempts = 0;
        try {
            ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
            if (!failed) {
                try {
                    gpu ??= createGpu(); gpuAttempts++; var result = await gpu.RecognizeAsync(frame, options, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); Report(gpu.ExecutionStatus); return CollectAttemptDiagnostics ? result with { Attempts = new(gpuAttempts, 0) } : result;
                } catch (DirectMlFailureException ex) { token.ThrowIfCancellationRequested(); await FallBack(ex).ConfigureAwait(false); }
                catch (OcrInitializationException) { Report(new("Error", Reason: "model-validation")); throw; }
            }
            token.ThrowIfCancellationRequested(); cpu ??= createCpu();
            // Exactly one retry of this frame. AI/context/deduplication stay in the
            // existing pipeline; no result or AI request escapes the failed GPU call.
            var recovered = await cpu.RecognizeAsync(frame, options with { UseDirectMl = false }, token).ConfigureAwait(false);
            return recovered with { Execution = ExecutionStatus, Attempts = CollectAttemptDiagnostics ? new(gpuAttempts, 1) : null };
        } finally { gate.Release(); }
    }
    private async Task FallBack(DirectMlFailureException error)
    {
        failed = true;
        if (gpu is { } old) {
            gpu = null;
            try { await old.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* GPU teardown must not disable working CPU OCR. */ }
        }
        Report(new("CPU", Reason: error.Message));
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            if (disposed) return; disposed = true;
            if (gpu is not null) await gpu.DisposeAsync().ConfigureAwait(false);
            if (cpu is not null) await cpu.DisposeAsync().ConfigureAwait(false);
        } finally { gate.Release(); }
    }
}
