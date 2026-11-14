namespace KoTrans.App;

public partial class MainWindow
{
    private int subtitleVisibilityFeedback = -1;
    internal Func<Task<bool>>? RestartSubtitleRecognitionOverride { private get; set; }
    private bool changingSubtitleDisplay;
    internal async Task CycleSubtitleDisplayAsync()
    {
        if (!ready || closing || startingTranslation || changingSubtitleDisplay) return;
        bool original = Current.ShowOriginal, translated = Current.ShowTranslation;
        changing = true;
        try {
            // Both -> original -> translation -> both. Set both atomically so the last
            // visible field is never briefly unchecked by the profile controls.
            ShowOriginal.IsChecked = translated;
            ShowKorean.IsChecked = !(original && translated);
        } finally { changing = false; }
        await ApplySubtitleDisplayAsync(fillMissing: true);
    }
    private async void SubtitleVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || changing) return;
        if (startingTranslation || changingSubtitleDisplay) {
            changing = true;
            try { ShowOriginal.IsChecked = Current.ShowOriginal; ShowKorean.IsChecked = Current.ShowTranslation; }
            finally { changing = false; }
            return;
        }
        if (ShowOriginal.IsChecked != true && ShowKorean.IsChecked != true) {
            // Reject the last uncheck immediately, keeping the persisted profile usable.
            changing = true;
            try { ((CheckBox)sender).IsChecked = true; }
            finally { changing = false; }
            if (operationToast.Visibility == Visibility.Visible && operationToast.Kind == FeedbackKind.Error
                && subtitleVisibilityFeedback != FeedbackCount) {
                model.Status = "원문 또는 한국어 번역 중 하나 이상을 켜세요.";
                return;
            }
            ShowFeedback(FeedbackKind.Error, "자막 표시 설정을 확인해 주세요", "원문 또는 한국어 번역 중 하나 이상을 켜세요.", replaceError: true, duration: TimeSpan.FromMilliseconds(1500));
            subtitleVisibilityFeedback = FeedbackCount;
            return;
        }
        if (subtitleVisibilityFeedback == FeedbackCount && operationToast.Kind == FeedbackKind.Error) {
            operationToast.Dismiss(); toastExpires = null; toastFading = false; PlaceFeedback();
            if (saveButtonStarted is null && saveButtonUntil is null) feedbackTimer.Stop();
        }
        subtitleVisibilityFeedback = -1;
        await ApplySubtitleDisplayAsync(fillMissing: false);
    }
    private async Task ApplySubtitleDisplayAsync(bool fillMissing)
    {
        if (changingSubtitleDisplay || startingTranslation || closing) return;
        changingSubtitleDisplay = true;
        var window = overlay; var profile = Current;
        var content = window?.SubtitleContent ?? (Source: "", Translation: "");
        bool wasRunning = pipeline?.IsRunning == true, watch = automatic;
        bool rebuild = wasRunning && profile.ShowTranslation != (ShowKorean.IsChecked == true);
        bool awaitingTranslation = !rebuild && pipeline?.IsTranslating == true;
        try {
            if (rebuild) StopTranslation();
            ApplyOptions(); FillTranslation(); window?.SetDisplayModeChanging(true);
            bool needSource = string.IsNullOrWhiteSpace(content.Source);
            bool needTranslation = profile.ShowTranslation && string.IsNullOrWhiteSpace(content.Translation);
            bool request = fillMissing || wasRunning;
            bool started = false;
            if (rebuild || request && pipeline?.IsRunning != true && (needSource || needTranslation)) {
                started = await (RestartSubtitleRecognitionOverride?.Invoke()
                    ?? StartTranslationAsync(watch, content.Source, content.Translation));
                if (!started) return;
            }
            if (closing || Current.Id != profile.Id || !ReferenceEquals(overlay, window)) return;
            if (!needSource && !started && !awaitingTranslation) window?.SetResult(content.Source, content.Translation,
                needTranslation && request ? "번역 준비 중…" : "");
            if (request && pipeline is { IsRunning: true } active) {
                if (needSource) {
                    window?.SetResult(null, null, "원문 인식 중…");
                    if (profile.CaptureMode == CaptureMode.Clipboard) ReadClipboard(true, false);
                    else await active.RequestManualAsync();
                }
                // A recreated pipeline receives the source before its monitor starts.
                // A display-only change can reuse the accepted source without OCR.
                else if (needTranslation && !started && !awaitingTranslation) active.SubmitClipboard(content.Source, manual: true);
            }
        } finally {
            changingSubtitleDisplay = false;
            if (!closing && Current.Id == profile.Id) {
                overlay?.SetDisplayModeChanging(false);
                await SaveOverlayPreferencesAsync();
            }
        }
    }
    private void RefreshOverlayVisibilityButton()
    {
        RefreshDebugContext();
        if (OverlayVisibilityButton is not null)
            OverlayVisibilityButton.Content = overlay?.IsVisible == true ? "자막창 감추기" : "자막창 열기";
    }
    private void ToggleOverlayVisibility(object sender, RoutedEventArgs e) => ToggleOverlayDisplay();
    // The display delegate lets background tests verify the command without creating an HWND.
    internal void ToggleOverlayDisplay(Action<TranslationWindow, bool>? display = null)
    {
        var window = CreateOverlayWindow();
        bool show = !window.IsVisible;
        if (display is not null) display(window, show);
        else if (show) window.Show(); else window.Hide();
        RefreshOverlayVisibilityButton();
    }
}
