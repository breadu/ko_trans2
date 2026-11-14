using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using KoTrans.Core;
using KoTrans.Ocr;
using Microsoft.ML.OnnxRuntime;

if (args.Length != 3 || !args[0].StartsWith("KoTrans2.Ocr.", StringComparison.Ordinal) || args[0].Length > 100
    || !int.TryParse(args[1], out int parentId) || !long.TryParse(args[2], out long parentStart)) return 2;
using var lifetime = new CancellationTokenSource();
using var parent = Process.GetProcessById(parentId);
if (parent.StartTime.ToUniversalTime().Ticks != parentStart) return 2;
// Parent death cannot leave a resident GPU process, even if native inference hangs.
_ = Task.Run(async () => { try { await parent.WaitForExitAsync(lifetime.Token); Environment.Exit(0); } catch (OperationCanceledException) { } });
using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
IOcrService? service = null;
try {
    using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await pipe.ConnectAsync(startup.Token);
    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverId) || serverId != parentId) return 2;
    string preset = "", configuredRoot = ""; OcrRunIdentity? savedReplay = null;
    while (pipe.IsConnected) {
        DirectMlRequest request;
        try { request = await DirectMlProtocol.ReadAsync<DirectMlRequest>(pipe, DirectMlProtocol.MaxHeader, lifetime.Token); }
        catch (EndOfStreamException) { break; }
        if (request.Id == Guid.Empty || request.Version != DirectMlProtocol.Version || !DirectMlProtocol.Supports(request.Options)) return 2;
        DirectMlReply response;
        try {
            if (request.Operation == "initialize" && service is null) {
                if (request.ModelsRoot is not { Length: > 0 and <= 32768 } root || !Path.IsPathFullyQualified(root)) return 2;
                // Missing/corrupt models are not GPU failures. Verify before touching DXGI/ORT.
                configuredRoot = root; savedReplay = request.ReplayIdentity;
                CheckModelFiles(root, request.Options, savedReplay);
                DirectMlDevice.Select();
                OrtEnv.Instance().DisableTelemetryEvents();
                DirectMlTrace.Enabled = request.Profile;
                var factory = new OcrServiceFactory(OcrEngineCatalog.Default, root);
                service = request.ReplayIdentity is { } replay
                    ? factory.CreateReplay(request.Options, replay) : factory.Create(request.Options with { UseDirectMl = false });
                var clock = Stopwatch.StartNew(); await service.InitializeAsync(request.Options, lifetime.Token);
                preset = request.Options.ModelPresetId;
                response = new(request.Id, DirectMlProtocol.Version, Device: DirectMlDevice.Selected.Name, DeviceId: DirectMlDevice.Selected.Id, LoadMs: clock.Elapsed.TotalMilliseconds);
            } else if (request.Operation == "recognize" && service is not null && preset == request.Options.ModelPresetId) {
                if (!new PixelRect(0, 0, request.Width, request.Height).IsValid || request.Stride < (long)request.Width * 4
                    || request.Bytes <= 0 || request.Bytes > DirectMlProtocol.MaxPixels || (long)request.Stride * request.Height != request.Bytes || !Enum.IsDefined(request.Format)) return 2;
                byte[] pixels = new byte[request.Bytes]; await pipe.ReadExactlyAsync(pixels, lifetime.Token);
                var frame = new ImageFrame(request.Width, request.Height, request.Stride, pixels, request.Format);
                DirectMlProtocol.ValidateInput(frame, request.Options);
                var clock = Stopwatch.StartNew(); var result = await service.RecognizeAsync(frame, request.Options, lifetime.Token);
                double elapsed = clock.Elapsed.TotalMilliseconds;
                response = new(request.Id, DirectMlProtocol.Version, Result: result, Device: DirectMlDevice.Selected.Name, DeviceId: DirectMlDevice.Selected.Id,
                    ProcessingMs: elapsed, ProfileFiles: DirectMlTrace.Finish());
            } else return 2;
        } catch (ModelFilesException) { response = new(request.Id, DirectMlProtocol.Version, "model"); }
        catch (ArgumentException) { response = new(request.Id, DirectMlProtocol.Version, "input"); }
        catch (OcrInitializationException ex) when (request.Operation == "recognize") {
            // Files can change after initialization. Recheck ONLY on failure so a
            // damaged/lost model is not misreported as a GPU fault/CPU retry.
            bool modelFailure = false;
            try { CheckModelFiles(configuredRoot, request.Options, savedReplay); } catch (ModelFilesException) { modelFailure = true; }
            response = modelFailure ? new(request.Id, DirectMlProtocol.Version, "model")
                : new(request.Id, DirectMlProtocol.Version, "gpu-runtime", ex.GetType().Name + ":" + ex.HResult.ToString("X8"));
        }
        catch (Exception ex) { response = new(request.Id, DirectMlProtocol.Version, "gpu-runtime", ex.GetType().Name + ":" + ex.HResult.ToString("X8")); }
        await DirectMlProtocol.WriteAsync(pipe, response, DirectMlProtocol.MaxResponse, lifetime.Token); await pipe.FlushAsync(lifetime.Token);
        if (response.Error.Length > 0) break;
    }
    return 0;
} catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return 1; }
finally { lifetime.Cancel(); if (service is not null) await service.DisposeAsync(); }

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

static void CheckModelFiles(string root, OcrOptions options, OcrRunIdentity? replay)
{
    try {
        if (!new OcrServiceFactory(OcrEngineCatalog.Default, root).CheckModels(options).Ready) throw new ModelFilesException();
        var execution = replay is not null ? PaddleModelPresets.Replay(root, options, replay, null) : PaddleModelPresets.Execution(root, options.ModelPresetId);
        var selected = PaddleModelSelection.Resolve(PaddleModelPresets.Root(root), execution, options.Language);
        ModelCatalog.Load(selected.Detector.Root, selected.Detector.Folder); ModelCatalog.Load(selected.Recognizer.Root, selected.Recognizer.Folder);
    } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OcrInitializationException) { throw new ModelFilesException(); }
}

sealed class ModelFilesException : Exception;
