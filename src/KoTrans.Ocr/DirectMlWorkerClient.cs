using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Ocr;

// Only starts our app-local executable, never searches PATH or an installed service.
internal sealed class DirectMlWorkerClient(string modelsRoot, DirectMlWorkerConfiguration configuration, OcrRunIdentity? replayIdentity = null) : IOcrService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeServerStream? pipe;
    private Process? child;
    private bool initialized, disposed;
    public OcrState State => initialized ? OcrState.Ready : OcrState.NotConfigured;
    public OcrExecutionStatus ExecutionStatus { get; private set; } = new("Preparing");
    internal int? ProcessId => child?.Id;
    internal DirectMlReply? LastReply { get; private set; }
    internal double LastRoundTripMs { get; private set; }
    public async Task InitializeAsync(OcrOptions options, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await InitializeCore(options, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task InitializeCore(OcrOptions options, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
        if (!DirectMlProtocol.Supports(options)) throw new ArgumentException("이 OCR 모델은 GPU 가속을 지원하지 않습니다.");
        if (initialized) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(configuration.StartupTimeout ?? TimeSpan.FromSeconds(90));
        try {
            string executable = Path.GetFullPath(configuration.Executable ?? Path.Combine(AppContext.BaseDirectory, "ocr-worker", "KoTrans.OcrWorker.exe"));
            if (!File.Exists(executable)) throw new DirectMlFailureException("worker-missing");
            string name = "KoTrans2.Ocr." + Guid.NewGuid().ToString("N");
            pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardError = true, RedirectStandardOutput = true };
            using var parent = Process.GetCurrentProcess();
            start.ArgumentList.Add(name); start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            child = Process.Start(start) ?? throw new DirectMlFailureException("worker-start");
            // Drain native diagnostics without retaining text, model paths or OCR input.
            child.OutputDataReceived += (_, _) => { }; child.ErrorDataReceived += (_, _) => { };
            child.BeginOutputReadLine(); child.BeginErrorReadLine();
            var connection = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = child.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connection, exited).ConfigureAwait(false) == exited) {
                await exited.ConfigureAwait(false); throw new DirectMlFailureException("worker-exited-before-connect");
            }
            await connection.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) || pid != child.Id)
                throw new DirectMlFailureException("worker-peer");
            var request = new DirectMlRequest(Guid.NewGuid(), DirectMlProtocol.Version, "initialize", options,
                ModelsRoot: Path.GetFullPath(modelsRoot), Profile: configuration.Profile, ReplayIdentity: replayIdentity);
            var reply = await Exchange(request, null, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply.Device) || string.IsNullOrWhiteSpace(reply.DeviceId)) throw new DirectMlFailureException("device-contract");
            initialized = true; ExecutionStatus = new("DirectML", reply.Device, DeviceId: reply.DeviceId);
        } catch (Exception ex) {
            await StopChild().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (ex is OcrInitializationException or ArgumentException or DirectMlFailureException) throw;
            throw new DirectMlFailureException(timeout.IsCancellationRequested ? "startup-timeout" : "worker-start-or-transport", ex);
        }
    }
    public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
    {
        DirectMlProtocol.ValidateInput(frame, options);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            await InitializeCore(options, token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(configuration.RequestTimeout ?? TimeSpan.FromSeconds(30));
            try {
                var request = new DirectMlRequest(Guid.NewGuid(), DirectMlProtocol.Version, "recognize", options,
                    Width: frame.Width, Height: frame.Height, Stride: frame.Stride, Bytes: frame.Pixels.Length, Format: frame.PixelFormat);
                var reply = await Exchange(request, frame, timeout.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var result = reply.Result ?? throw new DirectMlFailureException("missing-result");
                ValidateResult(result, frame, options);
                return result with { Execution = ExecutionStatus };
            } catch (Exception ex) {
                await StopChild().ConfigureAwait(false); token.ThrowIfCancellationRequested();
                if (ex is OcrInitializationException or ArgumentException or DirectMlFailureException) throw;
                throw new DirectMlFailureException(timeout.IsCancellationRequested ? "request-timeout" : "worker-runtime-or-transport", ex);
            }
        } finally { gate.Release(); }
    }
    private async Task<DirectMlReply> Exchange(DirectMlRequest request, ImageFrame? frame, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        await DirectMlProtocol.WriteAsync(pipe!, request, DirectMlProtocol.MaxHeader, token).ConfigureAwait(false);
        if (frame is not null) await pipe!.WriteAsync(frame.Pixels, token).ConfigureAwait(false);
        await pipe!.FlushAsync(token).ConfigureAwait(false);
        var reply = await DirectMlProtocol.ReadAsync<DirectMlReply>(pipe!, DirectMlProtocol.MaxResponse, token).ConfigureAwait(false);
        LastRoundTripMs = clock.Elapsed.TotalMilliseconds; LastReply = reply;
        if (configuration.Observe is { } observe && child is not null) {
            child.Refresh(); observe(new(child.Id, LastRoundTripMs, reply, child.PrivateMemorySize64, child.HandleCount));
        }
        if (reply.Id != request.Id || reply.Version != DirectMlProtocol.Version) throw new DirectMlFailureException("response-id-or-version");
        if (reply.Error == "model") throw new OcrInitializationException("OCR 모델·사전 파일을 검증할 수 없습니다. 정상 models 파일을 복원하세요.");
        if (reply.Error == "input") throw new ArgumentException("OCR 입력 또는 옵션이 올바르지 않습니다.");
        if (reply.Error.Length > 0) throw new DirectMlFailureException(reply.Error + ":" + reply.Detail);
        return reply;
    }
    internal static void ValidateResult(OcrResult result, ImageFrame frame, OcrOptions options)
    {
        bool Box(PixelRect r) => r.IsValid && r.X >= 0 && r.Y >= 0 && (long)r.X + r.Width <= frame.Width && (long)r.Y + r.Height <= frame.Height;
        bool Line(OcrLine l) => l.Text is { Length: <= 100_000 } && float.IsFinite(l.Confidence) && l.Confidence is >= 0 and <= 1 && Box(l.Bounds);
        if (result.Models is not { } model || model.EngineId != options.EngineId || model.ModelPresetId != options.ModelPresetId
            || model.ImplementationVersion != OcrAlgorithmRevision.Current || !model.FlushDenormalsToZero
            || result.Lines is null || result.Lines.Count > 4096 || !result.Lines.All(Line)
            || result.Candidates is { } candidates && (candidates.Count > 4096 || !candidates.All(Line))
            || result.Characters is null || result.Characters.Count > 100_000
            || result.Characters.Any(c => c.Text is null || c.Text.Length > 32 || !float.IsFinite(c.Confidence) || c.Confidence is < 0 or > 1
                || c.Line < 0 || c.Line >= result.Lines.Count || !Box(c.Bounds))
            || result.DetectedBoxes is null || result.DetectedBoxes.Count > 4096 || !result.DetectedBoxes.All(Box)
            || result.InferenceTime < TimeSpan.Zero
            || result.RecognitionCache is {} stats && (stats.Inferences is <0 or >20000 || stats.Hits is <0 or >20000
                || !Enum.IsDefined(stats.LastMiss) || stats.Misses is null
                || stats.Misses.Empty is <0 or >20000 || stats.Misses.InputNotCached is <0 or >20000
                || stats.Misses.ConditionsChanged is <0 or >20000 || stats.Misses.Evicted is <0 or >20000 || stats.Misses.Bypassed is <0 or >20000
                || stats.Misses.Empty+stats.Misses.InputNotCached+stats.Misses.ConditionsChanged+stats.Misses.Evicted+stats.Misses.Bypassed!=stats.Inferences))
            throw new DirectMlFailureException("output-contract");
    }
    private async Task StopChild()
    {
        initialized = false; pipe?.Dispose(); pipe = null;
        if (child is not { } owned) return; child = null;
        try {
            // Closing the pipe normally exits the worker. A stuck native Run cannot
            // be interrupted safely in-process; terminate ONLY this Process object.
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await owned.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!owned.HasExited) { owned.Kill(); await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } }
        } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
        finally { owned.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; await StopChild().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);
}
