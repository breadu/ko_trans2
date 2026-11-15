using System.Collections.Generic;
using System.Windows.Interop;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using MouseButton = System.Windows.Input.MouseButton;
using MouseButtonEventHandler = System.Windows.Input.MouseButtonEventHandler;

namespace KoTrans.App;

public partial class MainWindow
{
    private readonly ApiKeyStore vault;
    private readonly TranslationContexts contexts = new();
    private readonly List<Task> retiring = [];
    private TranslationPipeline? pipeline;
    private DesktopInput? input;
    private WindowTarget? target;
    private bool startingTranslation, automatic;
    internal Action<TranslationWindow>? PresentStartingOverlayOverride { get; set; }
    private int? translationStartFeedback;
    private PipelineUpdate? lastOverlayUpdate;
    private void RefreshOverlayControls()
    {
        if (WatchButton is not null) WatchButton.Content = TranslationButtonCaption(automatic && pipeline?.IsRunning == true);
        overlay?.SetAutomaticTranslation(automatic && pipeline?.IsRunning == true, startingTranslation);
        RefreshTrayTranslationCommand();
    }
    private long triggerGeneration;
    private Task? retiredLocalPreparation;
    private static async Task DrainCanceledPreparationAsync(Task preparation)
    {
        try { await preparation.ConfigureAwait(false); }
        catch (OperationCanceledException) when (preparation.IsCanceled) { } // Deliberate Stop/close cancellation is not a cleanup failure.
    }
    private QaFixtureWindow? qaFixture;
    private void InitTranslation()
    {
        QaFixtureButton.Visibility = App.MockAi ? Visibility.Visible : Visibility.Collapsed;
        input = new DesktopInput(this);
        input.ShowSettings += RestoreMainWindow;
        input.ToggleTranslation += async () => await ToggleTranslationAsync();
        input.Explain += () => QueueStudy(RunStudyAsync(CaptureStudySourceAsync));
        input.StudyScrollUp += () => study?.ScrollRemote(false); input.StudyScrollDown += () => study?.ScrollRemote(true);
        input.Trigger += async () => await RequestInputTriggerAsync();
        input.ClipboardChanged += () => ReadClipboard(false, false);
        RegisterSettingsHotkeys = input.Register;
        RegisterConfiguredHotkeys();
        RefreshHideSettingsButtonCaption();
    }
    private void FillTranslation()
    {
        RefreshDebugContext();
        InputMode.SelectedIndex = CaptureModeToSelectionIndex(Current.CaptureMode);
        UpdateLauncherTargetLabel();
        var info = TranslationProviderCatalog.Get(Current.Translation.Provider);
        var detail = info.RequiresModel
            ? (Current.Translation.Model.Length == 0 ? "모델 미설정" : Current.Translation.Model)
            : Current.Translation.Provider == TranslationProvider.DeepL ? Current.Translation.DeepLPlan.ToString()
            : Current.Translation.Provider == TranslationProvider.HyMtLocal ? (LocalTranslationEnvironment.Runtime.Backend == "CPU" ? "CPU 모드 · 번역이 느릴 수 있음" : "내 PC에서 처리") : "번역 전용";
        TranslationSummary.Text = !Current.ShowTranslation ? "원문만 표시 · 번역 API 사용 안 함" : App.MockAi ? "모의 AI 모드 · 외부 API 미사용" : $"{info.DisplayName} · {detail}";
        if (!automatic && !startingTranslation) WatchButton.Content = TranslationButtonCaption(false);
        overlay?.SetService(Current.Translation.Provider);
        RefreshHideSettingsButtonCaption();
        LauncherHotkeyHint.Text = $"{(Current.ShowTranslation ? "번역" : "원문 인식")} 시작·중지: {settings.Hotkeys.ToggleTranslation}  ·  메인 창 표시: {settings.Hotkeys.ShowSettings}";
        RefreshTrayTranslationCommand();
    }
    private void StopTranslation(bool cancelLocalPreparation = true)
    {
        completionStatus = null;
        StopObservingOcrExecution(); ocrPresentationError = false;
        RetireMarkerLearning();
        if (cancelLocalPreparation) localStartCancellation?.Cancel();
        if (cancelLocalPreparation && localPreparationTask is { IsCompleted: false } pendingLocal && !ReferenceEquals(retiredLocalPreparation, pendingLocal)) {
            retiredLocalPreparation = pendingLocal;
            retiring.Add(DrainCanceledPreparationAsync(pendingLocal));
        }
        triggerGeneration++; input?.Stop(); automatic = false;
        if (pipeline is { } active) { pipeline = null; active.Stop(); retiring.RemoveAll(t => t.IsCompletedSuccessfully); retiring.Add(active.DisposeAsync().AsTask()); }
        mockTranslationPipeline=null;
        if (ocr?.ExecutionStatus.Backend is "DirectML" or "Preparing") RetireOcrSession();
        if (WatchButton is not null) WatchButton.Content = TranslationButtonCaption(false);
        lastOverlayUpdate = null;
        overlay?.ClearPipelineNotices();
        overlay?.SetResult(null, "", ""); overlay?.SetBusy(false); RefreshOverlayControls();
        if (!startingTranslation) overlay?.ShowStoppedPrompt();
        UpdateOcrExecution();
    }
    internal async Task<bool> StartTranslationAsync(bool watch, string? initialSource = null, string? initialTranslation = null)
    {
        if (startingTranslation || closing || !ready) return false;
        if (markerLearningResetting) {
            ShowFeedback(FeedbackKind.Information,"학습 정보를 초기화하고 있어요","초기화가 끝난 뒤 다시 시작하세요.");
            return false;
        }
        if (translationStartFeedback == FeedbackCount) {
            operationToast.Dismiss(); toastExpires = null; toastFading = false; PlaceFeedback(); AdvanceFeedback();
        }
        translationStartFeedback = null;
        startingTranslation = true; StopTranslation(); InvalidateOcr(); captureCancellation?.Cancel();
        var profile = Current;
        using var localStart = new CancellationTokenSource(); localStartCancellation = localStart;
        TranslationWindow? startingOverlay = null;
        try
        {
            if (ocrTask is not null) { ReportTranslationStartFailure("수동 OCR이 끝난 뒤 다시 시작하세요."); return false; }
            RecognitionPreflight.ValidateStart(profile, watch);
            // Reject invalid source settings before engine work; keep actionable launch failures.
            var mode = profile.CaptureMode;
            WindowTarget? chosenWindow = null; PixelRect? region = null;
            if (mode == CaptureMode.Window) {
                target = RequireGameWindow(); chosenWindow = target;
                region = WindowRegion(profile, chosenWindow);
                WindowCaptureBounds.Resolve(getWindowBounds(chosenWindow), region);
            }
            ocrFactory.Validate(profile.OcrOptions());
            if (!profile.ShowOriginal && !profile.ShowTranslation) throw new InvalidDataException("원문 또는 한국어 번역 표시 중 하나 이상을 켜세요.");
            startingOverlay = CreateOverlayWindow(); startingOverlay.SetClickThrough(profile.OverlayClickThrough);
            startingOverlay.ShowPreparingPrompt(); RefreshOverlayControls();
            if (!string.IsNullOrWhiteSpace(initialSource)) startingOverlay.SetResult(initialSource, initialTranslation ?? "", profile.ShowTranslation ? "번역 준비 중…" : "원문 인식 준비 중…");
            if (PresentStartingOverlayOverride is { } present) present(startingOverlay); else startingOverlay.Show();
            // Let layout/render/input run before verification, Worker startup or model/session creation.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            localStart.Token.ThrowIfCancellationRequested();
            if (closing || Current.Id != profile.Id || !ReferenceEquals(overlay, startingOverlay)) return false;
            // Validate the dictionary before loading a model. Reuse this snapshot
            // so the engine and its context belong to the same start attempt.
            var preparedContext = PrepareProfileTranslationContext();
            if (profile.ShowTranslation && !App.MockAi && profile.Translation.Provider == TranslationProvider.HyMtLocal) {
                var owner = startingOverlay.IsVisible && (!IsVisible || WindowState == WindowState.Minimized)
                    ? (Window)startingOverlay : this;
                localPreparationTask = PrepareLocalTranslationAsync(profile.Id, localStart.Token, owner);
                if (!await localPreparationTask) return false;
                localStart.Token.ThrowIfCancellationRequested();
                if (closing || Current.Id != profile.Id || Current.Translation.Provider != TranslationProvider.HyMtLocal) return false;
                FillTranslation();
                // Background WPF tests replace the continuation before any native window/input is created.
                if (StartPreparedLocalOverride is not null) return await StartPreparedLocalOverride();
            }
            var ai = !profile.ShowTranslation ? null : App.MockAi ? (ITranslationService)new MockTranslationService() : TranslationServices.CreateUsingVault(profile.Translation, vault);
            // A new explicit start permits one new GPU attempt. Failed GPU sessions
            // are never retried by the frame watcher. CPU-only sessions stay reused.
            if (profile.OcrGpuAcceleration || ocr?.ExecutionStatus.Backend is "DirectML" or "Preparing" || ocr?.ExecutionStatus.IsFallback == true) RetireOcrSession();
            ocr = CurrentOcrService();
            var learning = await PrepareMarkerLearningAsync(profile,chosenWindow?.Identity,ocr,localStart.Token);
            localStart.Token.ThrowIfCancellationRequested();
            if(watch && learning is not null && chosenWindow is not null)
                learning.StartRegionObservation((sample,token)=>CaptureMarkerObservationAsync(profile,region,sample,token));
            var area = profile.CaptureArea;
            if (mode == CaptureMode.Window && region is not null) {
                profile.WindowCaptureRegion = region;
            }
            qaFixture?.ShowDiagnostics(mode != CaptureMode.Window);
            async Task<ImageFrame> CaptureSource(CancellationToken token)
            {
                var frame = await (await Dispatcher.InvokeAsync(() => {
                    token.ThrowIfCancellationRequested();
                    if (Current.Id != profile.Id) throw new OperationCanceledException(token);
                    return CaptureProfileAsync(mode, area, chosenWindow, region, !watch, token);
                }));
                learning?.ObserveFrame(frame);
                return frame;
            }
            var active = CreateProfilePipeline(learning ?? ocr, ai, CaptureSource, preparedContext);
            ObserveTranslation(active, profile.Id, watch);
            if (!string.IsNullOrWhiteSpace(initialSource)) overlay?.SetResult(initialSource, initialTranslation ?? "", "");
            StartProfileMonitoring(active, watch, initialText: profile.ShowTranslation && string.IsNullOrWhiteSpace(initialTranslation) ? initialSource : null);
            UpdateOcrExecution();
            WatchButton.Content = TranslationButtonCaption(watch);
            await SaveSettings();
            return !closing && Current.Id == profile.Id && pipeline == active && active.IsRunning;
        }
        catch (OperationCanceledException) {
            if (!closing && Current.Id == profile.Id && ReferenceEquals(overlay, startingOverlay)) model.Status = profile.ShowTranslation ? "번역 준비를 취소했습니다." : "원문 인식 준비를 취소했습니다.";
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopTranslation();
            if (ex is OcrInitializationException) ocrPresentationError = true;
            var reason = StartFailureMessage(ex, profile.ShowTranslation);
            if (ex is CaptureUnavailableException && profile.CaptureMode == CaptureMode.Window && target is null)
                ReportTranslationStartFailure(reason, profile.GameWindow is null ? "대상 게임 창을 선택하세요" : "대상 게임 창을 확인하세요",
                    profile.GameWindow is null ? "프로필 설정 → 캡처 · OCR에서 선택하세요." : reason);
            else ReportTranslationStartFailure(reason);
            return false;
        }
        finally {
            if (Current.Id == profile.Id && ReferenceEquals(overlay, startingOverlay)) startingOverlay?.FinishPreparingPrompt();
            if (ReferenceEquals(localStartCancellation, localStart)) localStartCancellation = null;
            localPreparationTask = null; startingTranslation = false; RefreshOverlayControls();
            UpdateOcrExecution();
        }
    }
    private void ReportTranslationStartFailure(string reason, string? title = null, string? detail = null)
    {
        if (debugTelemetry is not null) {
            long session = debugTelemetry.BeginSession(Current.OcrOptions(), Current.Translation, TranslationPipeline.DefaultSettle.TotalMilliseconds);
            debugTelemetry.Record(session, DebugCode.SESSION_START_FAILED, DebugReason.Rejected,
                v => v with { LastError = DebugReason.Rejected, TranslationState = DebugReason.Rejected });
        }
        model.Status = reason; overlay?.SetResult(null, null, reason);
        ShowFeedback(FeedbackKind.Error, title ?? (Current.ShowTranslation ? "자동 번역을 시작하지 못했어요" : "원문 인식을 시작하지 못했어요"), detail ?? reason, replaceError: true);
        translationStartFeedback = FeedbackCount;
    }
    private string TranslationButtonCaption(bool running) => (ready && !Current.ShowTranslation ? "원문 인식 " : "번역 ") + (running ? "중지" : "시작");
    internal static string StartFailureMessage(Exception error, bool translate) => error is TranslationException or CaptureUnavailableException or InvalidDataException or OcrInitializationException
        ? error.Message : translate ? "번역 준비 중 오류가 발생했습니다. 다시 시작해 주세요." : "원문 인식 준비 중 오류가 발생했습니다. 다시 시작해 주세요.";
    internal void StartProfileMonitoring(TranslationPipeline active, bool continuous, Action<RecognitionInputPlan>? startInput = null, string? initialText = null)
    {
        var plan = RecognitionInputPlan.For(Current, continuous);
        active.Start(plan.WatchScreen, initialText);
        if (!continuous) return;
        if (startInput is not null) startInput(plan);
        else input?.Start(Current.Triggers, () => WindowTargets.IsForeground(Current.CaptureMode == CaptureMode.Window ? target : null), plan);
    }
    internal async Task RequestInputTriggerAsync()
    {
        if (!automatic || !RecognitionInputPlan.For(Current, true).UseTriggers) return;
        overlay?.DismissInputPrompt();
        var request = ++triggerGeneration; var active = pipeline;
        await Task.Delay(Current.TriggerDelayMs); // Allow the game to draw, without intercepting its input.
        if (!closing && automatic && request == triggerGeneration && active is not null && active == pipeline
            && RecognitionInputPlan.For(Current, true).UseTriggers) await active.RequestTriggeredAsync();
    }
    private string ReadyStatus()
    {
        if (pipeline?.IsRunning == true) return Current.ShowTranslation ? "자동 번역이 실행 중입니다." : "자동 원문 인식이 실행 중입니다.";
        if (Current.CaptureMode == CaptureMode.Window)
            return Current.GameWindow is null ? "캡처 · OCR에서 대상 게임 창을 선택하세요." : $"게임을 실행한 뒤 {TranslationButtonCaption(false)}을 누르세요.";
        if (Current.CaptureMode == CaptureMode.Region && !Current.CaptureAreaConfigured) return "캡처 · OCR에서 인식 영역을 지정한 뒤 시작을 누르세요.";
        return Current.CaptureMode == CaptureMode.Clipboard ? "시작을 누르면 클립보드에 복사한 게임 텍스트를 읽습니다." : $"준비 완료 · {TranslationButtonCaption(false)}을 누르세요.";
    }
    // Reuses the production dispatcher/version guard with synthetic pipeline inputs in tests.
    internal void ObserveTranslation(TranslationPipeline active, Guid profileId, bool watch = false)
    {
        pipeline = active; automatic = watch; lastOverlayUpdate = null;
        var unresolvedErrors = new Dictionary<PipelineStatusArea, string>();
        string? warning = null;
        var warningArea = PipelineStatusArea.Input;
        FirstRunWizard?.Controller.Bind(active,profileId);
        var inputPlan = RecognitionInputPlan.For(Current, watch);
        overlay?.BeginSession(inputPlan.UseTriggers, inputPlan.WatchClipboard);
        active.ActivityChanged += (version, busy) => Dispatcher.BeginInvoke(() =>
        {
            if (pipeline != active || version != active.Version || Current.Id != profileId || closing) return;
            overlay?.SetBusy(busy);
            if (lastOverlayUpdate is { } previous) lastOverlayUpdate = previous with { IsBusy = busy };
        });
        active.Updated += update => Dispatcher.BeginInvoke(() =>
        {
            if (pipeline != active || update.Version != active.Version || Current.Id != profileId || closing) {
                debugTelemetry?.Record(active.DiagnosticSession, DebugCode.STALE_RESULT_DISCARDED, DebugReason.CandidateSuperseded); return;
            }
            if (update.State == PipelineState.Stopped) automatic = false;
            if (update.State == PipelineState.Stopped) { StopObservingOcrExecution(); ocrPresentationError = false; }
            UpdateOcrExecution();
            if (update.State == PipelineState.Stopped) { unresolvedErrors.Clear(); warning = null; }
            foreach (var area in unresolvedErrors.Keys.ToArray())
                if ((update.RecoveredAreas & area) != 0) unresolvedErrors.Remove(area);
            if (update.State == PipelineState.Error)
                unresolvedErrors[update.FailureArea == PipelineStatusArea.None ? PipelineStatusArea.Ocr : update.FailureArea] = update.Message;
            if ((update.RecoveredAreas & warningArea) != 0) warning = null;
            if (update.Important) { warning = update.Message; warningArea = update.FailureArea == PipelineStatusArea.None ? PipelineStatusArea.Input : update.FailureArea; }
            var status = unresolvedErrors.Count > 0 ? unresolvedErrors.Values.Last() : warning ?? update.Message;
            // Keep the event and subtitle/diagnostic updates; omit only the fleeting internal status.
            if (unresolvedErrors.Count > 0 || warning is not null || update.Message != "인식 완료 · 문장 확인 중")
                model.Status = (App.MockAi ? "[모의 AI] " : "") + status;
            if (update.Original is not null) OcrText.Text = update.Original;
            if (update.Translation is not null) TranslatedText.Text = update.Translation;
            lastOverlayUpdate = update;
            overlay?.SetPipelineResult(update);
            WizardSubtitleApplied(active,profileId,update);
            debugTelemetry?.SetContext(v => v with { HttpAttempts = update.HttpAttempts });
            if (debugTelemetry is not null && overlay is { } displayed && (update.Original is not null || update.Translation is not null)) {
                var actual = displayed.RenderedSubtitleContent;
                debugTelemetry.OverlayApplied(active.DiagnosticSession, displayed.IsVisible, update.Original, update.Translation,
                    actual.Source, actual.Translation);
            }
            RefreshOverlayControls();
            qaFixture?.Report(update, overlay?.CaptureExcluded == true);
            model.Timing = $"OCR {update.OcrRequests}회 · 번역 {update.TranslationRequests}회 · HTTP {update.HttpAttempts}회 · 재사용 {update.CacheHits}회"
                + (update.Ocr is null ? "" : $" · 캡처 {update.CaptureMs:F0} / 검출 {update.Ocr.DetectionTime.TotalMilliseconds:F0} / 인식 {update.Ocr.RecognitionTime.TotalMilliseconds:F0} / 후처리 {update.Ocr.PostprocessTime.TotalMilliseconds:F0} ms")
                + $" · AI {update.ApiMs:F0} / 전체 {update.TotalMs:F0} ms";
        });
    }
    private void EnsureCaptureSafe(PixelRect bounds,CaptureCover? presentationCover=null)
    {
        // Only a presenter bound to the validated foreground game supplies this override.
        // Its visible image, rather than the smaller hidden original client, covers app windows below it.
        var cover = presentationCover ?? WindowTargets.ForegroundCaptureCover();
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (!NeedsCaptureOverlapCheck(window.IsVisible, window.WindowState == WindowState.Minimized, window == qaFixture,
                window == overlay && overlay.CaptureExcluded || window == study && study.CaptureExcluded || window is OverlayChromeWindow { CaptureExcluded: true }
                || window is FirstRunWizardWindow {CaptureExcluded:true} || DebugOverlayWindow.ExcludedForCapture(window))) continue;
            var handle = new WindowInteropHelper(window).Handle;
            var state = WindowTargets.CaptureState(handle);
            bool above = cover is not null && state.Bounds is { } area && WindowTargets.Overlaps(area, bounds)
                && WindowTargets.IsAbove(cover.Handle, handle);
            if (WindowTargets.BlocksCapture(state, bounds, cover?.Bounds, above))
                throw new CaptureUnavailableException(CaptureOverlapMessage(window == this ? "설정창" : window == overlay ? "번역창" : window == study ? "학습창" : window is FirstRunWizardWindow ? "처음 사용하기 안내 창" : "추가 설정창"), CaptureWaitReason.Occluded);
        }
    }
    internal static string CaptureOverlapMessage(string windowName) => $"KO Trans 2 {windowName}이 캡처 영역을 가립니다. 해당 창을 숨기거나 최소화하거나, 게임 창을 앞으로 가져오세요.";
    internal static bool NeedsCaptureOverlapCheck(bool visible, bool minimized, bool fixture, bool captureExcluded)
        => visible && !minimized && !fixture && !captureExcluded;
    private async Task ToggleTranslationAsync()
    {
        if (automatic) { StopTranslation(); model.Status = Current.ShowTranslation ? "번역을 중지했습니다." : "원문 인식을 중지했습니다."; }
        else await StartMainTranslationAsync(hideMainForStart: false);
    }
    private async void ToggleWatch(object sender, RoutedEventArgs e) => await ToggleLauncherTranslationAsync();
    private async Task ToggleLauncherTranslationAsync()
    {
        if (automatic) await ToggleTranslationAsync();
        else await StartMainTranslationAsync(hideMainForStart: true);
    }
    private async void TranslateNow(object sender, RoutedEventArgs e) => await ManualTranslation(false, false);
    private async void Retranslate(object sender, RoutedEventArgs e) => await ManualTranslation(true, false);
    private async void TranslateImage(object sender, RoutedEventArgs e) => await ManualTranslation(false, true);
    private async Task ManualTranslation(bool force, bool image)
    {
        if (image && lastFrame is null) { model.Status = "먼저 이미지를 열거나 캡처하세요."; return; }
        if (pipeline is null && !await StartTranslationAsync(false)) return;
        if (Current.CaptureMode == CaptureMode.Clipboard && !image) ReadClipboard(true, force);
        else if (pipeline is { } active) await active.RequestManualAsync(force, image ? lastFrame : null);
    }
    private void ReadClipboard(bool manual, bool force)
    {
        if (pipeline is null || Current.CaptureMode != CaptureMode.Clipboard || (!manual && !automatic)) return;
        try {
            if (System.Windows.Clipboard.ContainsText()) pipeline.SubmitClipboard(System.Windows.Clipboard.GetText(), manual, force);
            else if (manual) pipeline.SubmitClipboard("", manual, force);
        }
        catch (ExternalException) { model.Status = "클립보드가 사용 중입니다. 다음 클립보드 변경 때 다시 확인합니다."; }
    }
    private async void ConfigureTranslation(object sender, RoutedEventArgs e)
    {
        var previous = CaptureEditorState();
        var dialog = CreateTranslationSettings(); dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        { ApplyEditorEffects(previous); RebuildEmbeddedProfileEditor(); await SaveSettings(); }
    }
    private void ResetContext(object sender, RoutedEventArgs e) { StopTranslation(); contexts.Reset(Current.Id); OcrText.Clear(); TranslatedText.Clear(); model.Status = "현재 프로필의 대화 문맥을 초기화했습니다."; }
    private void HideSettings(object sender, RoutedEventArgs e)
    {
        RefreshHideSettingsButtonCaption();
        if (tray?.IsAvailable == true) HideToTray();
        else if (input?.CanShowSettings == true) Hide();
        else { model.Status = "메인 창 표시 단축키가 충돌해 작업 표시줄로 최소화합니다."; WindowState = WindowState.Minimized; }
    }
    private void InputModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changing || InputMode.SelectedIndex < 0) return;
        InvalidateCaptureInput("원문 입력 모드가 변경되어 이전 문장 해설을 비웠습니다.");
        Current.CaptureMode = CaptureModeFromSelectionIndex(InputMode.SelectedIndex);
        ReconnectTarget(); FillTranslation();
    }
    private void PickWindow(object sender, RoutedEventArgs e)
    {
        if (Current.CaptureMode != CaptureMode.Window) return;
        StopTranslation(); InvalidateStudy("캡처 대상 선택을 시작하여 이전 문장 해설을 비웠습니다."); gameQa.Clear();
        var picker = CreateTargetPicker(); picker.Owner = this; picker.ShowDialog();
    }
    private bool selectingTargetWindow;
    private async void PickWindowDirect(object sender, RoutedEventArgs e)=>await SelectGameWindowDirectAsync();
    internal async Task SelectGameWindowDirectAsync()
    {
        if (!ready || closing || selectingTargetWindow || selectingArea || Current.CaptureMode != CaptureMode.Window) return;
        selectingTargetWindow = true;
        StopTranslation(); InvalidateStudy("캡처 대상 선택을 시작하여 이전 문장 해설을 비웠습니다."); gameQa.Clear();
        var profileId = Current.Id;
        bool restore = IsVisible;
        try
        {
            // Hide this settings window so the game is available to click anywhere.
            if (restore) Hide();
            var picked = await WindowClickSelector.PickAsync(findWindows);
            if (picked.Canceled) { model.Status = "대상 창 직접 클릭을 취소했습니다."; return; }
            if (picked.Target is null)
            {
                model.Status = "클릭한 위치에 선택 가능한 게임 창이 없습니다. 창 목록에서 선택하거나 다시 시도하세요.";
                return;
            }
            await CommitPickedWindowAsync(picked.Target, profileId, message => model.Status = message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { model.Status = "직접 클릭 선택 창을 표시하지 못했습니다. 대상 창 선택 목록을 이용해 주세요."; }
        finally
        {
            selectingTargetWindow = false;
            if (restore && !closing) { Show(); Activate(); }
        }
    }
    internal async Task<bool> CommitPickedWindowAsync(WindowTarget chosen, Guid expectedProfile, Action<string> report,System.Collections.Generic.IReadOnlyList<WindowTarget>? knownWindows=null)
    {
        if (closing || Current.Id != expectedProfile)
        {
            report("프로필이 변경되었습니다. 현재 프로필에서 창을 다시 선택하세요.");
            return false;
        }
        try
        {
            if (!ApplyProfileName()) { report(model.Status); return false; }
            SelectGameWindow(chosen);
            if (!await SaveSettings()) { report(model.Status); return false; }
            if (closing || Current.Id != expectedProfile) return false;
            ReconnectTarget(knownWindows);
            report("대상 창을 저장했습니다.");
            return true;
        }
        catch (Exception ex) when (ex is CaptureUnavailableException or InvalidDataException)
        {
            report(ex.Message);
            return false;
        }
    }
    internal Window CreateTargetPicker()
    {
        var profileId = Current.Id;
        var picker = new Window { Title = "캡처할 게임 창 선택", Width = 700, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { Name = "TargetCandidates", ItemsSource = findWindows() };
        var status = new TextBlock { Name = "TargetPickerStatus", TextWrapping = TextWrapping.Wrap };
        var select = new Button { Name = "TargetPickerSelect", Content = "선택" };
        var refresh = new Button { Name = "TargetPickerRefresh", Content = "새로 고침" };
        // Apply the launcher palette without touching picker refresh/selection semantics.
        TargetPickerTheme.Apply(picker, list, status, select, refresh);
        refresh.Click += (_, _) => list.ItemsSource = findWindows();
        bool confirming = false;
        async Task ConfirmAsync(WindowTarget chosen)
        {
            if (confirming) return;
            confirming = true;
            select.IsEnabled = refresh.IsEnabled = false;
            try
            {
                if (await CommitPickedWindowAsync(chosen, profileId, message => status.Text = message)
                    && picker.IsVisible) picker.DialogResult = true;
            }
            finally
            {
                confirming = false;
                select.IsEnabled = refresh.IsEnabled = true;
            }
        }
        select.Click += async (_, _) =>
        {
            if (list.SelectedItem is WindowTarget chosen) await ConfirmAsync(chosen);
        };
        list.ItemContainerStyle.Setters.Add(new EventSetter(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(async (sender, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { Content: WindowTarget chosen }) return;
            e.Handled = true;
            await ConfirmAsync(chosen);
        })));
        return picker;
    }
    private void ShowQaFixture(object sender, RoutedEventArgs e)
    {
        if (!App.MockAi) return;
        if (qaFixture is null)
        {
            qaFixture = new QaFixtureWindow(area => { StopTranslation(); Current.CaptureArea = area; Current.CaptureAreaConfigured = true; model.Area = area.ToString(); });
            qaFixture.Closed += (_, _) => qaFixture = null;
        }
        qaFixture.Show(); qaFixture.Activate();
    }
}
