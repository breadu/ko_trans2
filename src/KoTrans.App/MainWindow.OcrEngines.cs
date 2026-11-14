using System.Collections.Generic;
using KoTrans.Core;
using KoTrans.Ocr;

namespace KoTrans.App;

public partial class MainWindow
{
    // Only the user-facing name and guidance live here. The catalog descriptor,
    // saved engine/preset IDs, model paths and loading policy remain unchanged.
    internal static (string Label, string Guidance) OcrModelPresentation(OcrModelPresetDescriptor descriptor) =>
        (descriptor.EngineId, descriptor.Id) switch
        {
            ("paddle", "v6-medium-full") =>
                ("PaddleOCR v6 Medium (기본 · 정확도 우선)",
                 "정확도를 우선하는 모델입니다. 작은 글자나 복잡한 글꼴에 유리하지만 인식 시간이 길 수 있습니다."),
            ("paddle", "default") =>
                ("PaddleOCR v5 (속도 우선)",
                 "v5 고속 모델이 있으면 우선 사용합니다. 처리 속도를 중시할 때 좋지만, 복잡한 글꼴은 오인식할 수 있습니다."),
            ("paddle", "v5-det-v6-medium-rec") =>
                ("PaddleOCR v5 검출 + v6 인식 (균형형)",
                 "v5로 글자 위치를 찾고 v6로 글자를 읽습니다. 속도와 정확도의 균형을 노리는 조합입니다."),
            _ => (descriptor.DisplayName, descriptor.Description)
        };

    private void FillOcrEngines()
    {
        // The model is persisted as EngineId/PresetId; the compact ComboBox
        // only changes presentation. Never auto-select a replacement for an
        // unknown saved model, because that would silently alter the profile.
        bool previous = changing;
        changing = true;
        try
        {
            OcrEngineChoices.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var descriptor in ocrFactory.Presets.Presets)
            {
                var display = OcrModelPresentation(descriptor);
                var choice = new ComboBoxItem
                {
                    Content = display.Label,
                    Tag = descriptor,
                    ToolTip = display.Guidance + "\n" + descriptor.Description
                };
                System.Windows.Automation.AutomationProperties.SetName(choice, display.Label);
                OcrEngineChoices.Items.Add(choice);
                if (Current.OcrEngineId == descriptor.EngineId && Current.OcrModelPresetId == descriptor.Id)
                    selected = choice;
            }
            OcrEngineChoices.SelectedItem = selected;
        }
        finally { changing = previous; }
        RefreshOcrAvailability();
    }

    private void OcrModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changing || OcrEngineChoices.SelectedItem is not ComboBoxItem { Tag: OcrModelPresetDescriptor descriptor })
            return;
        if (Current.OcrEngineId == descriptor.EngineId && Current.OcrModelPresetId == descriptor.Id)
            return;

        // Retain the existing OCR invalidation, background work cancellation,
        // saved selection and "save needed" notice from the radio-button UI.
        InvalidateOcr(); RetireOcrSession(); captureCancellation?.Cancel();
        lastFrame = null; model.Preview = null; contexts.Reset(Current.Id);
        Current.OcrEngineId = descriptor.EngineId; Current.OcrModelPresetId = descriptor.Id;
        RefreshOcrAvailability();
        model.Status = Current.ShowTranslation ? "OCR 모델 변경 · 설정 저장 후 번역을 시작하세요." : "OCR 모델 변경 · 설정 저장 후 원문 인식을 시작하세요.";
        ShowFeedback(FeedbackKind.Information, "OCR 모델이 변경됐어요", "저장 필요 · " + OcrModelPresentation(descriptor).Label);
        if (operationToast.Kind == FeedbackKind.Information)
            unsavedOcrNotice = (Current.Id, Current.OcrEngineId, Current.OcrModelPresetId);
    }

    private void RefreshOcrAvailability()
    {
        RefreshMarkerLearningControls();
        if (OcrEngineStatus is null) return;
        var status = ocrFactory.CheckModels(Current.OcrOptions());
        // Explain what the selected model is good for, not its routine file presence.
        // Missing model files or unsupported saved identities remain explicit warnings.
        var descriptor = (OcrEngineChoices.SelectedItem as ComboBoxItem)?.Tag as OcrModelPresetDescriptor;
        OcrModelDescription.Text = descriptor is null
            ? "저장된 OCR 모델을 찾을 수 없습니다. 아래에서 사용할 모델을 다시 선택하세요."
            : OcrModelPresentation(descriptor).Guidance;
        OcrModelDescription.ToolTip = null; // Explanatory text is already visible directly below the model selector.
        // Model presence is not the same as SHA-256/ONNX validation at first use.
        OcrEngineStatus.Text = status.Ready ? "" : status.Message;
        OcrEngineStatus.Visibility = status.Ready ? Visibility.Collapsed : Visibility.Visible;
        OcrEngineStatus.ToolTip = status.Message;
        bool previous = changing; changing = true;
        try
        {
            OcrGpuAcceleration.IsChecked = Current.OcrGpuAcceleration;
            OcrGpuAcceleration.IsEnabled = DirectMlProtocol.Supports(Current.OcrOptions());
            OcrGpuDescription.Text = OcrGpuAcceleration.IsEnabled
                ? "GPU를 사용할 수 없으면 CPU로 자동 전환합니다."
                : "이 OCR 모델은 GPU 가속을 지원하지 않아 CPU로 실행합니다.";
            OcrGpuAcceleration.ToolTip = "설정 변경 시 번역이 중지됩니다. 다시 시작하면 적용됩니다. 지원하지 않는 모델에서는 선택 값이 보존됩니다.";
        }
        finally { changing = previous; }
    }

    private void OcrGpuAccelerationChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || changing || !OcrGpuAcceleration.IsEnabled) return;
        bool enabled = OcrGpuAcceleration.IsChecked == true;
        if (Current.OcrGpuAcceleration == enabled) return;
        bool wasRunning = pipeline?.IsRunning == true || startingTranslation;
        // Pipelines retain immutable options. Stop/version-guard old work before
        // retiring its session; the manager drains native inference before disposal.
        StopTranslation(); RetireOcrSession();
        Current.OcrGpuAcceleration = enabled;
        // A study OCR call can still be draining after the translation session
        // is retired. Keep its actual backend visible until the call returns.
        UpdateOcrExecution();
        if (model.OcrExecution == "OCR: 대기")
            model.OcrExecutionDetail = enabled ? "다음 시작 시 GPU 가속을 시도합니다." : "다음 시작 시 CPU로 인식합니다.";
        model.Status = wasRunning
            ? $"GPU 가속 설정을 변경하여 {(Current.ShowTranslation ? "번역" : "원문 인식")}을 중지했습니다. 다시 시작하면 적용됩니다."
            : "GPU 가속 설정을 변경했습니다. 다음 시작부터 적용합니다.";
        ShowFeedback(FeedbackKind.Information, "OCR GPU 가속 설정을 변경했어요",
            wasRunning ? $"{(Current.ShowTranslation ? "번역" : "원문 인식")}을 중지했습니다. 다시 시작하면 적용됩니다." : "다음 시작부터 적용됩니다.");
    }

    private IOcrService? observedOcr;
    private Action<OcrExecutionStatus>? ocrExecutionHandler;
    private string lastOcrGpuDevice = "";
    private long ocrPresentationGeneration;
    private bool ocrPresentationError;
    // Canceled native calls can overlap a newer study request until they actually return.
    private sealed class StudyOcrActivity(IOcrService service, CancellationToken token, Guid profile, long version)
    {
        internal IOcrService Service { get; } = service;
        internal CancellationToken Token { get; } = token;
        internal Guid Profile { get; } = profile;
        internal long Version { get; } = version;
        internal OcrExecutionStatus Status = service.ExecutionStatus;
        internal bool Retired;
    }
    private readonly List<StudyOcrActivity> studyOcrActivities = [];
    private bool CanObserveStudyOcr(StudyOcrActivity activity) => !closing && !activity.Retired
        && !activity.Token.IsCancellationRequested && activity.Profile == Current.Id && activity.Version == studyVersion;
    // Manual calls can publish execution events synchronously, before RecognizeAsync returns its task.
    // The cancellation scope is installed first and lasts until the call's finally block.
    private IOcrService? PrimaryOcrPresentation => ocr is not null && (pipeline?.IsRunning == true && Current.CaptureMode != CaptureMode.Clipboard
        || ocrCancellation is not null || startingTranslation && ocr.ExecutionStatus.Backend == "Preparing") ? ocr : null;
    private void StopObservingOcrExecution()
    {
        ocrPresentationGeneration++;
        if (observedOcr is not null && ocrExecutionHandler is not null) observedOcr.ExecutionChanged -= ocrExecutionHandler;
        observedOcr = null; ocrExecutionHandler = null;
    }
    private void ObserveOcrExecution(IOcrService service)
    {
        if (ReferenceEquals(observedOcr, service)) return;
        StopObservingOcrExecution();
        observedOcr = service;
        long observation = ocrPresentationGeneration;
        ocrExecutionHandler = status => Dispatcher.BeginInvoke(() => {
            if (closing || observation != ocrPresentationGeneration || !ReferenceEquals(observedOcr, service)) return;
            UpdateOcrExecution();
            if (observation != ocrPresentationGeneration || !ReferenceEquals(observedOcr, service)) return;
            debugTelemetry?.SetContext(v => v with { ActualBackend = status.Backend is "CPU" or "DirectML" or "Preparing" or "Error" ? status.Backend : "N/A", Fallback = status.IsFallback });
            if (status.IsFallback) ShowFeedback(FeedbackKind.Information, "GPU 가속을 사용할 수 없어 CPU OCR로 전환했습니다.",
                Current.ShowTranslation ? "번역은 계속 진행됩니다." : "원문 인식은 계속 진행됩니다.");
            if (useLocalModelData) WriteOcrExecutionDiagnostic(status);
        });
        service.ExecutionChanged += ocrExecutionHandler;
    }
    private void UpdateOcrExecution()
    {
        foreach (var activity in studyOcrActivities)
            if (CanObserveStudyOcr(activity)) activity.Status = activity.Service.ExecutionStatus;
        var primary = PrimaryOcrPresentation;
        var studyActivity = primary is null ? studyOcrActivities.LastOrDefault() : null;
        var service = primary ?? studyActivity?.Service;
        bool observe = !closing && service is not null && (primary is not null || CanObserveStudyOcr(studyActivity!));
        if (observe) ObserveOcrExecution(service!);
        else if (observedOcr is not null) StopObservingOcrExecution();
        // Retired/canceled calls retain their last known label until drain, but cannot publish new events.
        var status = studyActivity is not null && !observe ? studyActivity.Status : service?.ExecutionStatus ?? OcrExecutionStatus.Cpu;
        if (ocrPresentationError || observe && service!.State == OcrState.Failed) status = new("Error");
        if (status.Backend == "DirectML") lastOcrGpuDevice = status.Device;
        bool running = service is not null;
        model.OcrExecution = OcrExecutionLabel(status, running);
        var reasonCode = status.Reason.Split(':', 2)[0];
        var safeReason = reasonCode.Length > 0 && reasonCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? reasonCode : "gpu-runtime";
        model.OcrExecutionDetail = status.Backend == "Error" ? "OCR 모델 오류 · 모델 파일과 설정을 확인하세요."
            : !running ? "OCR이 시작되지 않았습니다."
            : status.IsFallback
            ? $"GPU 전환 실패 ({safeReason}) · CPU로 인식을 계속합니다."
                + (lastOcrGpuDevice.Length > 0 ? $"\n장치: {lastOcrGpuDevice}" : "")
            : status.DisplayText;
    }
    internal static string OcrExecutionLabel(OcrExecutionStatus status, bool running) => status.Backend == "Error" ? "OCR: 오류"
            : !running ? "OCR: 대기"
            : status.Backend == "Preparing" ? "OCR: GPU 준비 중"
            : status.Backend == "DirectML" ? "OCR: GPU"
            : status.IsFallback ? "OCR: CPU (GPU 전환 실패)" : "OCR: CPU";
    private static void WriteOcrExecutionDiagnostic(OcrExecutionStatus status)
    {
        // No OCR text, images, profile names, credentials or exception messages.
        try {
            string dir = Path.Combine(App.DataDirectory, "Diagnostics"); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "ocr-gpu.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) return;
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} backend={status.Backend}; device={status.Device}; id={status.DeviceId}; reason={status.Reason}\n");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
