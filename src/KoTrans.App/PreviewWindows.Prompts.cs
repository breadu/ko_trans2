namespace KoTrans.App;

internal sealed partial class TranslationWindow
{
    private enum IdlePrompt { None, Trigger, Stopped, Preparing }
    private IdlePrompt prompt;
    private readonly TextBlock instruction = new() { Name = "OverlayInstruction", TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    internal void BeginSession(bool waitForTrigger)
    {
        ClearPipelineNotices(); pipelineVersion = -1;
        reusableTranslation = "";
        reusableSource = "";
        original.SetSource("", profile.Language == "일본어" ? "ja" : "en"); translation.Text = "";
        ShowNotice("");
        prompt = waitForTrigger ? IdlePrompt.Trigger : IdlePrompt.None;
        RefreshInstructionStyle(); ApplyContentVisibility();
    }
    internal void ShowStoppedPrompt()
    {
        bool hadPipelineNotice = pipelineErrors.Count > 0 || pipelineWarning is not null;
        ClearPipelineNotices(); pipelineVersion = -1;
        prompt = IdlePrompt.Stopped; SetBusy(false);
        // A startup failure set after StopTranslation remains useful; old pipeline errors do not.
        if (hadPipelineNotice || chromeFailure is not null) ShowNotice("");
        RefreshInstructionStyle(); ApplyContentVisibility();
    }
    internal void ShowPreparingPrompt()
    {
        ClearPipelineNotices(); pipelineVersion = -1;
        reusableTranslation = "";
        reusableSource = "";
        original.SetSource("", profile.Language == "일본어" ? "ja" : "en"); translation.Text = "";
        ShowNotice("");
        prompt = IdlePrompt.Preparing; SetBusy(false);
        RefreshInstructionStyle(); ApplyContentVisibility();
    }
    internal void FinishPreparingPrompt() { if (prompt == IdlePrompt.Preparing) ShowStoppedPrompt(); }
    internal void DismissTriggerPrompt() { if (prompt == IdlePrompt.Trigger) ClearIdlePrompt(); }
    private void ClearIdlePrompt()
    {
        if (prompt == IdlePrompt.None) return;
        prompt = IdlePrompt.None; ApplyContentVisibility();
    }
    private void RefreshInstructionStyle()
    {
        instruction.FontSize = profile.OverlayFontSize; instruction.Foreground = translation.Foreground;
        string action = profile.ShowTranslation ? "번역" : "원문 인식";
        instruction.Text = prompt == IdlePrompt.Preparing ? $"{action} 준비 중…"
            : prompt == IdlePrompt.Trigger ? "게임에서 지정한 키나 버튼을 누르면 대사를 인식합니다."
            : $"자막창의 ‘시작’ 버튼을 누르면 {action}을 시작합니다.";
    }
    private void ApplyContentVisibility()
    {
        bool content = prompt == IdlePrompt.None;
        original.Visibility = content && profile.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        translation.Visibility = content && profile.ShowTranslation ? Visibility.Visible : Visibility.Collapsed;
        instruction.Visibility = content ? Visibility.Collapsed : Visibility.Visible;
        original.SetFuriganaEnabled(content && profile.ShowJapaneseFurigana && profile.Language == "일본어" && profile.ShowOriginal);
        RefreshDictionary();
    }
}
