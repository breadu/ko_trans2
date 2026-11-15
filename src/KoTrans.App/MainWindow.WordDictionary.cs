namespace KoTrans.App;

public partial class MainWindow
{
    private DictionaryPopupTracker? dictionaryPopup;
    private Task OpenDictionaryForProfileAsync(Profile profile, Uri uri, IntPtr owner, CancellationToken token)
    {
        if (dictionaryPopup is null) {
            dictionaryPopup = new DictionaryPopupTracker(SaveDictionaryPlacementAsync);
            dictionaryPopup.Failed += message => { if (!closing) model.Status = message; };
        }
        return DictionaryBrowser.OpenAsync(uri, owner, token, tracker: dictionaryPopup, gameProfile: profile);
    }
    internal async Task SaveDictionaryPlacementAsync(Profile owner, WindowPlacement placement)
    {
        // The popup can close after another profile was selected or its overlay was hidden.
        if (!settings.Profiles.Contains(owner)) return;
        owner.DictionaryPopupWindow = placement;
        if (!await SaveSettings(errorPrefix: "단어 사전 위치 저장 실패: "))
            throw new DictionaryBrowserException("사전 위치를 저장하지 못했습니다. 설정의 저장 상태를 확인하세요.");
    }
    private async Task<bool> FlushDictionaryPopupAsync(DictionaryPopupTracker? tracker)
    {
        if (tracker is null) return true;
        try { await tracker.FlushAsync(); return true; }
        catch (Exception ex) {
            model.Status = "단어 사전 위치 저장 실패: " + ex.Message;
            ShowFeedback(FeedbackKind.Error, "사전 위치를 저장하지 못했어요", model.Status, replaceError: true);
            return false;
        }
    }
    private void FillWordDictionary()
    {
        WordDictionaryOption.IsChecked = Current.WordDictionaryEnabled;
        WordDictionaryOption.IsEnabled = !Current.OverlayClickThrough;
        WordDictionaryOption.ToolTip = "원문 단어를 클릭해 사전을 엽니다. 비활성 상태라면 '게임에 클릭 전달'을 먼저 끄세요.";
        ToolTipService.SetShowOnDisabled(WordDictionaryOption, true);
        EnglishDictionaryUrl.Text = Current.WordDictionary.EnglishUrl;
        JapaneseDictionaryUrl.Text = Current.WordDictionary.JapaneseUrl;
    }
    private async void WordDictionaryChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || changing || Current.OverlayClickThrough) return;
        Current.WordDictionaryEnabled = WordDictionaryOption.IsChecked == true;
        overlay?.Apply(Current);
        await SaveOverlayPreferencesAsync();
    }
    private bool ApplyWordDictionaryUrls()
    {
        var options = new WordDictionaryOptions { EnglishUrl = EnglishDictionaryUrl.Text.Trim(), JapaneseUrl = JapaneseDictionaryUrl.Text.Trim() };
        try { options.Validate(); }
        catch (InvalidDataException ex) {
            model.Status = ex.Message;
            ShowFeedback(FeedbackKind.Error, "단어 사전 주소를 확인해 주세요", ex.Message, replaceError: true);
            return false;
        }
        Current.WordDictionary = options;
        return true;
    }
}
