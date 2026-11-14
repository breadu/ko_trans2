using KoTrans.Infrastructure;

namespace KoTrans.App;

// Pure preflight checks shared by automatic startup and every screen-capture path.
// No model, HWND, desktop, credentials or external service is touched here.
internal static class RecognitionPreflight
{
    internal static void ValidateCapture(Profile profile, CaptureMode mode)
    {
        if (mode == CaptureMode.Region && !profile.CaptureAreaConfigured)
            throw new CaptureUnavailableException("인식 영역이 지정되지 않았습니다. 프로필 설정 → 캡처 · OCR에서 영역을 지정하세요.");
    }

    internal static void ValidateStart(Profile profile, bool continuous)
    {
        // Manual image diagnostics can operate without any screen area or trigger.
        // Actual manual/study screen capture still passes through ValidateCapture.
        if (!continuous) return;
        ValidateCapture(profile, profile.CaptureMode);
        var plan = RecognitionInputPlan.For(profile, true);
        if (!plan.WatchScreen && !plan.WatchClipboard && !plan.UseTriggers)
            throw new TranslationException("인식 방식이 설정되지 않았습니다. 프로필 설정 → 인식 방식 · 트리거에서 화면 자동 인식을 선택하거나 입력 트리거를 하나 이상 지정하세요.");
        if (plan.UseTriggers) DesktopInput.ParseTriggers(profile.Triggers);
    }
}
