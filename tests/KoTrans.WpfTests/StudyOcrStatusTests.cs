using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class StudyOcrStatusTests
{
    internal static void StopWhileRunning()
    {
        var failures = new List<string>();
        foreach (string backend in new[] { "CPU", "DirectML", "Preparing" }) {
            using var fixture = new Fixture();
            var pipeline = fixture.Start(); var service = fixture.Services.Last(); service.Backend = backend;
            var study = fixture.Study();
            Check(service.Calls == 1 && !study.IsCompleted, "Study OCR was not held inside the shared service");
            string expected = Label(backend);
            Check(fixture.Model.OcrExecution == expected, "Study OCR did not report initial backend");
            int notices = fixture.Main.FeedbackCount;
            service.Queue(new("CPU", Reason: "request-timeout"));
            fixture.Stop();
            Check(!pipeline.IsRunning && !study.IsCompleted && !service.Disposed, "Stop assumed a cancellation request completed native work");
            Check(service.CancellationRequested == (backend != "CPU"), "The existing CPU/GPU stop cancellation policy changed");
            Console.WriteLine($"Study/translation-stop {backend}: pending={!study.IsCompleted}, cancel={service.CancellationRequested}, display={fixture.Model.OcrExecution}");
            if (fixture.Model.OcrExecution != expected) failures.Add(backend + ": " + fixture.Model.OcrExecution);
            Check(fixture.Main.FeedbackCount == notices, "Queued event from stopped translation produced fallback feedback");
            service.Release(); fixture.Drain(study);
            Check(fixture.Model.OcrExecution == "OCR: 대기", "Study completion did not return to idle");
            if (backend != "CPU") Until(() => service.Disposed, "Retired GPU service did not dispose after its operation drained");
        }
        Check(failures.Count == 0, "Pending study OCR falsely became idle: " + string.Join("; ", failures));
    }
    internal static void Boundaries()
    {
        OrdinaryStop(); OverlappingCancellation(false); OverlappingCancellation(true);
        ProfileChange(); Restart(); Shutdown(); FailureRetry(); GpuOptionChange();
    }
    // Real WPF option event, with a DirectML-capable synthetic OCR preset:
    // changing acceleration retires the current OCR session, but must not
    // pretend a still-running study OCR call has already returned.
    private static void GpuOptionChange()
    {
        foreach (string backend in new[] { "CPU", "DirectML", "Preparing" })
        {
            using var f = new Fixture(gpuOptionSupported: true);
            var active = f.Start();
            var service = f.Services.Last();
            service.Backend = backend;
            var pending = f.Study();
            var option = Named<CheckBox>(f.Main, "OcrGpuAcceleration");
            Check(option.IsEnabled && option.IsChecked != true
                && f.Model.OcrExecution == Label(backend) && !pending.IsCompleted,
                "GPU option fixture did not start with a valid running OCR call");
            var stale = service.Snapshot();
            option.IsChecked = true; // Calls OcrGpuAccelerationChanged; not a direct state mutation.
            Check(f.Profile.OcrGpuAcceleration && !active.IsRunning && !pending.IsCompleted
                && f.Model.OcrExecution == Label(backend),
                "GPU option change falsely reported idle while study OCR was still pending: " + backend);
            int feedback = f.Main.FeedbackCount;
            stale?.Invoke(new("CPU", Reason: "late-fallback"));
            service.Queue(new("Error"));
            Pump();
            Check(f.Model.OcrExecution == Label(backend) && f.Main.FeedbackCount == feedback,
                "Retired OCR events changed the status or displayed an obsolete fallback");
            service.Release();
            f.Drain(pending);
            Check(f.Model.OcrExecution == "OCR: 대기",
                "Finished study OCR did not return to idle after GPU option change");
            Until(() => service.Disposed, "GPU option change did not retire the old service after the call returned");
        }
        using var idle = new Fixture(gpuOptionSupported: true);
        var toggle = Named<CheckBox>(idle.Main, "OcrGpuAcceleration");
        Check(toggle.IsEnabled && idle.Model.OcrExecution == "OCR: 대기",
            "GPU option must be available and idle before any OCR starts");
        toggle.IsChecked = true;
        Check(idle.Profile.OcrGpuAcceleration && idle.Model.OcrExecution == "OCR: 대기"
            && idle.Model.OcrExecutionDetail.Contains("다음 시작 시 GPU"),
            "Idle GPU option change lost the next-start guidance");
        toggle.IsChecked = false;
        Check(!idle.Profile.OcrGpuAcceleration && idle.Model.OcrExecution == "OCR: 대기"
            && idle.Model.OcrExecutionDetail.Contains("다음 시작 시 CPU"),
            "Returning to CPU mode lost the idle guidance");
    }
    private static void OrdinaryStop()
    {
        foreach (string backend in new[] { "CPU", "DirectML", "Preparing" }) {
            using var f = new Fixture(); var active = f.Start(); var service = f.Services.Last();
            service.Signal(new(backend)); Pump(); var stale = service.Snapshot(); int notices = f.Main.FeedbackCount;
            f.Stop();
            Check(!active.IsRunning && f.Model.OcrExecution == "OCR: 대기", "Normal stop without study changed");
            stale?.Invoke(new("CPU", Reason: "late-fallback")); service.Signal(new("Error")); Pump();
            Check(f.Model.OcrExecution == "OCR: 대기" && notices == f.Main.FeedbackCount, "Stopped translation event revived idle/error/feedback");
        }
    }
    private static void OverlappingCancellation(bool newestFirst)
    {
        using var f = new Fixture(); f.Start(); var service = f.Services.Last(); var older = f.Study(); f.Stop();
        var stale = service.Snapshot(); f.CancelStudy(); int notices = f.Main.FeedbackCount;
        Check(service.CancellationRequested && !older.IsCompleted && !service.Disposed, "Cancel request was mistaken for completed study");
        stale?.Invoke(new("Error")); service.Signal(new("Error")); Pump();
        Check(f.Model.OcrExecution == "OCR: CPU" && notices == f.Main.FeedbackCount, "Canceled study event changed its frozen drain status");
        service.Backend = "DirectML"; var newer = f.Study();
        Check(f.Services.Count == 1 && !newer.IsCompleted && f.Model.OcrExecution == "OCR: GPU", "New study did not share the CPU session or show its current backend");
        if (newestFirst) {
            service.Release(1); f.Drain(newer);
            Check(!older.IsCompleted && f.Model.OcrExecution == "OCR: CPU", "Completing new study hid the older draining operation");
            service.Release(0); f.Drain(older);
        } else {
            service.Release(0); f.Drain(older);
            Check(!newer.IsCompleted && f.Model.OcrExecution == "OCR: GPU", "Old study finally hid newer study activity");
            service.Release(1); f.Drain(newer);
        }
        Check(older.IsCanceled && newer.IsCompletedSuccessfully && f.Model.OcrExecution == "OCR: 대기", "Study results/cancellation or final idle changed");
        service.Signal(new("Error")); Pump(); Check(f.Model.OcrExecution == "OCR: 대기", "Completed study remained subscribed");
    }
    private static void ProfileChange()
    {
        using var f = new Fixture(); f.Start(); var oldService = f.Services.Last(); oldService.Backend = "DirectML";
        var old = f.Study(); var stale = oldService.Snapshot();
        var game = f.Settings.CreateGameProfile("Next"); game.CaptureAreaConfigured = true;
        Complete(f.Main.SwitchProfileAsync(game));
        Check(oldService.CancellationRequested && !old.IsCompleted && !oldService.Disposed && f.Model.OcrExecution == "OCR: GPU", "Profile change assumed study/native disposal had finished");
        f.Start(); var next = f.Services.Last();
        Check(!ReferenceEquals(next, oldService) && f.Model.OcrExecution == "OCR: CPU", "New profile rebound a retired service");
        int notices = f.Main.FeedbackCount; stale?.Invoke(new("CPU", Reason: "late-fallback")); oldService.Signal(new("Error")); Pump();
        Check(f.Model.OcrExecution == "OCR: CPU" && notices == f.Main.FeedbackCount, "Old profile event replaced current status");
        oldService.Release(); f.Drain(old); Until(() => oldService.Disposed, "Profile switch did not eventually dispose old service");
        Check(f.Model.OcrExecution == "OCR: CPU", "Old study completion hid active new profile");
        f.Stop(); Check(f.Model.OcrExecution == "OCR: 대기", "New profile did not stop normally");
    }
    private static void Restart()
    {
        using var f = new Fixture(); f.Start(); var oldService = f.Services.Last(); oldService.Backend = "DirectML";
        var old = f.Study(); var stale = oldService.Snapshot();
        var overlay = Track(f.Main.CreateOverlayWindow()); Track(overlay.Chrome);
        f.Main.PresentStartingOverlayOverride = _ => { };
        var restart = f.Main.StartTranslationAsync(false); Complete(restart);
        Check(restart.Result && !old.IsCompleted && oldService.CancellationRequested && f.Services.Count == 2,
            "Actual restart changed cancellation/service selection or waited for unrelated native drain");
        var next = f.Services.Last(); next.Signal(new("DirectML", "New GPU")); Pump();
        stale?.Invoke(new("CPU", Reason: "late-fallback")); oldService.Release(); f.Drain(old);
        Check(f.Model.OcrExecution == "OCR: GPU", "Old completion/fallback overwrote restarted OCR");
        Until(() => oldService.Disposed, "Restart retired service leaked");
        Click(Button(f.Main, "취소")); Check(f.Model.OcrExecution == "OCR: 대기", "Restarted work did not stop");
    }
    private static void Shutdown()
    {
        using var f = new Fixture(); f.Start(); var service = f.Services.Last(); var pending = f.Study();
        var stale = service.Snapshot(); bool closed = false; f.Main.Closed += (_, _) => closed = true;
        f.Main.Close(); Pump();
        Check(service.CancellationRequested && !pending.IsCompleted && !service.Disposed && !closed
            && f.Model.OcrExecution == "OCR: CPU", "Shutdown skipped real native/study drain");
        stale?.Invoke(new("Error")); Pump(); Check(f.Model.OcrExecution == "OCR: CPU", "Closing study accepted stale state");
        service.Release(); f.Drain(pending); Until(() => closed, "Shutdown did not finish after study drain");
        Check(service.Disposed && service.DisposeCalls == 1 && f.Model.OcrExecution == "OCR: 대기", "Shutdown leaked/disposed early or left activity latched");
    }
    private static void FailureRetry()
    {
        using var f = new Fixture(); f.Start(); var service = f.Services.Last(); var failed = f.Study(); f.Stop();
        service.Fail(0); f.Drain(failed);
        Check(failed.Exception?.GetBaseException() is OcrInitializationException && f.Model.OcrExecution == "OCR: 오류", "Study model failure did not persist after drain");
        foreach (string backend in new[] { "CPU", "DirectML" }) {
            service.Backend = backend; var retry = f.Study();
            Check(f.Model.OcrExecution == Label(backend) && !retry.IsCompleted, "Study retry retained an obsolete model error");
            service.Signal(new("CPU", Reason: "worker-timeout")); Pump();
            Check(f.Model.OcrExecution == "OCR: CPU (GPU 전환 실패)" && f.Main.FeedbackToast.Message.Contains("CPU OCR"), "Independent study lost CPU fallback guidance");
            service.Release(); f.Drain(retry); Check(retry.IsCompletedSuccessfully && f.Model.OcrExecution == "OCR: 대기", "Study retry did not return idle");
        }
    }
    private static string Label(string backend) => backend == "Preparing" ? "OCR: GPU 준비 중" : backend == "DirectML" ? "OCR: GPU" : "OCR: CPU";
    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = FeedbackTests.DirectoryPath();
        internal readonly List<Ocr> Services = [];
        internal readonly AppSettings Settings = new();
        internal readonly MainWindow Main;
        internal MainViewModel Model => (MainViewModel)Main.DataContext;
        internal Profile Profile => Settings.Profiles.Single(p => p.Id == Settings.ActiveProfileId);
        private readonly List<Task<string>> studies = [];
        private readonly List<CancellationTokenSource> cancellations = [];
        internal Fixture(bool gpuOptionSupported = false)
        {
            Settings.Validate();
            string engineId = gpuOptionSupported ? "paddle" : "study-status";
            Profile.OcrEngineId = engineId; Profile.OcrModelPresetId = "default";
            Profile.CaptureMode = CaptureMode.Region; Profile.CaptureAreaConfigured = true; Profile.ShowTranslation = false;
            var descriptor = new OcrEngineDescriptor(engineId, "Mock OCR", "v1", ["en"], false, false, OcrCapabilities.Basic, false, true, "Synthetic");
            var registration = new OcrEngineRegistration(descriptor, _ => { var service = new Ocr(); Services.Add(service); return service; }, (_, _) => new(true, "Synthetic ready"));
            Main = Track(new MainWindow(new SettingsStore(DirectoryPath), new ApiKeyStore(DirectoryPath), () => [], ocrFactory: new(new([registration]), DirectoryPath)));
            Main.ApplyLoadedSettings(Settings);
        }
        internal TranslationPipeline Start()
        {
            var engine = Main.CurrentOcrService();
            var pipeline = Main.CreateProfilePipeline(engine, null, _ => throw new Exception("No actual capture expected"));
            Main.ObserveTranslation(pipeline, Profile.Id, watch: true); pipeline.Start(false); Pump(); return pipeline;
        }
        internal Task<string> Study()
        {
            var cancellation = new CancellationTokenSource(); cancellations.Add(cancellation);
            Field("studyCancellation").SetValue(Main, cancellation);
            var task = Main.RecognizeStudyFrameAsync(Profile, new(40, 20, 160, Enumerable.Repeat((byte)(studies.Count + 1), 3200).ToArray()), cancellation.Token);
            studies.Add(task);
            // RunStudyAsync consumes source cancellation and tracks the task for shutdown; exercise that same drain boundary.
            ((List<Task>)Field("studyTasks").GetValue(Main)!).Add(ConsumeCancellation(task));
            Pump(); return task;
        }
        internal void Stop() => Click(Named<Button>(Main, "WatchButton"));
        internal void CancelStudy() => typeof(MainWindow).GetMethod("CancelStudy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Main, ["Synthetic cancellation"]);
        internal void Drain(Task<string> task) { Until(() => task.IsCompleted, "Study OCR did not settle"); if (!task.IsCanceled && !task.IsFaulted) Check(task.Result == "synthetic source", "Study OCR result changed"); Pump(); }
        private static async Task ConsumeCancellation(Task task) { try { await task; } catch (Exception ex) when (ex is OperationCanceledException or OcrInitializationException) { } }
        private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose()
        {
            foreach (var service in Services) service.Release();
            foreach (var task in studies) Until(() => task.IsCompleted, "Fixture did not drain study OCR");
            Close(Main); foreach (var cancellation in cancellations) cancellation.Dispose(); Directory.Delete(DirectoryPath, true);
        }
    }
    private sealed class Ocr : IOcrService
    {
        private readonly List<TaskCompletionSource> calls = [];
        internal string Backend = "CPU"; internal int Calls => calls.Count;
        internal int DisposeCalls; internal bool Disposed, CancellationRequested;
        public OcrState State { get; private set; } = OcrState.Ready;
        public OcrExecutionStatus ExecutionStatus { get; private set; } = OcrExecutionStatus.Cpu;
        public event Action<OcrExecutionStatus>? ExecutionChanged;
        internal void Signal(OcrExecutionStatus status) { ExecutionStatus = status; ExecutionChanged?.Invoke(status); }
        internal void Queue(OcrExecutionStatus status) => ExecutionChanged?.Invoke(status);
        internal Action<OcrExecutionStatus>? Snapshot() => ExecutionChanged;
        internal void Release() { foreach (var call in calls) call.TrySetResult(); }
        internal void Release(int call) => calls[call].TrySetResult();
        internal void Fail(int call) { State = OcrState.Failed; Signal(new("Error")); calls[call].TrySetException(new OcrInitializationException("Synthetic study model failure")); }
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); calls.Add(gate);
            State = OcrState.Ready; Signal(new(Backend));
            using var registration = token.Register(() => CancellationRequested = true);
            await gate.Task; // Native inference may keep running after cancellation is requested.
            token.ThrowIfCancellationRequested();
            return new([new("synthetic source", 1, new(0, 0, 40, 20))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() { Disposed = true; DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
