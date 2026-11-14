using System.IO;
using System.Reflection;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class OcrExecutionLifecycleTests
{
    internal static void Run()
    {
        string dir = FeedbackTests.DirectoryPath(); var services = new List<Ocr>(); bool failCreate = false;
        var descriptor = new OcrEngineDescriptor("status-fixture", "Mock OCR", "v1", ["en"], false, false, OcrCapabilities.Basic, false, true, "Synthetic");
        var entry = new OcrEngineRegistration(descriptor, _ => {
            if (failCreate) throw new OcrInitializationException("합성 모델 오류");
            var service = new Ocr(); services.Add(service); return service;
        }, (_, _) => new(true, "Synthetic ready"));
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
        profile.OcrEngineId = descriptor.Id; profile.OcrModelPresetId = "default"; profile.ShowTranslation = false;
        profile.CaptureMode = CaptureMode.Clipboard;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [], ocrFactory: new(new([entry]), dir)));
        main.ApplyLoadedSettings(settings); var model = (MainViewModel)main.DataContext;
        main.PresentStartingOverlayOverride = _ => Check(model.OcrExecution == "OCR: 대기", "Start preparation falsely reports active CPU");
        try {
            failCreate = true;
            var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
            var start = main.StartTranslationAsync(false); Complete(start);
            Check(!start.Result && model.OcrExecution == "OCR: 오류", "Factory/model failure became normal idle CPU");
            failCreate = false; start = main.StartTranslationAsync(false); Complete(start);
            Check(start.Result && model.OcrExecution == "OCR: 대기", "Clipboard-only session falsely reports active OCR or retains old model error");
            Click(Button(main, "취소"));
            main.CurrentOcrService(); var service = services.Last();
            Check(model.OcrExecution == "OCR: 대기", "Creating a service falsely reports active OCR");
            SetFrame(main);
            foreach (string backend in new[] { "CPU", "DirectML" }) {
                service.Backend = backend; service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Click(Button(main, "OCR 실행"));
                Check(model.OcrExecution == (backend == "CPU" ? "OCR: CPU" : "OCR: GPU"), "Manual OCR does not show its active backend");
                service.Gate.SetResult(); Until(() => model.OcrExecution == "OCR: 대기", "Manual completion did not return idle");
            }
            Click(Button(main, "취소")); main.CurrentOcrService(); service = services.Last(); SetFrame(main);
            service.Backend = "Preparing"; service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(Button(main, "OCR 실행")); Check(model.OcrExecution == "OCR: GPU 준비 중", "GPU preparation missing");
            // Queue a worker event before stop, then deliver it after observation invalidation.
            service.QueueEvent(new("CPU", Reason: "request-timeout"));
            int notices = main.FeedbackCount;
            Click(Button(main, "취소")); service.Gate.TrySetResult(); Pump();
            Check(model.OcrExecution == "OCR: 대기" && main.FeedbackCount == notices, "Stopped GPU work resurrected CPU fallback/status toast");
            Until(() => service.Disposed, "Stopped GPU session did not drain");
            service.Signal(new("Error")); Pump();
            Check(model.OcrExecution == "OCR: 대기", "Retired service event changed idle state");
            main.CurrentOcrService(); service = services.Last(); SetFrame(main);
            service.Fail = true; Click(Button(main, "OCR 실행"));
            Until(() => model.OcrExecution == "OCR: 오류", "Manual model failure was hidden");
            service.Fail = false; service.Backend = "CPU"; service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(Button(main, "OCR 실행")); Check(model.OcrExecution == "OCR: CPU", "CPU retry retained model error");
            service.Signal(new("CPU", Reason: "request-timeout")); Pump();
            Check(model.OcrExecution == "OCR: CPU (GPU 전환 실패)" && main.FeedbackCount == notices + 1, "Active fallback notification lost");
            service.Signal(new("DirectML", "Synthetic GPU")); Pump(); Check(model.OcrExecution == "OCR: GPU", "GPU recovery label missing");
            service.Gate.SetResult(); Until(() => model.OcrExecution == "OCR: 대기", "Successful retry did not settle");
            // Leave a CPU session selected: RunOcr intentionally retires the previous GPU session.
            service.Signal(new("CPU")); service.Backend = "CPU"; service.InitialFallback = true;
            service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); int beforeInitial = main.FeedbackCount;
            Click(Button(main, "OCR 실행")); Pump();
            Check(model.OcrExecution == "OCR: CPU (GPU 전환 실패)" && main.FeedbackCount == beforeInitial + 1,
                "Fallback raised synchronously at OCR entry lost its user notice");
            service.Gate.SetResult(); Until(() => model.OcrExecution == "OCR: 대기", "Immediate-fallback OCR did not settle");
        } finally {
            foreach (var service in services) service.Gate?.TrySetResult(); Close(main); Directory.Delete(dir, true);
        }
    }
    private static void SetFrame(MainWindow main) => typeof(MainWindow).GetField("lastFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(main, new ImageFrame(40, 20, 160, new byte[3200]));
    private sealed class Ocr : IOcrService
    {
        internal TaskCompletionSource? Gate; internal string Backend = "CPU"; internal bool Fail, Disposed, InitialFallback;
        public OcrState State { get; private set; } = OcrState.Ready;
        public OcrExecutionStatus ExecutionStatus { get; private set; } = OcrExecutionStatus.Cpu;
        public event Action<OcrExecutionStatus>? ExecutionChanged;
        internal void Signal(OcrExecutionStatus status) { ExecutionStatus = status; ExecutionChanged?.Invoke(status); }
        internal void QueueEvent(OcrExecutionStatus status) => ExecutionChanged?.Invoke(status);
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) {
            State = Fail ? OcrState.Failed : OcrState.Ready; Signal(new(Fail ? "Error" : Backend, Reason: InitialFallback ? "worker-start-failed" : ""));
            if (Fail) throw new OcrInitializationException("합성 모델 오류");
            if (Gate is not null) await Gate.Task.WaitAsync(token);
            return new([new("synthetic", 1, new(0, 0, 40, 20))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
