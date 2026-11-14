namespace KoTrans.App;

// The selected input source has exactly one automatic activation path.
internal readonly record struct RecognitionInputPlan(bool WatchScreen, bool UseTriggers, bool WatchClipboard)
{
    internal static RecognitionInputPlan For(Profile profile, bool continuous)
    {
        if (!continuous) return default;
        if (profile.CaptureMode == CaptureMode.Clipboard) return new(false, false, true);
        if (profile.AutomaticWatch) return new(true, false, false);
        return new(false, profile.Triggers.Enabled && HasTriggers(profile.Triggers), false);
    }
    internal static bool HasTriggers(TriggerOptions triggers) => triggers.Keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length > 0
        || triggers.MouseButton != "None" || triggers.GamepadButtons.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length > 0;
    internal string Label => WatchClipboard ? "클립보드 감시" : WatchScreen ? "화면 자동 인식" : UseTriggers ? "입력 트리거" : "인식 방식 미설정";
}
