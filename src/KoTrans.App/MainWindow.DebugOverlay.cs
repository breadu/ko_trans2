using KoTrans.Infrastructure;
namespace KoTrans.App;

public partial class MainWindow
{
    private DebugTelemetry? debugTelemetry;
    internal DebugTelemetry? DebugDiagnostics => debugTelemetry;
    internal DebugOverlayWindow? DebugWindow { get; private set; }
    private DebugReason debugTargetState = DebugReason.Missing;
    private IntPtr debugTargetHandle;
    internal async Task EnableDebugOverlayAsync(string directory, bool show = true)
    {
        if (debugTelemetry is not null || closing) return;
        var store = new DebugOverlayStateStore(directory); var state = await store.LoadAsync();
        if (closing) return;
        debugTelemetry = new();
        DebugWindow = new(debugTelemetry, store, directory, state); // deliberately no Owner
        RefreshDebugContext();
        if (show) DebugWindow.Show();
    }
    private void RefreshDebugContext() => debugTelemetry?.SetContext(v => {
        var handle = target?.Handle ?? IntPtr.Zero; bool changed = handle != debugTargetHandle; debugTargetHandle = handle;
        return v with {
        CaptureMode = Current.CaptureMode.ToString(), RecognitionMode = Current.CaptureMode == CaptureMode.Clipboard ? "Clipboard" : RecognitionInputPlan.For(Current, true).WatchScreen ? "Continuous" : "Trigger",
        Target = Current.CaptureMode != CaptureMode.Window ? DebugReason.NotMeasured : target is not null ? DebugReason.Connected : debugTargetState,
        TargetChanges = v.TargetChanges + (changed ? 1 : 0), OverlayWindowVisible = overlay?.IsVisible == true,
        DisplayMode = Current.ShowOriginal && Current.ShowTranslation ? "Both" : Current.ShowOriginal ? "Original" : "Translation",
        TranslationBackend = Current.Translation.Provider == TranslationProvider.HyMtLocal ? LocalTranslationEnvironment.Runtime.Backend : "N/A" }; });
    private async Task<OcrResult> RecognizeDebugManualAsync(IOcrService service, ImageFrame frame, OcrOptions options, CancellationToken token)
    {
        var collector = debugTelemetry!;
        DebugTelemetry.EnableAttempts(service);
        long session = collector.BeginSession(options, Current.Translation, 0);
        collector.OcrStarted(session, DebugReason.Manual); var clock = System.Diagnostics.Stopwatch.StartNew();
        try {
            var result = await service.RecognizeAsync(frame, options, token);
            collector.OcrCompleted(session, result, clock.Elapsed.TotalMilliseconds);
            if (result.Lines.Count > 0) collector.Accept(session, false, false, false, false);
            else collector.Record(session, DebugCode.OCR_TEXT_REJECTED, DebugReason.EmptyText);
            return result;
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            collector.Record(session, DebugCode.OCR_TEXT_REJECTED, ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.OcrFailure,
                v => v with { LastError = ex is OperationCanceledException ? DebugReason.Cancelled : DebugReason.OcrFailure });
            collector.EndPending(session); throw;
        }
    }
}
