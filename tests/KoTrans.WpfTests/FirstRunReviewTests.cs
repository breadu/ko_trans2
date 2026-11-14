using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class FirstRunReviewTests
{
    internal static void Run()
    {
        var failures = new List<Exception>();
        foreach (var scenario in new Action[] { IndependentIssues, StopClearsIssues, MainStartGuard, CloseDuringMainStart, ProfileChangedDuringMainStart, SourceOnlyStart, AreaRefreshGuidance })
            try { scenario(); } catch (Exception ex) { failures.Add(new Exception(scenario.Method.Name, ex)); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    private static FirstRunReadiness State() => new(Guid.NewGuid(), true, true, true, true, false, "synthetic", "ready");
    private static FirstRunWizardController Waiting(FirstRunReadiness state)
    {
        var controller = new FirstRunWizardController(); controller.Refresh(state);
        for (int i = 0; i < 4; i++) controller.Next(state);
        controller.Arm(state.ProfileId); controller.Bind(new object(), state.ProfileId); controller.Started(true);
        return controller;
    }
    private static void IndependentIssues()
    {
        var state = State(); using var controller = Waiting(state);
        controller.ReportPipelineIssue("AI failure", PipelineStatusArea.Translation);
        controller.ReportPipelineIssue("Capture wait", PipelineStatusArea.Capture, isError: false);
        Check(controller.Message == "AI failure", "Capture wait replaced a more important translation error");
        controller.ReportPipelineIssue("Capture failure", PipelineStatusArea.Capture);
        controller.RecoverPipelineAreas(PipelineStatusArea.Capture);
        controller.Refresh(state with { Running = true });
        Check(controller.HasError && controller.Message == "AI failure", "Capture recovery discarded an unresolved AI failure in the wizard");
        controller.RecoverPipelineAreas(PipelineStatusArea.Translation);
        controller.Refresh(state with { Running = true });
        Check(!controller.HasError && controller.Message == "ready", "All recovered areas left a stale wizard error");
    }
    private static void StopClearsIssues()
    {
        var state = State(); using var controller = Waiting(state);
        controller.ReportPipelineIssue("Old AI failure", PipelineStatusArea.Translation);
        controller.Refresh(state);
        Check(!controller.Waiting && controller.Step == FirstRunStep.Start && !controller.HasError,
            "Stopping kept errors from the retired wizard session");
        controller.Refresh(state);
        Check(controller.Message == "ready", "A second refresh resurrected a stopped session's error");
    }
    private static void MainStartGuard()
    {
        using var fixture = new StartFixture();
        var first = fixture.BeginStart(); var second = fixture.Main.StartMainTranslationAsync(false);
        bool guarded = fixture.Wizard.IsBusy && !fixture.Wizard.Next.IsEnabled && second.IsCompletedSuccessfully && !second.Result && fixture.Saves == 1;
        fixture.ReleaseWithReadinessFailure();
        var firstError = Settle(first); var secondError = Settle(second);
        Check(guarded, "Main start bypassed the wizard busy guard and allowed a duplicate start/save");
        Check(firstError is null && secondError is null && !first.Result && !fixture.Wizard.IsBusy
            && fixture.Wizard.Controller.HasError && fixture.Wizard.Next.IsEnabled,
            "Readiness exception escaped the main start instead of restoring an actionable wizard");
    }
    private static void CloseDuringMainStart()
    {
        using var fixture = new StartFixture(); var start = fixture.BeginStart();
        int reads = fixture.BoundsReads;
        Close(fixture.Wizard); fixture.ReleaseWithReadinessFailure();
        var error = Settle(start);
        Check(error is null && !start.Result && fixture.BoundsReads == reads && fixture.Main.FirstRunWizard is null,
            "A closed wizard continued main-start preparation after its pending save");
    }
    private static Exception? Settle(Task task) { try { Complete(task); return null; } catch (Exception ex) { return ex; } }
    private static void ProfileChangedDuringMainStart()
    {
        using var fixture = new StartFixture(); var start = fixture.BeginStart();
        var other = new AppSettings(); other.Validate(); other.DefaultProfile.ShowTranslation = false;
        fixture.Main.ApplyLoadedSettings(other); fixture.ReleaseWithReadinessFailure();
        var error = Settle(start);
        Check(error is null && !start.Result && !other.DefaultProfile.ShowTranslation,
            "A pending wizard start changed another profile's subtitle settings");
    }
    private static void AreaRefreshGuidance()
    {
        using var fixture = new StartFixture();
        fixture.Wizard.Controller.Back(); fixture.Wizard.Controller.Back(); fixture.Wizard.Render();
        fixture.FailReadiness(); Complete(fixture.Wizard.RefreshInBackgroundAsync());
        var action = Named<Button>(fixture.Wizard, "WizardRetry").Content.ToString()!;
        Check(fixture.Wizard.Controller.HasError && fixture.Wizard.Controller.Message.Contains(action),
            "Area readiness failure referred to a button absent from the current wizard step");
    }
    private static void SourceOnlyStart()
    {
        using var fixture = new StartFixture();
        Named<CheckBox>(fixture.Main, "ShowKorean").IsChecked = false; Pump();
        var overlay = Track(fixture.Main.CreateOverlayWindow()); Track(overlay.Chrome);
        var active = fixture.Main.CreateProfilePipeline(new NoOcr(), null, _ => throw new Exception("No capture expected"));
        int displayRestarts = 0, starts = 0;
        fixture.Main.RestartSubtitleRecognitionOverride = () => { displayRestarts++; return Task.FromResult(false); };
        fixture.Main.PresentStartingOverlayOverride = _ => { starts++; throw new TranslationException("Synthetic stop before engine creation"); };
        try {
            fixture.Main.ObserveTranslation(active, fixture.Wizard.Controller.ProfileId, watch: true); active.Start(false);
            var start = fixture.Main.StartMainTranslationAsync(false); Complete(start);
            Check(displayRestarts == 0 && starts == 1 && !start.Result && !active.IsRunning,
                "Wizard enabling Korean started a competing display-mode restart");
        } finally { Complete(active.DisposeAsync().AsTask()); Close(overlay); Close(overlay.Chrome); }
    }
    private sealed class NoOcr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) => throw new Exception("No OCR expected");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class StartFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "KoTrans.WizardReview", Guid.NewGuid().ToString("N"));
        private TaskCompletionSource? saveGate;
        private bool failReadiness;
        internal int Saves, BoundsReads;
        internal MainWindow Main { get; }
        internal FirstRunWizardWindow Wizard { get; }
        internal StartFixture()
        {
            var data = new AppSettings(); data.Validate(); var profile = data.DefaultProfile;
            profile.CaptureMode = CaptureMode.Window; profile.CaptureAreaConfigured = true; profile.WindowCaptureRegion = new(0, 0, 200, 100);
            profile.Translation = new() { Provider = TranslationProvider.Ollama, Model = "synthetic" };
            var target = new WindowTarget(new(120), 120, 120, "Synthetic game", "test", "C:\\Synthetic\\test.exe", "Synthetic");
            Main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => [target], _ => {
                BoundsReads++; if (failReadiness) throw new InvalidOperationException("Synthetic window query failure"); return new(0, 0, 800, 600);
            }, persistSettings: _ => { if (saveGate is not null) Saves++; return saveGate?.Task ?? Task.CompletedTask; }));
            Main.ApplyLoadedSettings(data); Main.SelectGameWindow(target);
            Wizard = Track(Main.OpenFirstRunWizard(false)); Complete(Wizard.RefreshAsync());
            var state = Main.WizardReadinessAsync(FirstRunStep.Start, default); Complete(state);
            for (int i = 0; i < 4; i++) Check(Wizard.Controller.Next(state.Result), "Start fixture could not advance");
            Wizard.Render();
        }
        internal Task<bool> BeginStart() { saveGate = new(TaskCreationOptions.RunContinuationsAsynchronously); return Main.StartMainTranslationAsync(false); }
        internal void ReleaseWithReadinessFailure() { failReadiness = true; saveGate!.TrySetResult(); }
        internal void FailReadiness() => failReadiness = true;
        public void Dispose()
        {
            failReadiness = false; saveGate?.TrySetResult(); saveGate = null;
            Close(Wizard); Close(Main); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
