using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Threading;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed class DebugOverlayWindow : Window
{
    private readonly DebugTelemetry telemetry;
    private readonly DebugOverlayStateStore store;
    private readonly string directory;
    private DebugOverlayState state;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer saveDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly TextBlock capture = Text("DebugCapture"), gate = Text("DebugFrameGate"), ocr = Text("DebugOcr"), translation = Text("DebugTranslation");
    private readonly TextBlock ocrSummary = Text("DebugOcrSummary"), translationSummary = Text("DebugTranslationSummary"), eventsSummary = Text("DebugEventsSummary");
    private readonly TextBlock status = Text("DebugStatus"), activity = Text("DebugActivity"), details = Text("DebugEventDetails");
    private readonly TextBlock exclusionWarning = Text("DebugExclusionWarning");
    private readonly ListBox events = new() { Name = "DebugEvents", Background = new SolidColorBrush(Color.FromRgb(20, 30, 42)), Foreground = Brushes.White, MaxHeight = 175 };
    private readonly Expander ocrGroup, translationGroup, eventGroup;
    private readonly Button pause = new() { Name = "DebugPause", Content = "일시정지" }, folder = new() { Name = "DebugOpenFolder", Content = "폴더 열기", Visibility = Visibility.Collapsed };
    private bool paused, closed, userPlacementEdited;
    private Task saves = Task.CompletedTask, reports = Task.CompletedTask;
    private Task stateWrites = Task.CompletedTask;
    internal bool CaptureExcluded { get; private set; }
    internal DebugSnapshot? DisplayedSnapshot { get; private set; }
    internal bool IsPaused => paused;
    internal Task PendingWork => Task.WhenAll(saves, reports, stateWrites);
    internal static bool ExcludedForCapture(Window window) => window is DebugOverlayWindow { CaptureExcluded: true };
    private static TextBlock Text(string name) => new() { Name = name, Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    internal DebugOverlayWindow(DebugTelemetry telemetry, DebugOverlayStateStore store, string directory, DebugOverlayState state)
    {
        this.telemetry = telemetry; this.store = store; this.directory = directory; this.state = state;
        Title = "KO Trans 2 · DEBUG"; Topmost = true; ShowActivated = false; MinWidth = 340; MinHeight = 280;
        Width = Math.Max(MinWidth, state.Placement.Width); Height = Math.Max(MinHeight, state.Placement.Height);
        Background = new SolidColorBrush(Color.FromRgb(17, 28, 41)); Foreground = Brushes.White;
        ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.Manual;
        var root = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var save = new Button { Name = "DebugSave", Content = "진단 저장" }; var close = new Button { Name = "DebugClose", Content = "닫기" };
        foreach (var button in new[] { save, pause, close })
        {
            button.Width = 96;
            button.MinHeight = 36;
            toolbar.Children.Add(button);
        }
        toolbar.Children.Add(activity);
        var notices = new StackPanel(); notices.Children.Add(exclusionWarning); notices.Children.Add(status); notices.Children.Add(folder); DockPanel.SetDock(notices, Dock.Bottom); root.Children.Add(notices);
        var body = new StackPanel(); body.Children.Add(Section("CAPTURE", capture)); body.Children.Add(Section("FRAME GATE", gate));
        ocrGroup = Group("DebugOcrGroup", ocrSummary, ocr, state.OcrExpanded);
        translationGroup = Group("DebugTranslationGroup", translationSummary, translation, state.TranslationExpanded);
        var eventBody = new StackPanel(); eventBody.Children.Add(events); eventBody.Children.Add(details);
        eventGroup = Group("DebugEventsGroup", eventsSummary, eventBody, state.EventsExpanded);
        body.Children.Add(ocrGroup); body.Children.Add(translationGroup); body.Children.Add(eventGroup);
        root.Children.Add(new ScrollViewer { Name = "DebugScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = root;
        foreach (var group in new[] { ocrGroup, translationGroup, eventGroup }) { group.Expanded += (_, _) => ScheduleSave(); group.Collapsed += (_, _) => ScheduleSave(); }
        events.SelectionChanged += (_, _) => { if (events.SelectedItem is DebugEvent entry) details.Text = EventDetails(entry); };
        events.DisplayMemberPath = nameof(DebugEvent.Display);
        save.Click += async (_, _) => { var current = SaveReportAsync(); reports = Task.WhenAll(reports, current); await current; };
        pause.Click += (_, _) => SetPaused(!paused);
        close.Click += (_, _) => Close();
        folder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(Path.Combine(directory, "DebugReports")) { UseShellExecute = true }); } catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "폴더를 열지 못했습니다."; } };
        refresh.Tick += (_, _) => SafeRefresh(); saveDelay.Tick += async (_, _) => { saveDelay.Stop(); saves = SaveStateAsync(); await saves; };
        SourceInitialized += (_, _) => {
            try { DesktopWindows.ApplyPlacement(this, state.Placement); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "디버그 창 위치를 복원하지 못해 기본 위치를 사용합니다."; Left = 30; Top = 30; }
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(Message);
            SetCaptureExclusion(CaptureExclusionPolicy.TryExclude(handle));
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        };
        Loaded += (_, _) => refresh.Start();
        Closing += (_, _) => { refresh.Stop(); saveDelay.Stop(); saves = SaveStateAsync(); };
        Closed += (_, _) => { closed = true; Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged; };
        SafeRefresh();
    }
    private static Border Section(string title, UIElement body)
    {
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = title, Foreground = Brushes.LightSkyBlue, FontWeight = FontWeights.SemiBold }); panel.Children.Add(body);
        return new Border { Child = panel, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), BorderBrush = Brushes.DarkSlateGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
    }
    private static Expander Group(string name, TextBlock header, UIElement content, bool expanded) => new() {
        Name = name, Header = header, Content = content, IsExpanded = expanded, Foreground = Brushes.White, Margin = new Thickness(0, 3, 0, 8) };
    internal void SetCaptureExclusion(bool success)
    {
        CaptureExcluded = success;
        telemetry.SetContext(v => v with { Exclusion = CaptureExclusionPolicy.DebugCaptureEnabled ? DebugReason.NotMeasured
            : success ? DebugReason.Excluded : DebugReason.ExclusionFailed });
        exclusionWarning.Text = success || CaptureExclusionPolicy.DebugCaptureEnabled ? ""
            : "캡처 제외 실패 · 디버그 창을 캡처 영역 밖으로 이동하세요.";
    }
    internal void SetPaused(bool value) { paused = value; pause.Content = value ? "재개" : "일시정지"; activity.Text = value ? "PAUSED" : "RUNNING"; if (!value) SafeRefresh(); }
    private void SafeRefresh() { try { RefreshSnapshot(); } catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "진단 표시를 갱신하지 못했습니다."; } }
    internal void RefreshSnapshot()
    {
        if (paused || closed) return;
        var s = telemetry.Snapshot(); DisplayedSnapshot = s; var v = s.Values; var c = s.Current;
        activity.Text = "RUNNING";
        capture.Text = $"Mode  {v.CaptureMode} / {v.RecognitionMode} / {v.ReadingMode}\nTarget  {v.Target}\nRegion  {(v.Width > 0 ? $"{v.Width} × {v.Height}" : "N/A")}\nCapture  {v.CaptureStatus}\n대기 이유  {CaptureWaitLabel(v.CaptureWait)}\nExclusion  {v.Exclusion}\nLast error  {v.CaptureError}";
        gate.Text = $"최종 처리  {FrameActionLabel(v)}\nOCR 작업  {(v.OcrInProgress ? "실행 중" : v.OcrScheduled ? "예약됨" : "실행 안 함")}\n픽셀 판정(1차)  {v.Decision}\nReason  {v.GateReason}\nScope  {v.GateScope}\nText quiet  {Number(v.StableMs)} / {Number(v.SettleMs)} ms\nTrigger  {v.Trigger}\nReuse  {v.ReuseReason}";
        capture.Text += $"\n경로  {v.CapturePath}\n화면에서 읽은 크기  {v.ScreenCaptureWidth?.ToString() ?? "N/A"} × {v.ScreenCaptureHeight?.ToString() ?? "N/A"}";
        ocrSummary.Text = $"OCR                 {c?.OcrCalls ?? 0}회 / 화면 캐시 {v.OcrResultCacheHits}회 / 글자 캐시 {v.RecognitionCacheHits}회";
        ocr.Text = $"Current cycle  #{c?.Id.ToString() ?? "N/A"} ({c?.Status ?? "N/A"})\nOCR calls  {c?.OcrCalls ?? 0} / session {v.LogicalOcrCalls}\nOCR 캐시 재사용  {v.OcrResultCacheHits}회 (현재 문장 {c?.OcrCacheHits ?? 0}회)\n화면 캐시 미사용: {CacheMissLabel(v.OcrResultCacheLastMiss)}\n{CacheMissCounts(v.OcrResultCacheMisses)}\n공통 글자 인식 누적: 실행 {v.RecognitionInferences}회 / 재사용 {v.RecognitionCacheHits}회\n글자 캐시 미사용: {CacheMissLabel(v.RecognitionCacheLastMiss)}\n{CacheMissCounts(v.RecognitionCacheMisses)}\nPrevious cycle  {s.Previous?.OcrCalls.ToString() ?? "N/A"}\nCycle reuses  {c?.Reuses ?? 0}\nVerified / animation  {v.FrameReuses} / {v.AnimationReuses}\nEngine / model  {v.Engine} / {v.OcrModel}\nBackend  {v.RequestedBackend} → {v.ActualBackend}\nCPU fallback  {v.Fallback}\nLast GPU / CPU attempts  {v.GpuAttempts?.ToString() ?? "N/A"} / {v.CpuAttempts?.ToString() ?? "N/A"}\nDetected regions / OCR lines  {v.DetectedRegions?.ToString() ?? "N/A"} / {v.AcceptedLines?.ToString() ?? "N/A"}\nLast OCR  {Number(v.OcrMs)}ms\nDetection / recognition / post  {Number(v.DetectionMs)} / {Number(v.RecognitionMs)} / {Number(v.PostprocessMs)}ms\nSearch  {v.Search}";
        translationSummary.Text = $"TRANSLATION       {v.TranslationState} / {Number(v.TranslationMs)}ms";
        ocr.Text += $"\n끝 표시 진단  {(v.MarkerObservations?.ToString() ?? "N/A")}건 (최근 {v.MarkerDiagnostics?.Count ?? 0}건 보관)";
        ocr.Text += $"\n게임별 표시 학습  {v.MarkerLearningMode} / 확정 {v.LearnedMarkerPatterns}개 / 관찰 {v.LearningMarkerCandidates}개\n서로 다른 대사 {v.LearningMarkerPages}/5 · 깜빡임 {v.LearningMarkerAnimatedPages}/2\n이번 OCR 제외 {v.LearnedMarkerExcluded} / 제외 가능 {v.LearnedMarkerWouldExclude}";
        if(v.MarkerLearningStorageError)ocr.Text += "\n학습 저장 실패 (이번 실행에서만 사용)";
        ocr.Text += $"\n현재 세션 누적\n표시 제외 {v.LearnedMarkerExcludedTotal}개 / 적용 OCR {v.LearnedMarkerExcludingOcrCalls}회\n학습 표시만 변화 → OCR 생략 {v.LearnedMarkerSkippedFrames}프레임\n재확인 전환 {v.LearnedMarkerRecheckFrames}회 / 본문 추가 확인 {v.LearnedMarkerExtraBodyChecks}회\n학습 프레임 판정 {MarkerFrameLabel(v.LearnedMarkerFrameDecision)} / {v.LearnedMarkerFrameFailure}";
        ocr.Text += $"\n작은 영역 빠른 관찰 {v.LearnedMarkerFastSamples}회 / 점멸 포착 {v.LearnedMarkerFastCycles}회";
        ocr.Text += $"\n학습 증거 반영 {v.LearningMarkerValidatedCycles}회 / 본문 변화로 중단 {v.LearningMarkerBodyChangeDiscards}회 / 관찰 만료 {v.LearningMarkerExpiredWatches}회";
        ocr.Text += $"\n본문 확인 재사용 {v.LearnedMarkerBodyCacheHits}회\n재사용 준비 성공 {v.LearnedMarkerFramePreparations}회 / 실패 {v.LearnedMarkerFramePreparationFailures}회\n현재 비교의 1단계 색상 오차 {v.LearnedMarkerFrameNoisePixels}픽셀";
        if(v.MarkerDiagnostics?.LastOrDefault() is { } marker)
            ocr.Text += $"\n마지막 판정  {marker.Stage} / {marker.Decision}\n후보 위치·크기  {marker.Bounds.X},{marker.Bounds.Y} {marker.Bounds.Width}×{marker.Bounds.Height}\n신뢰도  {marker.Confidence:F3}";
        translation.Text = $"Provider / model  {v.Provider} / {v.TranslationModel}\nBackend  {v.TranslationBackend}\nCycle AI calls  {c?.AiRequests ?? 0}\nSession AI / translation cache  {v.AiRequests} / {v.TranslationCacheHits}\nStatus  {v.TranslationState}\nLast time  {Number(v.TranslationMs)}ms\nOverlay  {v.Overlay}\nLast error  {v.LastError}";
        translation.Text += $"\n전달 결과와 실제 자막 비교\n원문 {MatchLabel(v.OverlaySourceMatchesUpdate)} / 번역 {MatchLabel(v.OverlayTranslationMatchesUpdate)}\n자막 반영 불일치 {v.OverlayContentMismatches}회";
        eventsSummary.Text = $"RECENT EVENTS       최근 {Math.Min(5, s.Events.Count)}건";
        var selected = events.SelectedItem; var recent = s.Events.Take(5).ToArray(); events.ItemsSource = recent;
        if (selected is DebugEvent old && recent.Contains(old)) events.SelectedItem = old;
    }
    private static string MatchLabel(bool? match) => match switch { true => "일치", false => "불일치", _ => "측정 전/비교 대상 없음" };
    private static string Number(double? n) => n?.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A";
    private static string CacheMissLabel(OcrCacheMissReason reason)=>reason switch {
        OcrCacheMissReason.Empty=>"보관 결과 없음",OcrCacheMissReason.InputNotCached=>"일치하는 입력 없음",
        OcrCacheMissReason.ConditionsChanged=>"조건 변경",OcrCacheMissReason.Evicted=>"용량 초과로 제거됨",
        OcrCacheMissReason.Bypassed=>"수동 재인식으로 우회",_=>"없음"};
    private static string CacheMissCounts(OcrCacheMissCounts v)=>$"결과 없음 {v.Empty} · 입력 불일치 {v.InputNotCached} · 조건 변경 {v.ConditionsChanged}\n용량 초과 {v.Evicted} · 수동 우회 {v.Bypassed}";
    private static string MarkerFrameLabel(MarkerFrameDecision decision)=>decision switch {
        MarkerFrameDecision.Reused=>"학습 표시만 변화 · 재사용 중",MarkerFrameDecision.Unchanged=>"동일 화면",
        MarkerFrameDecision.BodyChanged=>"본문·주변 변화 · OCR 확인",MarkerFrameDecision.Uncertain=>"모양 불확실 · OCR 확인",
        MarkerFrameDecision.SizeChanged=>"크기 변경 · OCR 확인",_=>"검증 대기"};
    private static string FrameActionLabel(DebugValues v)
    {
        if(v.CaptureStatus==DebugReason.Paused) return "캡처 일시 대기" + (v.OcrInProgress ? " · 이전 OCR 정리 중" : "");
        if(v.OcrInProgress) return v.FinalFrameAction==DebugFrameAction.Reused ? "재사용 중 · 별도 OCR 확인 중" : "문자 인식 중";
        return v.FinalFrameAction switch {
            DebugFrameAction.Reused=>"재사용 중",DebugFrameAction.Watching=>"감시 중",DebugFrameAction.CheckingStability=>"변화 확인 중",
            DebugFrameAction.OcrQueued=>"OCR 예약",DebugFrameAction.Recognizing=>"문자 인식 중",DebugFrameAction.OcrCompleted=>"OCR 인식 완료",
            DebugFrameAction.ConfirmingText=>"문장 확인 중",DebugFrameAction.Accepted=>"문장 확정",DebugFrameAction.WaitingForTrigger=>"트리거 입력 대기",
            DebugFrameAction.CapturePaused=>"캡처 일시 대기",DebugFrameAction.CaptureUnavailable=>"캡처 오류",DebugFrameAction.Stopped=>"중지",_=>"측정 전" };
    }
    private static string CaptureWaitLabel(CaptureWaitReason reason) => reason switch {
        CaptureWaitReason.NotForeground=>"게임 전면 대기",CaptureWaitReason.HiddenOrMinimized=>"게임 창 복원 대기",
        CaptureWaitReason.Occluded=>"캡처 영역 가림 해제 대기",CaptureWaitReason.ScalingTransition=>"확대 화면 전환 대기",CaptureWaitReason.WindowBusy=>"원본 게임 캡처 응답 대기",_=>"없음" };
    private static string EventDetails(DebugEvent e) => $"{e.Time.ToLocalTime():HH:mm:ss.fff} {e.Code} / {e.Reason}\nSession {e.Session}, cycle #{e.Cycle?.Id.ToString() ?? "N/A"}\nOCR {e.Cycle?.OcrCalls ?? 0}, AI {e.Cycle?.AiRequests ?? 0}\n최종 처리 {FrameActionLabel(e.Values)}\n픽셀 판정(1차) {e.Values.Decision} / {e.Values.GateReason}\nBackend {e.Values.ActualBackend}\nOCR {Number(e.Values.OcrMs)}ms / translation {Number(e.Values.TranslationMs)}ms";
    internal async Task SaveReportAsync()
    {
        try {
            var snapshot = telemetry.Snapshot(); // latest, even while display is paused
            var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "N/A";
            string[] parts = version.Split('+', 2);
            await Task.Run(() => DebugReportWriter.SaveAsync(directory, snapshot, parts[0], parts.Length > 1 ? parts[1] : ""));
            status.Text = "진단 정보가 저장되었습니다."; folder.Visibility = Visibility.Visible;
        } catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "진단 저장 실패 · 폴더 권한과 여유 공간을 확인하고 다시 시도하세요."; }
    }
    private void ScheduleSave() { if (closed) return; saveDelay.Stop(); saveDelay.Start(); }
    internal async Task SaveStateAsync()
    {
        try {
        state = state with { OcrExpanded = ocrGroup.IsExpanded, TranslationExpanded = translationGroup.IsExpanded, EventsExpanded = eventGroup.IsExpanded };
        if (userPlacementEdited && new WindowInteropHelper(this).Handle != IntPtr.Zero && WindowState == WindowState.Normal) {
            state = state with { Placement = DesktopWindows.RememberOverlay(this), DpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX }; userPlacementEdited = false;
        }
        var snapshot = state;
        var write = PersistStateAsync(stateWrites, snapshot); stateWrites = write;
        if (!await write) status.Text = "디버그 창 상태 저장 실패 · 다음 변경이나 종료 때 다시 시도합니다.";
        } catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "디버그 창 상태 저장 실패 · 다음 변경이나 종료 때 다시 시도합니다."; }
    }
    private async Task<bool> PersistStateAsync(Task previous, DebugOverlayState snapshot)
    {
        // Preserve user edit order even when thread-pool work starts out of order.
        try { await previous.ConfigureAwait(false); await Task.Run(() => store.SaveAsync(snapshot)).ConfigureAwait(false); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
    private IntPtr Message(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return new(3); } // MA_NOACTIVATE
        if (msg == 0x232) { userPlacementEdited = true; ScheduleSave(); }
        return IntPtr.Zero;
    }
    private void DisplaysChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() => {
        if (closed || userPlacementEdited || new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        try { DesktopWindows.ApplyPlacement(this, state.Placement); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "모니터 배치를 갱신하지 못했습니다. 창을 직접 이동해 주세요."; }
    });
    internal async Task CloseForExitAsync() { if (!closed) Close(); await PendingWork; }
}
