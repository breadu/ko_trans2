using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class DebugOverlayTests
{
    private sealed class Tray : IApplicationTray
    {
        public bool IsAvailable => true;
        public event Action? OpenRequested { add { } remove { } }
        public event Action? ExitRequested { add { } remove { } }
        public void Dispose() { }
    }
    private sealed class NoOcr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t) => throw new Exception("No OCR/capture expected");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static void Run()
    {
        // The launch option, not --dev-run or the presence of a debug window
        // in tests, opts in to screenshots of non-subtitle UI.
        string sampleRoot = Path.Combine(Path.GetTempPath(), "KoTrans.DebugPolicy");
        var normal = AppLaunchOptions.Parse([], sampleRoot);
        var dev = AppLaunchOptions.Parse(["--dev-run"], sampleRoot);
        var debugLaunch = AppLaunchOptions.Parse(["--debug-overlay"], sampleRoot);
        Check(!normal.DebugOverlay && !dev.DebugOverlay && debugLaunch.DebugOverlay
            && CaptureExclusionPolicy.NeedsExclusion(normal.DebugOverlay, false)
            && CaptureExclusionPolicy.NeedsExclusion(dev.DebugOverlay, false)
            && CaptureExclusionPolicy.NeedsExclusion(normal.DebugOverlay, true)
            && !CaptureExclusionPolicy.NeedsExclusion(debugLaunch.DebugOverlay, false)
            && CaptureExclusionPolicy.NeedsExclusion(debugLaunch.DebugOverlay, true),
            "Debug overlay must enable capturing wizard, reader and diagnostic windows without exposing subtitles");
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.DebugUi", Guid.NewGuid().ToString("N"));
        var settings = new AppSettings(); settings.Validate(); settings.DefaultProfile.CaptureMode = CaptureMode.Clipboard;
        var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => [])); main.ApplyLoadedSettings(settings);
        main.ConfirmShutdownFailureOverride = _ => throw new Exception("Unexpected shutdown error");
        Check(main.DebugWindow is null && main.DebugDiagnostics is null && !File.Exists(Path.Combine(root, "debug-overlay.json")), "Normal constructor created diagnostics/data");
        Complete(main.EnableDebugOverlayAsync(root, show: false));
        var debug = Track(main.DebugWindow!); var telemetry = main.DebugDiagnostics!;
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        settings.DefaultProfile.CaptureMode = CaptureMode.Region; settings.DefaultProfile.CaptureAreaConfigured = false;
        var failedStart = main.StartTranslationAsync(true); Complete(failedStart);
        Check(!failedStart.Result && telemetry.Snapshot().Values.AiRequests == 0
            && telemetry.Snapshot().Events.Any(e => e.Code == DebugCode.SESSION_START_FAILED), "Preparation failure is invisible or counted as a provider request");
        settings.DefaultProfile.CaptureMode = CaptureMode.Clipboard;
        var pipeline = main.CreateProfilePipeline(new NoOcr(), new MockTranslationService(), _ => throw new Exception("Debug captured extra frames"));
        main.ObserveTranslation(pipeline, settings.ActiveProfileId, watch: true); pipeline.Start(false);
        try {
            Check(debug.Topmost && debug.Owner is null && !debug.ShowActivated && debug.Width == 400 && debug.Height == 600, "Debug window is not independent/nonactivating/default size");
            pipeline.SubmitClipboard("synthetic private original");
            Until(() => telemetry.Snapshot().Values.AiRequests == 1 && telemetry.Snapshot().Values.TranslationState == DebugReason.Completed, "Debug did not observe provider completion");
            Until(() => telemetry.Snapshot().Values is { OverlaySourceMatchesUpdate: true, OverlayTranslationMatchesUpdate: true },
                "Actual WPF subtitle contents were not compared with the applied pipeline result");
            debug.RefreshSnapshot(); Layout(debug, 400, 600);
            Check(Named<TextBlock>(debug,"DebugTranslation").Text.Contains("원문 일치 / 번역 일치"),
                "Subtitle content-match diagnostics missing from the display");
            main.AttachTray(new Tray());
            Check(main.HideToTray() && main.DebugWindow == debug && pipeline.IsRunning, "Hiding main to tray changed debug/pipeline lifetime");
            main.DisposeTray();
            Check(Named<TextBlock>(debug, "DebugOcrSummary").Text.Contains("0회") && Named<TextBlock>(debug, "DebugTranslationSummary").Text.Contains("Completed"), "Collapsed summaries lack actual measurements");
            Check(Named<TextBlock>(debug,"DebugOcrSummary").Text.Contains("캐시 0회")
                && Named<TextBlock>(debug,"DebugOcr").Text.Contains("OCR 캐시 재사용  0회"),"OCR cache totals missing from diagnostic display");
            Check(Named<TextBlock>(debug,"DebugOcr").Text.Contains("공통 글자 인식 누적")
                && Named<TextBlock>(debug,"DebugOcr").Text.Contains("조건 변경 0")
                && Named<TextBlock>(debug,"DebugOcr").Text.Contains("수동 우회 0"),"Recognition cache/miss counters missing from diagnostic display");
            var og = Named<Expander>(debug, "DebugOcrGroup"); var tg = Named<Expander>(debug, "DebugTranslationGroup"); var eg = Named<Expander>(debug, "DebugEventsGroup");
            og.IsExpanded = false; tg.IsExpanded = true; eg.IsExpanded = true;
            Layout(debug, 400, 600);
            Check(debug.Width == 400 && debug.Height == 600 && Named<ScrollViewer>(debug, "DebugScroll").VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Expanding groups resized the window or removed scrolling");
            Complete(debug.SaveStateAsync());
            var load = new DebugOverlayStateStore(root).LoadAsync(); Complete(load);
            Check(!load.Result.OcrExpanded && load.Result.TranslationExpanded && load.Result.EventsExpanded, "Independent expansion state not saved");
            Click(Named<Button>(debug, "DebugPause")); var frozen = debug.DisplayedSnapshot;
            // A stale/future overlay version can reject delivery even though the main
            // callback runs. Diagnostic success must reflect actual controls.
            overlay.SetPipelineResult(new(pipeline.Version + 100, PipelineState.Watching, "synthetic newer overlay"));
            pipeline.SubmitClipboard("second private original", manual: true);
            Until(() => telemetry.Snapshot().Values.AiRequests == 2 && telemetry.Snapshot().Values.TranslationState == DebugReason.Completed, "Pause stopped diagnostic collection/translation");
            Until(() => telemetry.Snapshot().Values is { OverlaySourceMatchesUpdate: false, OverlayContentMismatches: > 0 },
                "Rejected subtitle delivery was logged as successful content");
            debug.RefreshSnapshot(); Check(ReferenceEquals(debug.DisplayedSnapshot, frozen) && pipeline.IsRunning, "Pause changed displayed snapshot or pipeline");
            Check(!Directory.Exists(Path.Combine(root, "DebugReports")), "Background refresh automatically saved a report");
            Click(Named<Button>(debug, "DebugSave")); Complete(debug.PendingWork);
            string file = Directory.GetFiles(Path.Combine(root, "DebugReports"), "*.txt").Single(); string report = File.ReadAllText(file);
            Check(report.Contains("AiRequests: 2") && !report.Contains("private original"), "Paused report is stale or contains source/translation data");
            Click(Named<Button>(debug, "DebugPause"));
            Check(!debug.IsPaused && debug.DisplayedSnapshot!.Values.AiRequests == 2, "Resume did not restore latest display");
            debug.SetCaptureExclusion(false);
            Check(!DebugOverlayWindow.ExcludedForCapture(debug) && Text(debug).Contains("캡처 영역 밖"), "Failed exclusion was treated as active or did not warn");
            debug.SetCaptureExclusion(true); Check(DebugOverlayWindow.ExcludedForCapture(debug), "Successful exclusion not integrated with capture guard");
            telemetry.Record(pipeline.DiagnosticSession,DebugCode.FRAME_CHANGED,DebugReason.Stabilizing,
                v=>v with {Decision=DebugReason.Stabilizing,FinalFrameAction=DebugFrameAction.CheckingStability});
            telemetry.Reuse(pipeline.DiagnosticSession,DebugReason.VerifiedFrame);debug.RefreshSnapshot();
            string gate=Named<TextBlock>(debug,"DebugFrameGate").Text;
            Check(gate.Contains("최종 처리  재사용 중") && gate.Contains("픽셀 판정(1차)  Stabilizing") && gate.Contains("OCR 작업  실행 안 함"),"Raw stabilizing state hid the final frame reuse action");
            telemetry.OcrStarted(pipeline.DiagnosticSession, DebugReason.Standard);
            telemetry.Record(pipeline.DiagnosticSession,DebugCode.OCR_SKIPPED,DebugReason.Unchanged,
                v=>v with {Decision=DebugReason.Unchanged,FinalFrameAction=DebugFrameAction.Watching,ReuseReason=DebugReason.None});
            debug.RefreshSnapshot();
            Check(Named<TextBlock>(debug,"DebugFrameGate").Text.Contains("최종 처리  문자 인식 중"),"Idle frame polling hid active OCR work");
            telemetry.OcrCompleted(pipeline.DiagnosticSession,new([],TimeSpan.Zero),1);
            debug.RefreshSnapshot();
            Check(!Named<TextBlock>(debug,"DebugFrameGate").Text.Contains("재사용 중") && !telemetry.Snapshot().Values.OcrInProgress,"Old reuse or active OCR remained after a completed check");
            foreach (var reason in new[] { CaptureWaitReason.NotForeground, CaptureWaitReason.HiddenOrMinimized, CaptureWaitReason.Occluded }) {
                telemetry.CapturePaused(pipeline.DiagnosticSession, reason); debug.RefreshSnapshot();
                Check(Named<TextBlock>(debug,"DebugFrameGate").Text.Contains("캡처 일시 대기")
                    && !Named<TextBlock>(debug,"DebugCapture").Text.Contains("대기 이유  없음"), "Capture wait/reason is not displayed");
            }
            telemetry.CaptureCompleted(pipeline.DiagnosticSession, 800, 600); debug.RefreshSnapshot();
            Check(!Named<TextBlock>(debug,"DebugFrameGate").Text.Contains("캡처 일시 대기"), "Resume left paused caption latched");
            telemetry.Record(pipeline.DiagnosticSession,DebugCode.CAPTURE_FAILED,DebugReason.CaptureUnavailable,
                v=>v with {CaptureStatus=DebugReason.CaptureUnavailable,CaptureError=DebugReason.CaptureUnavailable});
            debug.RefreshSnapshot(); Check(Named<TextBlock>(debug,"DebugFrameGate").Text.Contains("캡처 오류"),"Actual capture error looks like normal waiting");
            telemetry.CaptureCompleted(pipeline.DiagnosticSession, 800, 600);
            telemetry.OcrCompleted(pipeline.DiagnosticSession,new([],TimeSpan.Zero) {MarkerObservations=1,
                MarkerDiagnostics=[OcrMarkerDiagnostics.Describe(new("syntheticVO",.7f,new(20,30,40,15)),OcrMarkerStage.JoinedTail,OcrMarkerDecision.NoUniqueTailComponent)]},1);
            debug.RefreshSnapshot();
            Check(Named<TextBlock>(debug,"DebugOcr").Text.Contains("끝 표시 진단  1건")
                && Named<TextBlock>(debug,"DebugOcr").Text.Contains("NoUniqueTailComponent")
                && !Text(debug).Contains("syntheticVO"),"Marker diagnostics are missing or expose source text");
            string markerReport=DebugReportWriter.Format(telemetry.Snapshot(),"test",new string('a',40));
            Check(markerReport.Contains("NoUniqueTailComponent") && markerReport.Contains("TrailingLatinDigits")
                && !markerReport.Contains("syntheticVO"),"Saved report lost marker reasons or includes source text");
            telemetry.MarkerLearning(pipeline.DiagnosticSession,new(MarkerLearningMode.Apply,1,2,5,2,0,0) {
                ExcludedTotal=12,ExcludingOcrCalls=7,SkippedFrames=34,RecheckFrames=3,ExtraBodyChecks=8,BodyCheckCacheHits=21,FastSamples=90,FastCycles=2,
                FramePreparations=5,FramePreparationFailures=3,FrameNoisePixels=16,ValidatedCycles=4,BodyChangeDiscards=6,ExpiredWatches=2,
                FrameDecision=MarkerFrameDecision.Reused});
            debug.RefreshSnapshot();
            string totals=Named<TextBlock>(debug,"DebugOcr").Text;
            Check(totals.Contains("현재 세션 누적") && totals.Contains("표시 제외 12개") && totals.Contains("OCR 생략 34프레임")
                && totals.Contains("재사용 중") && totals.Contains("본문 추가 확인 8회") && totals.Contains("빠른 관찰 90회"),"Cumulative marker counters/meaning are missing from the debug overlay");
            Check(totals.Contains("재사용 준비 성공 5회 / 실패 3회") && totals.Contains("색상 오차 16픽셀"),"Reuse preparation/noise diagnostics are missing from the overlay");
            Check(totals.Contains("본문 확인 재사용 21회"),"Body verification reuse is missing from the overlay");
            Check(totals.Contains("학습 증거 반영 4회") && totals.Contains("본문 변화로 중단 6회") && totals.Contains("관찰 만료 2회"),"Learning evidence/discard diagnostics are missing");
            Click(Named<Button>(debug,"DebugSave"));Complete(debug.PendingWork);
            string totalsReport=File.ReadAllText(Directory.GetFiles(Path.Combine(root,"DebugReports"),"*.txt").OrderByDescending(File.GetLastWriteTimeUtc).First());
            Check(totalsReport.Contains("LearnedMarkerExcludedTotal: 12") && totalsReport.Contains("LearnedMarkerSkippedFrames: 34")
                && totalsReport.Contains("LearnedMarkerExtraBodyChecks: 8") && totalsReport.Contains("LearnedMarkerBodyCacheHits: 21") && totalsReport.Contains("LearnedMarkerFastCycles: 2")
                && totalsReport.Contains("LearnedMarkerFramePreparations: 5") && totalsReport.Contains("LearnedMarkerFramePreparationFailures: 3")
                && totalsReport.Contains("LearnedMarkerFrameNoisePixels: 16") && totalsReport.Contains("LearningMarkerValidatedCycles: 4")
                && totalsReport.Contains("LearningMarkerBodyChangeDiscards: 6") && totalsReport.Contains("LearningMarkerExpiredWatches: 2")
                && !totalsReport.Contains("private original"),"Saved debug report lost cumulative counters or leaked original text");
            var many=Enumerable.Range(0,40).Select(i=>OcrMarkerDiagnostics.Describe(new("private",.7f,new(i,1,20,10)),
                OcrMarkerStage.SeparateCandidate,OcrMarkerDecision.NotShortCandidate)).ToArray();
            telemetry.OcrCompleted(pipeline.DiagnosticSession,new([],TimeSpan.Zero) {MarkerDiagnostics=many,MarkerObservations=many.Length},1);
            var savedMarkers=telemetry.Snapshot().Values.MarkerDiagnostics!;
            many[8]=many[8] with {Bounds=new(999,1,20,10)};
            Check(savedMarkers.Count==32 && savedMarkers[0].Bounds.X==8 && telemetry.Snapshot().Values.MarkerObservations==40,
                "Marker history is unbounded or mutable through the OCR result");
            telemetry.Record(pipeline.DiagnosticSession, DebugCode.OCR_TEXT_REJECTED, DebugReason.EmptyText);
            telemetry.EndPending(pipeline.DiagnosticSession);
            debug.RefreshSnapshot(); var list = Named<ListBox>(debug, "DebugEvents"); list.SelectedIndex = 0;
            Check(Named<TextBlock>(debug, "DebugOcr").Text.Contains("Unconfirmed"), "Mock unconfirmed cycle is not displayed");
            string selectedDetails = Named<TextBlock>(debug, "DebugEventDetails").Text;
            telemetry.Record(pipeline.DiagnosticSession, DebugCode.GPU_FALLBACK, DebugReason.OcrFailure, v => v with { Fallback = true, ActualBackend = "CPU" });
            debug.RefreshSnapshot();
            Check(Named<TextBlock>(debug, "DebugEventDetails").Text == selectedDetails, "Selected historical event was replaced with live values");
            Check(Named<TextBlock>(debug, "DebugOcr").Text.Contains("CPU fallback  True"), "Mock GPU fallback state is not displayed");
            string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "synthetic path conflict");
            var failureWindow = Track(new DebugOverlayWindow(telemetry, new DebugOverlayStateStore(blocked), blocked, new()));
            try {
                Complete(failureWindow.SaveReportAsync()); Complete(failureWindow.SaveStateAsync());
                Check(Text(failureWindow).Contains("저장 실패") && pipeline.IsRunning, "Debug write failure interrupted translation or lacked guidance");
            } finally { Complete(failureWindow.CloseForExitAsync()); Close(failureWindow); }
            Complete(overlay.CloseForExitAsync());
            Check(main.DebugWindow == debug && new WindowInteropHelper(debug).Handle == IntPtr.Zero, "Closing overlay destroyed debug or created HWND in Background");
            // Rebind a running session: debug X must only close debug and flush its work.
            var next = main.CreateProfilePipeline(new NoOcr(), new MockTranslationService(), _ => throw new Exception("No capture"));
            main.ObserveTranslation(next, settings.ActiveProfileId); next.Start(false);
            Complete(debug.CloseForExitAsync()); Check(next.IsRunning, "Closing debug stopped recognition/translation");
            Complete(next.DisposeAsync().AsTask());
        } finally {
            Complete(pipeline.DisposeAsync().AsTask()); Complete(debug.CloseForExitAsync());
            Close(main); Close(overlay); Close(overlay.Chrome); Close(debug); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
