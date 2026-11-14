using KoTrans.Core;

namespace KoTrans.App;

// View-only navigation: all actual game settings still use the existing Profile/SettingsStore.
// Reusing one MainWindow avoids duplicating controls, native window ownership and event flows.
public partial class MainWindow
{
    private bool profileSettingsOpen;
    private long profileNavigationVersion;
    private Guid? credentialReturnProfileId;
    private TranslationSettingsEditor? embeddedProfileEditor;
    private static readonly string[] PageTitles =
    [
        "기본 정보", "캡처 · OCR", "번역 서비스", "자막창", "인식 방식 · 트리거", "캐릭터 사전", "고급 설정", "공통 설정"
    ];
    private static readonly string[] PageDescriptions =
    [
        "프로필 이름과 영어·일본어 원문 읽기 방식을 설정합니다.",
        "게임 창, 캡처 영역과 OCR 모델을 설정합니다.",
        "현재 게임의 번역 제공자, 모델과 요청 옵션을 설정합니다.",
        "원문과 한국어 번역 자막의 크기·색상·배치를 설정합니다.",
        "번역 시작 후 사용할 화면 자동 인식 또는 게임 입력 트리거를 선택합니다.",
        "게임별 캐릭터 이름 사전을 연결합니다.",
        "클립보드, 확대 화면 캡처와 OCR 문제 해결 도구를 관리합니다.",
        "API 인증 정보와 전역 단축키를 관리합니다. 기본 프로필 전용입니다."
    ];

    private void OpenProfileSettings(object sender, RoutedEventArgs e)
    {
        if (profileSettingsOpen) _ = ReturnToLauncherAsync();
        else ShowProfileSettingsPage(0);
    }

    private async void ReturnToLauncher(object sender, RoutedEventArgs e) => await ReturnToLauncherAsync();

    private async Task<bool> ReturnToLauncherAsync()
    {
        long navigation = profileNavigationVersion;
        var profile = Current.Id;
        if (profileSettingsOpen && !await SaveCurrentProfileAsync()) return false;
        // Saving can yield while a sidebar, wizard or overlay opens a different page.
        // A completed write must not undo that newer navigation.
        if (closing || navigation != profileNavigationVersion || Current.Id != profile) return false;
        ShowLauncher();
        return true;
    }

    internal void ShowLauncher()
    {
        profileNavigationVersion++;
        profileSettingsOpen = false;
        if (LauncherView is null || ProfileSettingsView is null) return;
        LauncherView.Visibility = Visibility.Visible;
        ProfileSettingsView.Visibility = Visibility.Collapsed;
        embeddedProfileEditor?.ShowSection(ProfileEditorSection.None);
        ProfileSettingsButton.Content = "프로필 설정";
        PlaceFeedback();
        ResizeForView();
        UpdateLauncherTargetLabel();
    }

    internal void ShowProfileSettingsPage(int page)
    {
        if (ProfilePages is null || page < 0 || page >= PageTitles.Length) return;
        profileNavigationVersion++;
        if (page == 7 && !settings.CanEditGlobal) page = 0;
        if (!profileSettingsOpen && IsVisible && WindowState == WindowState.Normal)
            settings.MainWindow = DesktopWindows.Remember(this);
        profileSettingsOpen = true;
        LauncherView.Visibility = Visibility.Collapsed;
        ProfileSettingsView.Visibility = Visibility.Visible;
        ProfileSettingsButton.Content = "메인 화면";
        PlaceFeedback();
        ResizeForView();
        ProfilePages.SelectedIndex = page;
        RefreshProfilePage();
    }

    private void ProfilePageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilePages is null || ProfilePageHeading is null || e.Source != ProfilePages) return;
        profileNavigationVersion++;
        RefreshProfilePage();
    }

    private void RefreshProfilePage()
    {
        if (ProfilePages is null || BasicPage is null || embeddedProfileEditor is null) return;
        BasicPage.StopAnimation(); CapturePage.StopAnimation(); AdvancedPage.StopAnimation(); SubtitlePage.StopAnimation();
        var page = ProfilePages.SelectedIndex;
        if (page is < 0 or > 7) page = 0;
        if (page == 7 && !settings.CanEditGlobal)
        {
            ProfilePages.SelectedIndex = 0;
            return;
        }
        ProfilePageHeading.Text = PageTitles[page];
        ProfilePageDescription.Text = PageDescriptions[page];
        BackToGameProfileButton.Visibility = page == 7 && settings.CanEditGlobal && credentialReturnProfileId.HasValue
            ? Visibility.Visible : Visibility.Collapsed;
        BasicPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        CapturePage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = page == 6 ? Visibility.Visible : Visibility.Collapsed;
        SubtitlePage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        RefreshOverlayVisibilityButton();
        ProfileEditorPage.Visibility = page is 2 or 4 or 5 or 7 ? Visibility.Visible : Visibility.Collapsed;
        ProfileEditorPage.Style = page == 7 ? null : (Style)FindResource("UiCard");
        StudyTopmostOption.Visibility = page == 7 && settings.CanEditGlobal
            ? Visibility.Visible : Visibility.Collapsed;
        embeddedProfileEditor.ShowSection(page switch
            {
                2 => ProfileEditorSection.Translation,
                4 => ProfileEditorSection.Input,
                5 => ProfileEditorSection.Dictionary,
                7 => ProfileEditorSection.Global,
                _ => ProfileEditorSection.None
            });
    }

    private void StudyTopmostChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || changing) return;
        if (!settings.CanEditGlobal)
        {
            bool previous = changing; changing = true;
            StudyTopmostOption.IsChecked = settings.StudyTopmost; changing = previous;
            return;
        }
        settings.StudyTopmost = StudyTopmostOption.IsChecked == true;
        study?.RefreshGlobalAccess();
    }

    private void RebuildEmbeddedProfileEditor()
    {
        if (!ready || ProfileEditorHost is null) return;
        embeddedProfileEditor?.Dispose();
        var profile = Current;
        embeddedProfileEditor = new TranslationSettingsEditor(profile, settings.Hotkeys, vault,
            () => settings.CanEditGlobal && settings.ActiveProfileId == profile.Id,
            () => settings.ActiveProfileId == profile.Id, darkTheme: true);
        ConfigureLocalModelEditor(embeddedProfileEditor, profile.Id, this);
        embeddedProfileEditor.GlobalSettingsRequested += async (_, _) => await NavigateToGlobalSettingsAsync();
        embeddedProfileEditor.ServiceSelectionChanged += async (_, _) => {
            if (FirstRunWizard is { } wizard) await wizard.RefreshServiceSelectionAsync();
        };
        embeddedProfileEditor.AttachStudyOption(StudyTopmostOption);
        ProfileEditorHost.Content = embeddedProfileEditor;
        if (!settings.CanEditGlobal && ProfilePages.SelectedIndex == 7)
            ProfilePages.SelectedIndex = 0;
        RefreshProfilePage();
    }

    private async Task NavigateToGlobalSettingsAsync()
    {
        if (!ready || closing) return;
        var source = settings.CanEditGlobal ? credentialReturnProfileId : Current.Id;
        if (!settings.CanEditGlobal && !await SwitchProfileAsync(settings.DefaultProfile)) return;
        credentialReturnProfileId = source;
        ShowProfileSettingsPage(7);
        model.Status = "공통 API 키는 기본 프로필에서 등록·변경하며 모든 게임 프로필이 공유합니다.";
    }

    private async void ReturnToPreviousGameProfile(object sender, RoutedEventArgs e)
    {
        if (closing || credentialReturnProfileId is not Guid id) return;
        var previous = settings.Profiles.FirstOrDefault(p => p.Id == id);
        if (previous is null) { credentialReturnProfileId = null; RefreshProfilePage(); return; }
        if (!await SwitchProfileAsync(previous)) return;
        credentialReturnProfileId = null;
        ShowProfileSettingsPage(2);
    }

    private bool ApplyEmbeddedProfileEditor()
    {
        if (!ApplyWordDictionaryUrls()) return false;
        var editor = embeddedProfileEditor;
        if (editor is null || !editor.HasChanges) {
            if (hotkeyWarning.Length > 0 && !closing) RegisterConfiguredHotkeys();
            return true;
        }
        var profile = Current;
        var previous = CaptureEditorState();
        if (!editor.ApplyChanges(profile, settings.Hotkeys))
        {
            model.Status = editor.LastMessage;
            return false;
        }
        ApplyEditorEffects(previous);
        return true;
    }

    // The editor replaces Translation and Triggers on apply. Capture the effective
    // service fields rather than inactive model slots or dictionary object identities.
    private sealed record ServiceConfiguration(TranslationProvider Provider, string Model, int Timeout,
        string? Endpoint, OllamaModelSettings? Ollama, DeepLApiPlan? Plan, LocalTranslationModel? LocalModel);
    private sealed record EditorState(ServiceConfiguration Service, RecognitionInputPlan Input, TriggerOptions Triggers,
        string? CharacterFile, string[] Hotkeys);
    private EditorState CaptureEditorState()
    {
        var p = Current;
        var options = p.Translation;
        bool ollama = options.Provider == TranslationProvider.Ollama;
        var service = new ServiceConfiguration(options.Provider, options.Model, options.RequestTimeoutSeconds,
            ollama ? options.OllamaAddress : null, ollama ? options.SettingsForOllama() : null,
            options.Provider == TranslationProvider.DeepL ? options.DeepLPlan : null,
            options.Provider == TranslationProvider.HyMtLocal ? options.LocalModel : null);
        var dictionary = p.CharacterDictionaryEnabled && TranslationProviderCatalog.Get(options.Provider).SupportsCharacterDictionary
            ? p.CharacterDictionaryPath : null;
        return new(service, RecognitionInputPlan.For(p, true), p.Triggers, dictionary, DesktopInput.RegisteredHotkeys(settings.Hotkeys));
    }
    private static bool SameBindings(TriggerOptions previous, TriggerOptions current)
    {
        if (previous.MouseButton != current.MouseButton) return false;
        if (previous.Keys == current.Keys && previous.GamepadButtons == current.GamepadButtons) return true;
        try {
            var left = DesktopInput.ParseTriggers(previous);
            var right = DesktopInput.ParseTriggers(current);
            return left.Mask == right.Mask && left.Keys.SetEquals(right.Keys);
        } catch (TranslationException) { return false; } // Repair of a previously invalid binding.
    }
    private void ApplyEditorEffects(EditorState previous)
    {
        var next = CaptureEditorState();
        bool serviceChanged = previous.Service != next.Service;
        if (serviceChanged) localStartCancellation?.Cancel();
        bool inputChanged = previous.Input != next.Input || next.Input.UseTriggers && !SameBindings(previous.Triggers, next.Triggers);
        bool dictionaryChanged = !string.Equals(previous.CharacterFile, next.CharacterFile, StringComparison.OrdinalIgnoreCase);
        if (inputChanged || Current.ShowTranslation && (serviceChanged || dictionaryChanged)) {
            localStartCancellation?.Cancel();
            if (pipeline?.IsRunning == true) {
                StopTranslation();
                model.Status = Current.ShowTranslation ? "설정 변경으로 번역 중지 · 다시 시작하세요."
                    : "설정 변경으로 원문 인식 중지 · 다시 시작하세요.";
            }
        }
        if (serviceChanged) {
            var provider = TranslationProviderCatalog.Get(next.Service.Provider);
            var service = provider.DisplayName + (provider.RequiresModel ? " · " + (next.Service.Model.Length > 0 ? next.Service.Model : "모델 미설정") : "");
            var reason = previous.Service.Provider != next.Service.Provider ? "번역 제공자가 변경되어 이전 문장 해설을 비웠습니다."
                : previous.Service.Model != next.Service.Model || previous.Service.LocalModel != next.Service.LocalModel ? "번역 모델이 변경되어 이전 문장 해설을 비웠습니다." : "서비스 설정이 변경되어 이전 문장 해설을 비웠습니다.";
            InvalidateStudy(reason + "\n현재 서비스: " + service);
        }
        FillTranslation();
        if (hotkeyWarning.Length > 0 || !previous.Hotkeys.SequenceEqual(next.Hotkeys, StringComparer.OrdinalIgnoreCase))
            RegisterConfiguredHotkeys();
    }
    internal Func<HotkeyOptions, string>? RegisterSettingsHotkeys { private get; set; }
    private string hotkeyWarning = "";
    private void RegisterConfiguredHotkeys()
    {
        try { hotkeyWarning = RegisterSettingsHotkeys?.Invoke(settings.Hotkeys) ?? ""; }
        catch (TranslationException ex) { hotkeyWarning = ex.Message; }
        if (hotkeyWarning.Length > 0) model.Status = hotkeyWarning;
        UpdateGameQaHotkeyStatus();
    }

    private void UpdateLauncherTargetLabel()
    {
        if (LauncherTargetLabel is null || !ready) return;
        RefreshGameLaunchButton();
        LauncherTargetLabel.Text = Current.CaptureMode switch
        {
            CaptureMode.Region => "화면 영역",
            CaptureMode.Clipboard => "클립보드",
            CaptureMode.Window when target is not null => target.Title,
            CaptureMode.Window when Current.GameWindow is not null => "게임 창 연결 대기",
            _ => "게임 창 미지정"
        };
    }
}
