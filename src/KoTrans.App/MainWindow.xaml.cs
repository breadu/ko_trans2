using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

namespace KoTrans.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    private readonly SettingsStore store;
    private readonly Func<AppSettings, Task> persistSettings;
    private readonly Action<string> copyOcrText;
    private AppSettings settings = new();
    private Profile Current => settings.Profiles.First(p => p.Id == settings.ActiveProfileId);
    private TranslationWindow? overlay;
    private bool overlayClosedByUser;
    internal Func<string, bool>? ConfirmShutdownFailureOverride { get; set; }
    private StudyWindow? study;
    private CancellationTokenSource? captureCancellation;
    private bool ready, changing, closeApproved, closing;
    private ImageFrame? lastFrame;
    private IOcrService? ocr;
    private readonly OcrServiceFactory ocrFactory;
    private readonly OcrServiceManager ocrManager;
    internal IOcrService CurrentOcrService()
    {
        if (pipeline?.IsRunning != true || ocr is null) ocr = ocrManager.Select(Current.Id, Current.OcrOptions());
        ocrPresentationError = false;
        UpdateOcrExecution(); return ocr;
    }
    private void RetireOcrSession()
    {
        StopObservingOcrExecution(); ocrPresentationError = false;
        foreach (var activity in studyOcrActivities)
            if (ReferenceEquals(activity.Service, ocr)) { activity.Status = ocr.ExecutionStatus; activity.Retired = true; }
        ocr = null; ocrManager.Retire();
        lastOcrGpuDevice = "";
        UpdateOcrExecution();
    }
    private CancellationTokenSource? ocrCancellation;
    private Task<OcrResult>? ocrTask;
    private long generation;
    private readonly TextDuplicateFilter duplicate = new();
    private void InvalidateOcr(string studyReason = "원문 언어 또는 읽기 설정이 변경되어 이전 문장 해설을 비웠습니다.")
    {
        StopTranslation(); InvalidateStudy(studyReason); gameQa.Clear(); generation++; ocrCancellation?.Cancel(); duplicate.Reset();
        if (OcrText is not null) OcrText.Text = "";
        if (TranslatedText is not null) TranslatedText.Text = "";
    }
    private readonly string[] colors = ["#FFFFFF", "#D3D3D3", "#FFE68A", "#A5DEFF", "#FF819A", "#D3A7FF"];
    public MainWindow() : this(new SettingsStore(App.DataDirectory, ReleaseIdentity.For(typeof(App).Assembly)), new ApiKeyStore(App.DataDirectory), useLocalModelData: true, getWindowScale: WindowScaleDiscovery.Find, useOriginalWindowCapture: LosslessCaptureDiscovery.IsCovering) { }
    internal MainWindow(SettingsStore store, ApiKeyStore vault, Func<IReadOnlyList<WindowTarget>>? findWindows = null, Func<WindowTarget, PixelRect>? getWindowBounds = null, Func<string, string?>? pickOverlayColor = null, OcrServiceFactory? ocrFactory = null, Func<TimeSpan>? feedbackClock = null, Func<AppSettings, Task>? persistSettings = null, Func<GameWindowIdentity, CancellationToken, Task<GameLaunchOutcome>>? launchGame = null, Action<string>? copyOcrText = null, bool useLocalModelData = false, Func<WindowTarget,WindowScaleTransform?>? getWindowScale = null, Func<WindowTarget,bool>? useOriginalWindowCapture=null)
    {
        this.store = store; this.vault = vault; this.useLocalModelData = useLocalModelData;
        this.copyOcrText = copyOcrText ?? System.Windows.Clipboard.SetText;
        this.persistSettings = persistSettings ?? (value => store.SaveAsync(value));
        this.ocrFactory = ocrFactory ?? new(OcrEngineCatalog.Default, OcrModelPaths.FindDirectory());
        ocrManager = new(this.ocrFactory);
        this.findWindows = findWindows ?? (() => WindowTargets.List(App.MockAi));
        this.getWindowBounds = getWindowBounds ?? (w => WindowTargets.Resolve(w, false));
        this.getWindowScale = getWindowScale ?? (_=>null); // Background fixtures never query native presenter windows.
        this.useOriginalWindowCapture=useOriginalWindowCapture ?? (_=>false);
        this.pickOverlayColor = pickOverlayColor ?? PickWindowsColor;
        this.launchGame = launchGame ?? GameLauncher.LaunchAsync;
        InitializeComponent(); DataContext = model;
        AppVersionText.Text = AppVersionInfo.StatusLabel(App.IsDev);
        AppVersionText.ToolTip = AppVersionInfo.StatusTooltip(App.IsDev);
        Title = "KO Trans 2" + (App.IsDev ? " · DEV-RUN" : "") + (App.MockAi ? " · MOCK AI" : "");
        Closed += (_, _) => dictionaryPopup?.Dispose();
        Closed += (_, _) => FirstRunWizard?.Close();
        Closed += (_, _) => { BasicPage.Dispose(); CapturePage.Dispose(); AdvancedPage.Dispose(); SubtitlePage.Dispose(); embeddedProfileEditor?.Dispose(); };
        Closed += (_, _) => { gameLaunchLifetime.Cancel(); gameLaunchLifetime.Dispose(); };
        ContentRendered += FirstRender; Closing += OnClosing; Closed += (_, _) => DisposeTray();
        InitializeResponsiveLayout();
        InitializeFeedback(feedbackClock);
    }
    internal async Task PrepareStartupAsync()
    {
        try
        {
            ApplyLoadedSettings(await store.LoadAsync());
            if (store.MigratedRetiredOcrModel)
                model.Status = "이전에 사용하던 OCR 모델이 지원 종료되어 PaddleOCR v6로 변경되었습니다.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { model.Status = ex.Message; model.Timing = "설정 로딩 실패 · 원본 설정을 보존했습니다."; }
        PrepareInitialPlacement(settings.MainWindow);
    }
    private async void FirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= FirstRender;
        InitializeTray();
        if (!ready || closing || closeApproved) return;
        InitTranslation();
        var renderedMs = App.ProcessElapsedMs;
        model.Timing = $"이번 실행: 첫 화면 {renderedMs:F0} ms · 설정은 표시 전 적용 · 시작 시 OCR 모델 미로딩";
        try { await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, "startup-metrics.json"), System.Text.Json.JsonSerializer.Serialize(new { firstRenderMs = renderedMs, uiReadyMs = renderedMs, ocrReadyMs = (double?)null, firstOcrMs = (double?)null })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { model.Timing += " · 시작 시간 파일 저장 실패"; }
    }
    // Native Desktop fixtures render the real UI without installing global
    // hooks/tray or writing startup metrics to the real user data directory.
    internal void SuppressDesktopStartupForTest() => ContentRendered -= FirstRender;
    // The same control binding path is used after disk loading and by isolated background tests.
    internal void ApplyLoadedSettings(AppSettings loaded)
    {
        loaded.Validate(); settings = loaded; ready = true; RefreshProfiles(); Workspace.IsEnabled = true; model.Status = ReadyStatus();
    }
    private void RefreshProfiles()
    {
        ((DefaultProfileConverter)Resources["IsDefaultProfile"]).DefaultProfileId = settings.DefaultProfileId;
        changing = true; Profiles.ItemsSource = null; Profiles.ItemsSource = settings.Profiles; Profiles.SelectedItem = Current;
        FillProfile(); changing = false; ReconnectTarget();
        RebuildEmbeddedProfileEditor();
        RefreshProfilePage();
    }
    private void RefreshCaptureAreaSummary()
    {
        model.Area = CaptureAreaDescription.ForProfile(Current);
        model.CompactArea = CaptureAreaDescription.CompactForProfile(Current);
        var preview = CaptureAreaDescription.PreviewForProfile(Current);
        model.CaptureAreaBasis = preview.Basis;
        model.CaptureAreaPosition = preview.Position;
        model.CaptureAreaSize = preview.Size;
        RefreshCaptureMap();
    }
    private void FillProfile()
    {
        var p = Current; VerticalText.IsChecked = p.Vertical; ProfileName.Text = p.Name; SourceLanguage.SelectedIndex = p.Language == "일본어" ? 1 : 0;
        CopyOcrResultToClipboard.IsChecked = p.CopyOcrToClipboard;
        OriginalCaptureWhenScaled.IsChecked=p.OriginalCaptureWhenScaled;
        RefreshMarkerLearningControls();
        VerticalText.Visibility = p.Language == "일본어" ? Visibility.Visible : Visibility.Collapsed;
        JapaneseFuriganaOption.IsChecked = p.ShowJapaneseFurigana;
        JapaneseFuriganaOption.Visibility = p.Language == "일본어" ? Visibility.Visible : Visibility.Collapsed;
        ReadingMode.SelectedIndex = p.ReadingMode == "NVL" ? 1 : 0; OpacitySlider.Value = p.OverlayOpacity; FontSlider.Value = p.OverlayFontSize; OriginalFontSlider.Value = p.OverlayOriginalFontSize;
        FillOverlayColor(FontColor, TranslationColorPicker, p.OverlayColor);
        FillOverlayColor(OriginalFontColor, OriginalColorPicker, p.OverlayOriginalColor);
        ShowOriginal.IsChecked = p.ShowOriginal; ShowKorean.IsChecked = p.ShowTranslation; RefreshCaptureAreaSummary();
        StudyTopmostOption.IsChecked = settings.StudyTopmost;
        OverlayPassThrough.IsChecked = p.OverlayClickThrough;
        FillWordDictionary();
        FillTranslation();
        FillOcrEngines();
        DeleteProfileButton.IsEnabled = !feedbackOperationBusy && !settings.CanEditGlobal;
        // Keep the explanation's layout slot even when viewing a game profile.
        DefaultProfileExplanation.Visibility = settings.CanEditGlobal ? Visibility.Visible : Visibility.Hidden;
        ProfileName.IsReadOnly = settings.CanEditGlobal;
        RenameProfileButton.IsEnabled = !feedbackOperationBusy && !settings.CanEditGlobal;
        TranslationSettingsButton.Content = settings.CanEditGlobal ? "AI · 키 · 입력 설정" : "AI · 입력 설정";
        study?.BindProfile(Current); study?.RefreshGlobalAccess();
        foreach (var dialog in profileDialogs.ToArray()) dialog.RefreshAccess();
        if (GlobalSettingsPageItem is not null)
            GlobalSettingsPageItem.Visibility = settings.CanEditGlobal ? Visibility.Visible : Visibility.Collapsed;
        if (LauncherTargetLabel is not null)
            UpdateLauncherTargetLabel();
        RefreshSubtitlePreview();
    }
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changing || Profiles.SelectedItem is not Profile p) return;
        await SwitchProfileAsync(p);
    }
    internal async Task<bool> SwitchProfileAsync(Profile p)
    {
        if (!settings.Profiles.Contains(p)) throw new ArgumentException("프로필을 찾을 수 없습니다.");
        if (feedbackOperationBusy) {
            bool previous = changing; changing = true; Profiles.SelectedItem = Current; changing = previous;
            return false;
        }
        if (Current.Id == p.Id) return await SaveCurrentProfileAsync();
        return await PerformProfileFeedbackAsync("해당 프로필로 전환했어요", feedback => SwitchProfileCoreAsync(p, feedback));
    }
    private TaskCompletionSource? profileChangePending;
    private async Task<bool> SwitchProfileCoreAsync(Profile? p, SaveFeedback feedback, bool deleteCurrent = false)
    {
        bool wasChanging = changing; changing = true; Profiles.SelectedItem = Current; changing = wasChanging;
        if (!ApplyProfileName() || !ApplyEmbeddedProfileEditor()) return false;
        study?.CommitPlacement();
        ApplyOptions(); overlay?.CommitPlacement();
        var proposal = settings.CopyProfileMembership();
        bool creating = p is null;
        p ??= proposal.CreateGameProfile("새 게임 프로필");
        var previous = Current;
        if (deleteCurrent) proposal.DeleteProfile(previous.Id);
        proposal.ActiveProfileId = p.Id;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profileChangePending = pending;
        ProfileSettingsView.IsEnabled = false;
        try {
            if (!await SaveSettings(feedback, proposal: proposal)) return false;
            if (creating) settings.Profiles.Add(p);
            ActivateSavedProfile(p, deleteCurrent ? previous : null);
            return true;
        } finally {
            ProfileSettingsView.IsEnabled = true;
            profileChangePending = null;
            pending.SetResult();
        }
    }
    private void ActivateSavedProfile(Profile p, Profile? removed)
    {
        bool reopenOverlay = overlay?.IsVisible == true;
        InvalidateOcr("프로필이 변경되어 이전 문장 해설을 비웠습니다."); RetireOcrSession(); lastFrame = null; captureCancellation?.Cancel();
        if (Current.Translation.LocalModel != p.Translation.LocalModel) ReleaseLocalModelForChange();
        var replacedOverlay = overlay; overlay = null; replacedOverlay?.CloseForReplacement(); overlayClosedByUser = false;
        if (removed is not null) settings.DeleteProfile(removed.Id);
        settings.ActiveProfileId = p.Id; model.Preview = null;
        RefreshProfiles(); model.Status = $"프로필을 변경했습니다. {TranslationButtonCaption(false)}을 누르세요.";
        if (reopenOverlay) ShowTranslation(this, new RoutedEventArgs());
    }
    private async void NewProfile(object sender, RoutedEventArgs e) => await CreateProfileWithFeedbackAsync();
    internal Task<bool> CreateProfileWithFeedbackAsync() => PerformProfileFeedbackAsync("새 프로필을 만들었어요", async feedback =>
    {
        if (settings.Profiles.Count >= 200) { model.Status = "프로필은 200개까지 만들 수 있습니다."; return false; }
        bool created = await SwitchProfileCoreAsync(null, feedback);
        if (created) feedback.SuccessDetail = "기본 설정 복사 완료 · 게임 창과 인식 영역은 새로 지정하세요.";
        return created;
    });
    private bool ApplyProfileName()
    {
        if (settings.CanEditGlobal) { ProfileName.Text = Current.Name; return true; }
        string name = ProfileName.Text.Trim();
        if (name.Length is < 1 or > 80) { model.Status = "프로필 이름을 1–80자로 입력하세요."; return false; }
        name = settings.GetUniqueProfileName(name, Current.Id);
        Current.Name = name;
        ProfileName.Text = name; return true;
    }
    internal Task<bool> SaveCurrentProfileAsync() => SaveCurrentProfileCoreAsync(null);
    private async Task<bool> SaveCurrentProfileCoreAsync(SaveFeedback? feedback)
    {
        bool wasRunning = pipeline?.IsRunning == true;
        if (!ApplyProfileName() || !ApplyEmbeddedProfileEditor()) return false;
        ApplyOptions();
        // No HWND is created for background controls; remember actual windows only when the app is displayed.
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero) RememberWindows();
        bool stopped = wasRunning && pipeline?.IsRunning != true;
        if (stopped && feedback is not null) feedback.SuccessDetail = "자동 처리를 중지했습니다. 시작 버튼으로 다시 시작하세요.";
        return await SaveSettings(feedback, stopped ? "자동 처리 중지 · 설정 저장 실패: " : "", validatedForm: true);
    }
    private async void RenameProfile(object sender, RoutedEventArgs e) => await RenameProfileWithFeedbackAsync();
    internal Task<bool> RenameProfileWithFeedbackAsync() => PerformProfileFeedbackAsync("프로필 이름을 변경했어요", feedback => {
        if (settings.CanEditGlobal) { model.Status = "기본 프로필 이름은 변경할 수 없습니다."; return Task.FromResult(false); }
        return SaveCurrentProfileCoreAsync(feedback);
    });
    private async void DeleteProfile(object sender, RoutedEventArgs e)
    {
        if (feedbackOperationBusy) return;
        if (settings.CanEditGlobal) { model.Status = "기본 프로필은 삭제할 수 없습니다."; return; }
        if (MessageBox.Show(this, "선택한 프로필을 삭제할까요?", "프로필 삭제", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await DeleteProfileWithFeedbackAsync();
    }
    // The confirmation remains in the click handler; background tests exercise the confirmed operation.
    internal Task<bool> DeleteProfileWithFeedbackAsync() => PerformProfileFeedbackAsync("프로필을 삭제했어요", async feedback => {
        if (settings.CanEditGlobal) { model.Status = "기본 프로필은 삭제할 수 없습니다."; return false; }
        var removed = Current;
        feedback.SuccessDetail = removed.Name;
        return await SwitchProfileCoreAsync(settings.DefaultProfile, feedback, deleteCurrent: true);
    });
    private void OptionChanged(object sender, SelectionChangedEventArgs e) => ApplyOptions();
    private void OriginalChanged(object sender, RoutedEventArgs e) => ApplyOptions();
    private void StyleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyOptions();
    private void ApplyOptions()
    {
        if (!ready || changing) return;
        if (Current.Language != (SourceLanguage.SelectedIndex == 1 ? "일본어" : "영어") || Current.ReadingMode != (ReadingMode.SelectedIndex == 1 ? "NVL" : "ADV") || Current.Vertical != (VerticalText.IsChecked == true)) InvalidateOcr();
        Current.Vertical = VerticalText.IsChecked == true;
        Current.Language = SourceLanguage.SelectedIndex == 1 ? "일본어" : "영어";
        Current.ReadingMode = ReadingMode.SelectedIndex == 1 ? "NVL" : "ADV";
        VerticalText.Visibility = Current.Language == "일본어" ? Visibility.Visible : Visibility.Collapsed;
        Current.ShowJapaneseFurigana = JapaneseFuriganaOption.IsChecked == true;
        JapaneseFuriganaOption.Visibility = Current.Language == "일본어" ? Visibility.Visible : Visibility.Collapsed;
        RefreshOcrAvailability();
        Current.OverlayOpacity = OpacitySlider.Value; Current.OverlayFontSize = FontSlider.Value; Current.OverlayOriginalFontSize = OriginalFontSlider.Value;
        if (FontColor.SelectedItem is string selectedColor) Current.OverlayColor = selectedColor;
        if (OriginalFontColor.SelectedItem is string sourceColor) Current.OverlayOriginalColor = sourceColor;
        UpdateColorSwatch(TranslationColorPicker,Current.OverlayColor); UpdateColorSwatch(OriginalColorPicker,Current.OverlayOriginalColor);
        Current.ShowOriginal = ShowOriginal.IsChecked == true;
        Current.ShowTranslation = ShowKorean.IsChecked == true;
        overlay?.Apply(Current);
        RefreshSubtitlePreview();
    }
    private async Task<bool> SaveSettings(SaveFeedback? feedback = null, string errorPrefix = "", AppSettings? proposal = null,
        bool validatedForm = false)
    {
        while (proposal is null && profileChangePending is { } pending) await pending.Task;
        var toSave = proposal ?? settings;
        long serial = ++saveRequestSerial;
        try {
            var selected = toSave.Profiles.First(p => p.Id == toSave.ActiveProfileId);
            string? snapshot = feedback is null ? null : System.Text.Json.JsonSerializer.Serialize(toSave);
            Guid profile = selected.Id; string name = selected.Name;
            string engine = selected.OcrEngineId, preset = selected.OcrModelPresetId;
            await persistSettings(toSave);
            if (feedback is not null) { feedback.Snapshot = snapshot; feedback.Serial = serial; feedback.ProfileId = profile; feedback.ProfileName = name; }
            if (serial >= saveSettledSerial) {
                saveSettledSerial = serial;
                if (serial == saveRequestSerial && saveFailureFeedback is not null
                    && (feedback is not null || validatedForm)
                    && (!formValidationFailed || validatedForm || proposal is not null)) ClearSavedFailure();
                if (serial == saveRequestSerial) ClearSavedOcrInformation(profile, engine, preset);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) {
            if (feedback is not null) feedback.FailureReported = true;
            if (serial >= saveSettledSerial) {
                model.Status = "설정 저장 실패 · 저장 위치와 권한을 확인하세요."; saveSettledSerial = serial;
                ReportSaveFailure(true, SaveFailureDetail(ex));
            }
            return false;
        }
    }
    private async void SaveClicked(object sender, RoutedEventArgs e) => await SaveWithFeedbackAsync();
    private void RememberWindows()
    {
        if (WindowState == WindowState.Normal)
            settings.MainWindow = DesktopWindows.Remember(this);
        if (FirstRunWizard?.LastNormalPlacement is { } wizardPlacement)
            settings.FirstRunWizardWindow = wizardPlacement;
        if (overlay is not null) overlay.CommitPlacement();
        study?.CommitPlacement();
    }
    private Window[] HideWindows()
    {
        var windows = System.Windows.Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray();
        foreach (var w in windows) w.Hide(); return windows;
    }
    private static void RestoreWindows(Window[] windows) { foreach (var w in windows) if (System.Windows.Application.Current.Windows.Cast<Window>().Contains(w)) w.Show(); }
    private async void Capture(object sender, RoutedEventArgs e)
    {
        if (captureCancellation is not null) return;
        var p = Current; var area = p.CaptureArea; var windows = HideWindows();
        try {
            await CaptureFrameAsync(async token => {
                await Task.Delay(180, token);
                return await CaptureProfileAsync(p.CaptureMode, area, target,
                    p.CaptureMode == CaptureMode.Window && target is not null ? WindowRegion(p, target) : null, true, token);
            });
        }
        finally { if (!closing) { RestoreWindows(windows); Activate(); } }
    }
    // Share completion handling with synthetic UI tests without opening/hiding windows or capturing a desktop.
    internal async Task CaptureFrameAsync(Func<CancellationToken, Task<ImageFrame>> acquire)
    {
        if (captureCancellation is not null || closing) return;
        InvalidateOcr("새 원문을 캡처하기 위해 이전 문장 해설을 비웠습니다."); lastFrame = null; model.Preview = null;
        long request = generation; var profile = Current.Id;
        bool IsCurrent() => !closing && generation == request && Current.Id == profile;
        using var cancellation = new CancellationTokenSource(); captureCancellation = cancellation;
        try
        {
            var frame = await acquire(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            lastFrame = frame;
            var bitmap = PreviewFrame(frame);
            model.Preview = bitmap; model.Status = $"캡처 완료 · {frame.Width} × {frame.Height} px";
        }
        catch (OperationCanceledException) { if (IsCurrent()) model.Status = "캡처를 취소했습니다."; }
        catch (Exception ex) when (ex is CaptureUnavailableException or ArgumentException or System.ComponentModel.Win32Exception or ExternalException or IOException)
        { if (IsCurrent()) model.Status = "화면 캡처 실패: " + ex.Message; }
        finally { captureCancellation = null; }
    }
    private void CancelCapture(object sender, RoutedEventArgs e)
    {
        StopTranslation(); captureCancellation?.Cancel(); generation++; ocrCancellation?.Cancel(); model.Status = "작업을 취소했습니다.";
    }
    private static BitmapSource PreviewFrame(ImageFrame frame)
    {
        if (!MemoryMarshal.TryGetArray(frame.Pixels, out ArraySegment<byte> bytes) || bytes.Offset != 0) throw new InvalidDataException("지원하지 않는 이미지 버퍼입니다.");
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, frame.PixelFormat == FramePixelFormat.Bgra32 ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, bytes.Array!, frame.Stride);
        bitmap.Freeze(); return bitmap;
    }
    private Task<ImageFrame>? imageLoadTask;
    private async void LoadImage(object sender, RoutedEventArgs e)
    {
        if (imageLoadTask is not null || closing) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp" };
        if (picker.ShowDialog(this) != true) return;
        InvalidateOcr("새 원문 이미지를 불러오기 위해 이전 문장 해설을 비웠습니다."); var request = generation; var profileId = Current.Id;
        model.Status = "이미지를 읽는 중…";
        try
        {
            imageLoadTask = ImageFileLoader.LoadAsync(picker.FileName);
            var frame = await imageLoadTask;
            if (closing || request != generation || profileId != Current.Id) return;
            lastFrame = frame; model.Preview = PreviewFrame(frame);
            model.Status = "이미지를 불러왔습니다.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or UnauthorizedAccessException or FileFormatException)
        { if (!closing && request == generation) model.Status = "이미지 읽기 실패: " + ex.Message; }
        finally { imageLoadTask = null; }
    }
    private async void RunOcr(object sender, RoutedEventArgs e)
    {
        StopTranslation();
        if (lastFrame is null) { model.Status = "먼저 캡처하거나 이미지를 열어 주세요."; return; }
        if (ocrTask is not null) { model.Status = "이전 OCR 작업이 끝나기를 기다리고 있습니다."; return; }
        var request = ++generation; var profileId = Current.Id;
        var options = Current.OcrOptions();
        var frame = lastFrame;
        var qaId = gameQa.Begin(frame, options, GameQaProfile.From(Current));
        using var cancellation = new CancellationTokenSource(); ocrCancellation = cancellation;
        model.Status = "OCR 준비·인식 중…";
        OcrText.Text = "";
        try
        {
            ocr = CurrentOcrService();
            ocrTask = debugTelemetry is null ? ocr.RecognizeAsync(frame, options, cancellation.Token) : RecognizeDebugManualAsync(ocr, frame, options, cancellation.Token);
            UpdateOcrExecution();
            var result = await ocrTask; cancellation.Token.ThrowIfCancellationRequested();
            if (request != generation || profileId != Current.Id || closing) return;
            gameQa.Complete(qaId, result, result.Text, false);
            OcrText.Text = result.Text; manualRecognizedFrame = frame; manualRecognizedText = result.Text;
            var repeated = duplicate.IsDuplicate(result.Text);
            model.Status = $"OCR 완료 · {result.Lines.Count}개 줄 인식";
            if (result.Lines.Count == 0) model.Status += " · 문자를 찾지 못했습니다. 영역·언어를 확인하세요.";
        }
        catch (OperationCanceledException) { gameQa.Finish(qaId, "canceled"); if (request == generation && !closing) model.Status = "OCR 취소됨"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { gameQa.Finish(qaId, "failed"); if (request == generation && !closing) { model.Status = "OCR 실패: " + ex.Message; if (ex is OcrInitializationException) ocrPresentationError = true; } }
        finally { ocrTask = null; ocrCancellation = null; UpdateOcrExecution(); }
    }
    private void SaveCapture(object sender, RoutedEventArgs e)
    {
        if (model.Preview is null) { model.Status = "먼저 화면을 캡처하세요."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 이미지|*.png", FileName = "ko-trans2-capture.png" };
        if (dialog.ShowDialog(this) != true) return;
        try { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(model.Preview)); using var stream = File.Create(dialog.FileName); encoder.Save(stream); model.Status = "PNG 이미지를 저장했습니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { model.Status = "이미지 저장 실패: " + ex.Message; }
    }
    private void ShowTranslation(object sender, RoutedEventArgs e)
    {
        if (overlay is not null) { overlay.Show(); return; }
        CreateOverlayWindow().Show();
    }
    internal TranslationWindow CreateOverlayWindow()
    {
        if (overlay is not null) return overlay;
        if (overlayClosedByUser) {
            model.Status = startingTranslation ? (Current.ShowTranslation ? "번역 준비 중…" : "원문 인식 준비 중…") : "자막창을 열었습니다. 시작을 누르세요.";
            overlayClosedByUser = false;
        }
        var profile = Current; var window = new TranslationWindow(profile,
            openDictionary: (uri, owner, token) => OpenDictionaryForProfileAsync(profile, uri, owner, token),
            getGameWindow: () => !closing && Current.Id == profile.Id ? target : null); overlay = window;
        if (pipeline?.IsRunning == true) window.BeginSession(RecognitionInputPlan.For(Current, automatic).UseTriggers);
        window.SetResult(OcrText.Text, TranslatedText.Text, "");
        if (lastOverlayUpdate is { } update) window.SetPipelineResult(update);
        if (pipeline?.IsRunning != true) window.ShowStoppedPrompt();
        window.SetCaptureAreaAvailable(profile.CaptureMode != CaptureMode.Clipboard);
        RefreshOverlayControls();
        window.ShowMainRequested += () => {
            if (overlay != window || Current.Id != profile.Id || closing) return;
            RestoreMainWindow();
            ShowProfileSettingsPage(2);
        };
        window.ToggleTranslationRequested += async () => {
            if (overlay != window || Current.Id != profile.Id || closing || startingTranslation) return;
            await ToggleTranslationAsync();
        };
        window.CaptureAreaRequested += async () => {
            if (overlay != window || Current.Id != profile.Id || closing) return;
            await SelectCaptureAreaAsync();
        };
        window.ExplanationRequested += () => {
            if (overlay != window || Current.Id != profile.Id || closing) return;
            ExplainOverlaySubtitle(window);
        };
        window.DisplayModeChangeRequested += async () => {
            if (overlay != window || Current.Id != profile.Id || closing) return;
            await CycleSubtitleDisplayAsync();
        };
        window.PreferencesChanged += async () => {
            if (Current.Id != profile.Id || closing) return;
            bool wasChanging = changing; changing = true;
            OverlayPassThrough.IsChecked = profile.OverlayClickThrough;
            WordDictionaryOption.IsChecked = profile.WordDictionaryEnabled;
            WordDictionaryOption.IsEnabled = !profile.OverlayClickThrough;
            changing = wasChanging;
            await SaveOverlayPreferencesAsync();
        };
        window.InteractionFailed += message => { if (Current.Id == profile.Id && !closing) model.Status = message; };
        window.IsVisibleChanged += (_, _) => RefreshOverlayVisibilityButton();
        window.Closed += (_, _) => {
            retiring.Add(window.DictionaryCleanup);
            if (overlay != window) return;
            overlay = null;
            RefreshOverlayVisibilityButton();
            if (closing || window.ClosedForReplacement) return;
            StopTranslation(); generation++; ocrCancellation?.Cancel(); captureCancellation?.Cancel(); overlayClosedByUser = true;
            model.Status = Current.ShowTranslation ? "자막창을 닫아 번역을 중지했습니다." : "자막창을 닫아 원문 인식을 중지했습니다.";
        };
        return window;
    }
    private async void OverlayPassThroughChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || changing) return;
        Current.OverlayClickThrough = OverlayPassThrough.IsChecked == true;
        WordDictionaryOption.IsEnabled = !Current.OverlayClickThrough;
        overlay?.SetClickThrough(Current.OverlayClickThrough);
        await SaveOverlayPreferencesAsync();
    }
    private async Task SaveOverlayPreferencesAsync()
    {
        await SaveSettings(errorPrefix: "자막 설정 저장 실패: ");
    }
    private void ShowStudy(object sender, RoutedEventArgs e)
    {
        var window = EnsureStudy(); window.ShowReader(); window.Activate();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        if (!exitRequested && !closing && tray?.IsAvailable == true) { e.Cancel = true; HideToTray(); return; }
        e.Cancel = true; if (closing) return; closing = true;
        if (profileChangePending is { } pending) await pending.Task;
        await WizardPlacementSave;
        captureCancellation?.Cancel(); generation++; ocrCancellation?.Cancel();
        StopTranslation(); CancelStudy("종료 준비로 학습을 중지했습니다.");
        if (ready && (!ApplyProfileName() || !ApplyEmbeddedProfileEditor())) { CancelApplicationExit(); model.Status += " 설정을 확인한 뒤 다시 종료하세요."; return; }
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero) RememberWindows();
        if (!await SaveForExitAsync(dictionaryPopup, AskExitSaveChoice))
        {
            CancelApplicationExit();
            model.Status = "종료를 취소했습니다. 번역·학습은 중지된 상태입니다. 시작 또는 분석 버튼으로 다시 실행할 수 있습니다.";
            return;
        }
        // The exit decision is final: a browser close during a long drain must not start another save.
        dictionaryPopup?.Dispose();
        dictionaryPopup = null;
        Workspace.IsEnabled = false;
        var failures = await ShutdownCleanup.RunAsync(new (string, Func<Task>)[] {
            ("로컬 번역", () => LocalTranslationEnvironment.Runtime.DisposeAsync().AsTask()),
            ("번역 작업", () => Task.WhenAll(retiring)), ("학습 작업", () => Task.WhenAll(studyTasks)),
            ("이미지 읽기", () => imageLoadTask ?? Task.CompletedTask),
            ("학습 서비스", () => explanations.DisposeAsync().AsTask()), ("QA 저장", () => qaSaveTask),
            ("QA 메모리", () => { gameQa.Clear(); return Task.CompletedTask; }),
            ("일본어 사전", () => { japanese?.Dispose(); return Task.CompletedTask; }),
            ("입력 감시", () => { input?.Dispose(); return Task.CompletedTask; }),
            ("OCR 세션", () => ocrManager.DisposeAsync().AsTask()),
            ("번역창", () => overlay?.CloseForExitAsync() ?? Task.CompletedTask),
            ("학습창/음성", () => study?.CloseForExitAsync() ?? Task.CompletedTask),
            ("디버그 창", () => DebugWindow?.CloseForExitAsync() ?? Task.CompletedTask)
        }, step => {
            ShowForExitOrRestore();
            model.Status = $"{step} 종료를 기다리고 있습니다. 작업이 끝나면 창이 닫힙니다.";
        });
        if (failures.Count > 0)
        {
            var message = "종료 정리 실패: " + string.Join(", ", failures) + "\n다른 자원의 정리는 진행했습니다. 종료할까요?";
            ShowForExitOrRestore();
            bool exit = ConfirmShutdownFailureOverride?.Invoke(message)
                ?? MessageBox.Show(this, message, "종료 정리 확인", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
            if (!exit)
            { CancelApplicationExit(); ready = false; Workspace.IsEnabled = false; model.Status = "일부 자원이 정리되어 번역/학습을 중지했습니다. 알림 아이콘의 종료 메뉴 또는 창 닫기로 종료를 다시 시도하세요."; return; }
        }
        closeApproved = true;
        // Cleanup can complete synchronously (including a never-shown Window). Do not reenter Closing.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    internal async Task<bool> SaveForExitAsync(DictionaryPopupTracker? tracker, Func<string, ExitSaveChoice> decide)
    {
        while (true) {
            bool placementSaved = await FlushDictionaryPopupAsync(tracker);
            if (placementSaved && (!ready || await SaveSettings())) return true;
            string failed = placementSaved ? "프로그램 설정" : "단어 사전창 위치";
            switch (decide(failed)) {
                case ExitSaveChoice.Retry: continue;
                case ExitSaveChoice.Discard: return true;
                default: return false;
            }
        }
    }
    private ExitSaveChoice AskExitSaveChoice(string failed)
    {
        ShowForExitOrRestore();
        var result = MessageBox.Show(this, $"{failed} 저장에 실패했습니다. 저장을 다시 시도할까요?\n\n예 — 다시 시도\n아니요 — 저장하지 않고 종료\n취소 — 앱으로 돌아가기",
            "종료 전 저장 실패", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return result switch { MessageBoxResult.Yes => ExitSaveChoice.Retry, MessageBoxResult.No => ExitSaveChoice.Discard, _ => ExitSaveChoice.Cancel };
    }
}
internal enum ExitSaveChoice { Cancel, Retry, Discard }
