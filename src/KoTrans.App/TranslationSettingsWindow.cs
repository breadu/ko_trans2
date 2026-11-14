using KoTrans.Infrastructure;
using System.Windows.Data;
using System.Windows.Documents;

namespace KoTrans.App;

internal enum ProfileEditorSection { All, Translation, Input, Dictionary, Global, None }

internal sealed partial class TranslationSettingsEditor : UserControl, IDisposable
{
    private TranslationOptions draft;
    private readonly Func<bool> canEditGlobal, canEditProfile;
    private readonly Profile boundProfile;
    private readonly HotkeyOptions boundHotkeys;
    private readonly StackPanel globalKeys = new() { Name = "GlobalKeys" }, globalHotkeys = new() { Name = "GlobalHotkeys" };
    private readonly StackPanel studyOptions = new();
    private readonly Border studyCard, credentialsCard, hotkeysCard;
    internal void AttachStudyOption(CheckBox option)
    {
        if (option.Parent is Panel parent) parent.Children.Remove(option);
        studyOptions.Children.Add(option); option.Margin = new Thickness(0, 5, 0, 0);
        RefreshAccess();
    }
    private readonly StackPanel translationPanel = new(), dictionaryPanel = new(), triggerPanel = new() { Name = "ProfileTriggerFields" };
    private readonly StackPanel triggerGroup = new() { Name = "InputTriggerGroup" }, triggerFields = new() { Name = "InputTriggerFields" };
    private readonly TextBlock recognitionModeHelp = new() { Name = "RecognitionModeHelp", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel globalNavigation = new();
    private readonly TextBlock globalNavigationHelp = new()
    {
        Name = "GlobalNavigationHelp",
        Foreground = new SolidColorBrush(Color.FromRgb(113, 177, 221)),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button goToGlobal = new()
    {
        Name = "GoToGlobalCredentials",
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 10, 0, 0)
    };
    internal event EventHandler? GlobalSettingsRequested;
    internal event EventHandler? ServiceSelectionChanged;
    internal TranslationProvider SelectedProvider => draft.Provider;
    internal LocalTranslationModel SelectedLocalModel => draft.LocalModel;
    private ProfileEditorSection section = ProfileEditorSection.All;
    private bool dirty;
    private readonly ApiKeyStore vault;
    private const string StoredKeyMask = "********"; // visual placeholder, never the real key
    private bool updatingStoredKeyMask, showingStoredKeyMask;
    internal bool HasChanges => dirty;
    internal bool HasUnregisteredCredential
    {
        get {
            if (showingStoredKeyMask || secret.Visibility != Visibility.Visible) return false;
            using var input = secret.SecurePassword;
            return input.Length > 0;
        }
    }
    internal string LastMessage => status.Text;
    internal void ShowSection(ProfileEditorSection value)
    {
        if (section != value) { SetStatus(""); scroll.StopAnimation(); }
        section = value;
        translationPanel.Visibility = value is ProfileEditorSection.All or ProfileEditorSection.Translation ? Visibility.Visible : Visibility.Collapsed;
        dictionaryPanel.Visibility = value is ProfileEditorSection.All or ProfileEditorSection.Dictionary ? Visibility.Visible : Visibility.Collapsed;
        triggerPanel.Visibility = value is ProfileEditorSection.All or ProfileEditorSection.Input ? Visibility.Visible : Visibility.Collapsed;
        RefreshAccess();
    }
    private readonly SmoothScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    internal (ScrollViewer Scroll, FrameworkElement Caption) ProviderNavigation => (scroll, providerCaption);
    public void Dispose() { disposed = true; CancelConnectionCheck(); DisposeLocalModel(); activeModelList?.Close(); scroll.Dispose(); }
    private readonly TextBlock credentialState = new() { Name = "CredentialStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock providerCredentialState = new() { Name = "ProviderCredentialStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly WrapPanel keyButtons = new() { ItemHeight = 46 };
    private readonly TextBlock providerHelp = new() { Name = "ProviderHelp", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock ollamaHelp = new()
    {
        Name = "OllamaFormatHelp", TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(159, 185, 208)),
        Margin = new Thickness(0, 6, 0, 0)
    };
    private readonly TextBlock keepAliveHelp = new()
    {
        Name = "OllamaKeepAliveHelp", Text = "길게 설정하면 재로딩은 줄지만 메모리를 계속 사용합니다.",
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
    };
    private readonly ComboBox credentialProvider = new() { Name = "CredentialProvider", ItemsSource = TranslationProviderCatalog.All.Where(p => p.Credentials.Count > 0).Select(p => p.Provider).ToArray() };
    private readonly ComboBox credentialField = new() { Name = "CredentialField", DisplayMemberPath = "Label" };
    private readonly ComboBox deeplPlan = new() { Name = "DeepLPlan", ItemsSource = Enum.GetValues<DeepLApiPlan>() };
    private TextBlock timeoutCaption = null!;
    private readonly TextBlock providerCaption, modelCaption, endpointCaption, ollamaFormatCaption, keepAliveCaption, deeplPlanCaption, credentialCaption, secretCaption, qaHotkeyCaption;
    private readonly CheckBox dictionaryEnabled = new() { Name = "DictionaryEnabled", Content = "캐릭터 사전 사용" };
    private readonly RadioButton watch = new() { Name = "AutomaticWatch", Content = "화면 자동 인식 사용" };
    private readonly TextBox delay = new() { Name = "TriggerDelayMs" };
    internal void RefreshAccess()
    {
        bool wasVisible = globalKeys.Visibility == Visibility.Visible;
        bool visible = canEditGlobal() && (section is ProfileEditorSection.All or ProfileEditorSection.Global);
        globalKeys.Visibility = globalHotkeys.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        credentialsCard.Visibility = hotkeysCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        studyCard.Visibility = visible && studyOptions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshCredentialVisibility();
        if (!visible || secret.Visibility != Visibility.Visible) { showingStoredKeyMask = false; secret.Clear(); credentialState.Text = ""; }
        else if (!wasVisible || showingStoredKeyMask || secret.Password.Length == 0) RefreshCredentialMask();
        // Test-capture hotkeys stay in the source/legacy editor, not in the product profile UI.
        qaHotkeyCaption.Visibility = saveQa.Visibility = section == ProfileEditorSection.All ? Visibility.Visible : Visibility.Collapsed;
        RefreshProviderCredentialState();
        RefreshGlobalNavigation();
        RefreshLocalModelAccess();
        RefreshRecognitionMode();
    }
    private void RefreshRecognitionMode()
    {
        bool clipboard = boundProfile.CaptureMode == CaptureMode.Clipboard;
        watch.IsEnabled = canEditProfile() && !clipboard;
        watch.Opacity = watch.IsEnabled ? 1 : .45;
        triggerGroup.IsEnabled = canEditProfile() && !clipboard;
        triggerGroup.Opacity = triggerGroup.IsEnabled ? 1 : .45;
        enabled.IsEnabled = watch.IsEnabled;
        triggerFields.IsEnabled = enabled.IsChecked == true && !clipboard;
        triggerFields.Opacity = triggerGroup.IsEnabled && !triggerFields.IsEnabled ? .45 : 1;
        bool hasTriggers = RecognitionInputPlan.HasTriggers(new TriggerOptions { Keys = keys.Value, MouseButton = mouse.SelectedItem as string ?? "None", GamepadButtons = pad.Value });
        recognitionModeHelp.Text = clipboard
            ? "번역 시작 후 클립보드 변경을 감시합니다. 화면 감시와 게임 입력 트리거는 사용하지 않습니다."
            : watch.IsChecked == true
                ? "번역 시작 후 화면 변화를 감지하고, 문장이 안정되면 자동으로 OCR·번역합니다."
                : enabled.IsChecked == true
                    ? hasTriggers
                        ? "번역 시작 후 등록한 키·마우스·게임패드 버튼을 누르면 화면을 확인하고, 문장이 안정되면 OCR·번역합니다."
                        : "선택된 입력이 없습니다. 키보드·마우스·게임패드 입력을 선택하거나 화면 자동 인식을 선택하세요."
                    : "인식 방식이 선택되지 않았습니다. 화면 자동 인식 또는 입력 트리거 사용을 선택하세요.";
    }
    private readonly TextBox keepAlive = new() { Name = "OllamaKeepAlive" };
    private string configuredOllamaModel = "";
    private readonly ComboBox ollamaFormat = new() { ItemsSource = Enum.GetValues<OllamaRequestFormat>(), Name = "OllamaFormat" };
    private readonly ComboBox provider = new() { ItemsSource = TranslationProviderCatalog.All.Select(p => p.Provider).ToArray() };
    private readonly TextBox model = new(), endpoint = new(), timeout = new(), dictionary = new();
    private readonly Button connectionCheck = new() { Name = "OllamaConnectionCheck", Content = "연결 확인", Width = 104, Margin = new Thickness(8, 5, 0, 10), Padding = new Thickness(9, 6, 9, 6) };
    private readonly TextBlock connectionStatus = new() { Name = "OllamaConnectionStatus", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly DockPanel endpointRow = new();
    private readonly Func<string, CancellationToken, Task<System.Collections.Generic.IReadOnlyList<ProviderModel>>> ollamaLookup;
    private CancellationTokenSource? connectionRequest;
    private bool disposed;
    private readonly Button modelList = new() { Name = "ModelListButton", Content = "모델 목록", Width = 104, Margin = new Thickness(8, 5, 0, 10), Padding = new Thickness(9, 6, 9, 6) };
    private readonly DockPanel modelRow = new();
    private ModelListWindow? activeModelList;
    private readonly KeyboardTriggerEditor keys = new() { Name = "TriggerKeys" };
    private readonly GamepadTriggerEditor pad = new() { Name = "TriggerPad" };
    private readonly HotkeyCaptureBox show = new(), toggle = new(), explain = new(), scrollUp = new(), scrollDown = new(), saveQa = new() { AllowControl = false, IsEnabled = false };
    private readonly ComboBox mouse = new() { ItemsSource = new[] { "None", "Left", "Right", "Middle", "X1", "X2" } };
    private readonly RadioButton enabled = new() { Name = "GameTriggersEnabled", Content = "입력 트리거 사용" };
    // Match the text fields' 11x8 padding/margins; no oversized min-height.
    private readonly PasswordBox secret = new() { MaxLength = 4096,
        Padding = new Thickness(11, 8, 11, 8), Margin = new Thickness(0, 5, 0, 10) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private void SetStatus(string message) { status.Text = message; status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }
    internal TranslationSettingsEditor(Profile profile, HotkeyOptions hotkeys, ApiKeyStore vault, Func<bool>? canEditGlobal = null, Func<bool>? canEditProfile = null, bool darkTheme = false,
        Func<string, CancellationToken, Task<System.Collections.Generic.IReadOnlyList<ProviderModel>>>? ollamaLookup = null)
    {
        this.canEditGlobal = canEditGlobal ?? (() => true); this.canEditProfile = canEditProfile ?? (() => true);
        boundProfile = profile; boundHotkeys = hotkeys; this.vault = vault;
        this.ollamaLookup = ollamaLookup ?? ProviderModelList.LoadOllamaAsync;
        secret.GotKeyboardFocus += (_, _) => { if (showingStoredKeyMask) secret.SelectAll(); };
        secret.PreviewTextInput += (_, _) => ClearStoredMaskForEditing();
        DataObject.AddPastingHandler(secret, (_, _) => ClearStoredMaskForEditing());
        secret.PreviewKeyDown += (_, e) => {
            if (showingStoredKeyMask && e.Key is System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete) {
                ClearStoredMaskForEditing(); e.Handled = true;
            }
        };
        secret.LostKeyboardFocus += (_, _) => { if (secret.Password.Length == 0) RefreshCredentialMask(); };
        secret.PasswordChanged += (_, _) => {
            if (!updatingStoredKeyMask) {
                showingStoredKeyMask = false;
                credentialState.Text = HasUnregisteredCredential
                    ? "새 API 키는 아직 저장되지 않았습니다. 이 페이지를 떠나기 전에 ‘키 등록/변경’을 누르세요."
                    : "";
            }
        };
        draft = profile.Translation with { }; draft.Validate();
        provider.Name = "Provider"; model.Name = "Model"; endpoint.Name = "Endpoint"; timeout.Name = "Timeout";
        show.Name = "ShowHotkey"; toggle.Name = "ToggleHotkey"; explain.Name = "ExplainHotkey";
        saveQa.Name = "SaveGameQaHotkey";
        scrollUp.Name = "ScrollUpHotkey"; scrollDown.Name = "ScrollDownHotkey"; status.Name = "SettingsStatus";
        keys.Name = "TriggerKeys"; pad.Name = "TriggerPad"; mouse.Name = "MouseTrigger";
        var panel = new StackPanel { Margin = new Thickness(0, 2, 12, 2) };
        Border GlobalCard(string name, string title, StackPanel fields) {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            content.Children.Add(fields);
            return new Border { Name = name, Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1), BorderBrush = darkTheme ? new SolidColorBrush(Color.FromRgb(52, 78, 101)) : Brushes.LightGray,
                Background = darkTheme ? new SolidColorBrush(Color.FromRgb(28, 43, 61)) : Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
        }
        studyCard = GlobalCard("GlobalStudyCard", "문장 해설창", studyOptions);
        credentialsCard = GlobalCard("GlobalCredentialsCard", "API 제공자 인증정보", globalKeys);
        hotkeysCard = GlobalCard("GlobalHotkeysCard", "단축키", globalHotkeys);
        panel.Children.Add(studyCard);
        panel.Children.Add(translationPanel);
        panel.Children.Add(globalNavigation);
        panel.Children.Add(dictionaryPanel);
        panel.Children.Add(triggerPanel);
        panel.Children.Add(credentialsCard);
        panel.Children.Add(hotkeysCard);
        panel.Children.Add(status);
        scroll.Content = panel; Content = scroll;
        Panel fieldPanel = translationPanel;
        TextBlock Field(string title, FrameworkElement control) { var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }; fieldPanel.Children.Add(label); fieldPanel.Children.Add(control); return label; }
        providerCaption = Field("번역 제공자", provider);
        translationPanel.Children.Add(providerCredentialState);
        BuildLocalModelControls(darkTheme);
        DockPanel.SetDock(connectionCheck, Dock.Right); endpointRow.Children.Add(connectionCheck); endpointRow.Children.Add(endpoint);
        endpointCaption = Field("Ollama 서버 주소", endpointRow);
        translationPanel.Children.Add(connectionStatus);
        connectionCheck.Click += async (_, _) => await CheckOllamaConnectionAsync();
        DockPanel.SetDock(modelList, Dock.Right); modelRow.Children.Add(modelList); modelRow.Children.Add(model);
        modelCaption = Field("모델 ID", modelRow);
        modelList.Click += (_, _) => {
            if (activeModelList is not null || !this.canEditProfile()) return;
            var dialog = CreateModelListWindow(); activeModelList = dialog; dialog.Owner = Window.GetWindow(this);
            try { dialog.ShowDialog(); } finally { activeModelList = null; }
        };
        Unloaded += (_, _) => { CancelConnectionCheck(); activeModelList?.Close(); };
        timeoutCaption = Field("응답 대기 시간 (초)", timeout);
        timeout.ToolTip = "5~300초 사이에서 설정하세요.";
        ollamaFormatCaption = Field("요청 방식", ollamaFormat);
        translationPanel.Children.Add(ollamaHelp);
        keepAliveCaption = Field("모델 유지 시간 (분)", keepAlive);
        keepAlive.ToolTip = "0~60분 사이에서 설정하세요.";
        translationPanel.Children.Add(keepAliveHelp);
        deeplPlanCaption = Field("DeepL API 요금제", deeplPlan);
        translationPanel.Children.Add(providerHelp);
        globalNavigation.Margin = new Thickness(0, 15, 0, 8);
        globalNavigation.Children.Add(globalNavigationHelp);
        goToGlobal.Click += (_, _) => GlobalSettingsRequested?.Invoke(this, EventArgs.Empty);
        globalNavigation.Children.Add(goToGlobal);
        provider.SelectedItem = profile.Translation.Provider; endpoint.Text = profile.Translation.OllamaAddress;
        deeplPlan.SelectedItem = profile.Translation.DeepLPlan;
        model.LostFocus += (_, _) => FlushOllamaModelChange();
        fieldPanel = globalKeys;
        Field("API 제공자 · 번역 서비스 선택과 별도", credentialProvider);
        credentialCaption = Field("인증 항목", credentialField);
        secretCaption = Field("인증 정보 · Windows 계정으로 보호", secret);
        globalKeys.Children.Add(credentialState);
        globalKeys.Children.Add(keyButtons);
        void KeyAction(string title, Action action)
        {
            var button = new Button { Content = title }; keyButtons.Children.Add(button);
            button.Click += (_, _) => { if (!this.canEditGlobal()) { RefreshAccess(); return; } try { action(); RefreshCredentialMask(); RefreshProviderCredentialState(); } catch (Exception ex) when (ex is TranslationException or IOException or UnauthorizedAccessException) { SetStatus(ex is TranslationException ? ex.Message : "키 파일을 처리할 수 없습니다."); } };
        }
        string CredentialId() => (credentialField.SelectedItem as TranslationCredentialField)?.Id
            ?? throw new TranslationException("인증 항목을 선택하세요.");
        KeyAction("키 등록/변경", () => {
            if (showingStoredKeyMask || string.IsNullOrWhiteSpace(secret.Password))
                throw new TranslationException("기존 키는 보호되어 있습니다. 새 API 키를 입력한 후 등록하세요.");
            vault.SaveCredential((TranslationProvider)credentialProvider.SelectedItem, CredentialId(), secret.Password);
            SetStatus("인증 정보를 DPAPI로 저장했습니다.");
        });
        KeyAction("키 삭제", () => { vault.DeleteCredential((TranslationProvider)credentialProvider.SelectedItem, CredentialId()); SetStatus("선택한 인증 정보를 삭제했습니다."); });
        KeyAction("기존 INI 키 가져오기…", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "기존 KO Trans 설정|*.ini" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true && this.canEditGlobal()) SetStatus($"{vault.ImportSelectedIni(picker.FileName)}개 키를 가져왔습니다. 원본 파일은 보존했습니다.");
        });
        provider.SelectionChanged += (_, _) => {
            if (provider.SelectedItem is not TranslationProvider selected) return;
            CancelConnectionCheck(); connectionStatus.Text = ""; connectionStatus.Visibility = Visibility.Collapsed;
            activeModelList?.Close();
            FlushOllamaModelChange(); draft.Model = model.Text.Trim();
            if (draft.Provider == TranslationProvider.Ollama) RememberOllamaFields(draft.Model);
            else if (int.TryParse(timeout.Text, out var seconds) && seconds is >= 5 and <= 300) draft.TimeoutSeconds = seconds;
            if (draft.Provider == TranslationProvider.DeepL && deeplPlan.SelectedItem is DeepLApiPlan plan) draft.DeepLPlan = plan;
            draft.SwitchProvider(selected); LoadProviderFields(); RefreshAccess();
            dirty = true;
            SetStatus("");
            ServiceSelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        fieldPanel = dictionaryPanel;
        dictionaryEnabled.IsChecked = profile.CharacterDictionaryEnabled;
        var help = new Button
        {
            Name = "CharacterDictionaryHelpButton", Content = "ⓘ 사용 안내",
            ToolTip = "캐릭터 사전의 용도, JSON 예시, AI 활용 방법",
            // Use the same implicit button theme as '사전 선택…'; do not draw an accent.
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(8, 0, 0, 6)
        };
        var dictionaryHeader = new DockPanel { Name = "CharacterDictionaryHeader", Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(help, Dock.Right);
        dictionaryHeader.Children.Add(help);
        dictionaryHeader.Children.Add(dictionaryEnabled);
        dictionaryPanel.Children.Add(dictionaryHeader);

        dictionary.Name = "CharacterDictionaryPath";
        var browse = new Button
        {
            Name = "CharacterDictionaryBrowseButton", Content = "사전 선택…",
            MinWidth = 112, Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(8, 5, 0, 10)
        };
        var dictionaryFileRow = new DockPanel { Name = "CharacterDictionaryFileRow" };
        DockPanel.SetDock(browse, Dock.Right);
        dictionaryFileRow.Children.Add(browse);
        dictionaryFileRow.Children.Add(dictionary);
        Field("사전 파일 (JSON)", dictionaryFileRow);
        dictionary.Text = profile.CharacterDictionaryPath;

        browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "캐릭터 사전|*.json" }; if (picker.ShowDialog(Window.GetWindow(this)) == true) dictionary.Text = picker.FileName; };
        help.Click += (_, _) =>
        {
            var guide = new CharacterDictionaryHelpWindow { Owner = Window.GetWindow(this) };
            guide.ShowDialog();
        };
        watch.GroupName = enabled.GroupName = "RecognitionMode" + Guid.NewGuid().ToString("N");
        watch.IsChecked = profile.AutomaticWatch;
        var autoGroup = new StackPanel(); autoGroup.Children.Add(watch);
        autoGroup.Children.Add(new TextBlock { Text = "화면 확인 시작부터 안정화·OCR·번역까지 자동으로 처리합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 8, 0, 0) });
        Border Card(UIElement child) => new() { Child = child, BorderBrush = darkTheme ? new SolidColorBrush(Color.FromRgb(69, 96, 122)) : Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12) };
        triggerPanel.Children.Add(Card(autoGroup)); triggerPanel.Children.Add(Card(triggerGroup));
        triggerPanel.Children.Add(recognitionModeHelp);
        triggerGroup.Children.Add(enabled); enabled.IsChecked = !profile.AutomaticWatch && profile.Triggers.Enabled;
        triggerGroup.Children.Add(new TextBlock { Text = "입력으로 시작한 뒤에는 화면 자동 인식과 같은 방식으로 안정된 문장을 확인합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 8, 0, 10) });
        triggerGroup.Children.Add(triggerFields); fieldPanel = triggerFields;
        keys.SetDarkTheme(darkTheme); pad.SetDarkTheme(darkTheme);
        Field("키보드 트리거", keys); keys.Value = profile.Triggers.Keys;
        Field("마우스 트리거", mouse); mouse.SelectedItem = profile.Triggers.MouseButton;
        Field("게임패드 트리거 · 버튼을 직접 눌러 추가", pad); pad.Value = profile.Triggers.GamepadButtons;
        Field("입력 트리거 후 OCR 대기 (0–5000ms) · 문장 안정화와 별도", delay); delay.Text = profile.TriggerDelayMs.ToString();
        credentialProvider.SelectionChanged += (_, _) => LoadCredentialFields();
        credentialField.SelectionChanged += (_, _) => RefreshCredentialMask();
        credentialProvider.SelectedItem = TranslationProviderCatalog.All.First(p => p.Credentials.Count > 0).Provider;
        fieldPanel = globalHotkeys;
        Field("설정창 표시 단축키", show); show.Text = hotkeys.ShowSettings;
        Field("번역 시작/중지 단축키", toggle); toggle.Text = hotkeys.ToggleTranslation;
        Field("현재 문장 학습 단축키", explain); explain.Text = hotkeys.Explain;
        Field("학습창 위로 스크롤", scrollUp); scrollUp.Text = hotkeys.StudyScrollUp;
        Field("학습창 아래로 스크롤", scrollDown); scrollDown.Text = hotkeys.StudyScrollDown;
        qaHotkeyCaption = Field("문제 장면 저장키 · 현재 연결 안 함", saveQa); saveQa.Text = hotkeys.SaveGameQa;
        globalHotkeys.Children.Add(new TextBlock {
            Text = "단축키 칸을 클릭하고 새 키 조합을 누르세요. Tab은 이동, Esc는 변경 취소입니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        globalHotkeys.Children.Add(new TextBlock { Text = "학습 단축키는 명시적으로 누를 때만 분석합니다. 스크롤 단축키는 학습창에 포커스가 없어도 동작하며, 숨긴 학습창은 열지 않습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        LoadProviderFields();
        ShowSection(ProfileEditorSection.All);
        foreach (var box in new TextBox[] { model, endpoint, timeout, dictionary, show, toggle, explain, scrollUp, scrollDown, saveQa, delay, keepAlive })
            box.TextChanged += (_, _) => dirty = true;
        endpoint.TextChanged += (_, _) => {
            if (draft.Provider != TranslationProvider.Ollama) return;
            CancelConnectionCheck();
            connectionStatus.Text = "주소가 변경됐습니다. 연결 확인을 눌러 서버 상태를 확인하세요.";
            connectionStatus.Visibility = Visibility.Visible;
            activeModelList?.Close();
        };
        keys.ValueChanged += (_, _) => { dirty = true; RefreshRecognitionMode(); };
        pad.ValueChanged += (_, _) => { dirty = true; RefreshRecognitionMode(); };
        foreach (var combo in new[] { ollamaFormat, deeplPlan, mouse })
            combo.SelectionChanged += (_, _) => {
                dirty = true;
                if (combo == mouse) RefreshRecognitionMode();
                if (combo == ollamaFormat) RefreshOllamaFormatHelp();
            };
        foreach (var checkbox in new System.Windows.Controls.Primitives.ToggleButton[] { dictionaryEnabled, watch, enabled })
        {
            checkbox.Checked += (_, _) => { dirty = true; if (checkbox == watch || checkbox == enabled) RefreshRecognitionMode(); };
            checkbox.Unchecked += (_, _) => { dirty = true; if (checkbox == watch || checkbox == enabled) RefreshRecognitionMode(); };
        }
        if (darkTheme) ApplyDarkStyles();
        // Display-only Korean labels: keep ComboBox items and persisted tokens in English.
        var mouseTemplate = new DataTemplate();
        var mouseText = new FrameworkElementFactory(typeof(TextBlock));
        mouseText.SetBinding(TextBlock.TextProperty,
            new Binding(".") { Converter = new MouseTriggerLabelConverter() });
        if (darkTheme) mouseText.SetResourceReference(TextBlock.StyleProperty, "UiComboText");
        mouseTemplate.VisualTree = mouseText; mouse.ItemTemplate = mouseTemplate;
        // Friendly labels in both embedded dark and standalone light editors.
        var formatTemplate = new DataTemplate();
        var formatText = new FrameworkElementFactory(typeof(TextBlock));
        formatText.SetBinding(TextBlock.TextProperty,
            new Binding(".") { Converter = new OllamaRequestFormatLabelConverter() });
        if (darkTheme) formatText.SetResourceReference(TextBlock.StyleProperty, "UiComboText");
        formatTemplate.VisualTree = formatText;
        ollamaFormat.ItemTemplate = formatTemplate;
        var localProviderTemplate = new DataTemplate();
        var localProviderText = new FrameworkElementFactory(typeof(TextBlock));
        localProviderText.SetBinding(TextBlock.TextProperty, new Binding(".") { Converter = new ProviderNameConverter() });
        localProviderText.SetResourceReference(TextBlock.StyleProperty, "UiComboText");
        localProviderTemplate.VisualTree = localProviderText; provider.ItemTemplate = localProviderTemplate;
    }
    private void ApplyDarkStyles()
    {
        static Style Theme(Type type, params Setter[] setters)
        {
            var s = new Style(type);
            foreach (var setter in setters) s.Setters.Add(setter);
            return s;
        }
        var ink = new SolidColorBrush(Color.FromRgb(231, 241, 251));
        var inputBackground = new SolidColorBrush(Color.FromRgb(20, 35, 51));
        var outline = new SolidColorBrush(Color.FromRgb(69, 96, 122));
        Resources[typeof(TextBlock)] = Theme(typeof(TextBlock),
            new Setter(TextBlock.ForegroundProperty, ink));
        Resources[typeof(TextBox)] = Theme(typeof(TextBox),
            new Setter(TextBox.BackgroundProperty, inputBackground),
            new Setter(TextBox.ForegroundProperty, ink),
            new Setter(TextBox.BorderBrushProperty, outline),
            new Setter(TextBox.PaddingProperty, new Thickness(11, 8, 11, 8)),
            new Setter(TextBox.MarginProperty, new Thickness(0, 5, 0, 10)));
        Resources[typeof(PasswordBox)] = Theme(typeof(PasswordBox),
            new Setter(PasswordBox.BackgroundProperty, inputBackground),
            new Setter(PasswordBox.ForegroundProperty, ink),
            new Setter(PasswordBox.BorderBrushProperty, outline),
            new Setter(PasswordBox.PaddingProperty, new Thickness(11, 8, 11, 8)),
            new Setter(PasswordBox.MarginProperty, new Thickness(0, 5, 0, 10)));
        // The editor has a global light-ink TextBlock style. A selected ComboBox
        // otherwise creates its own light TextBlock on a white selection surface.
        static DataTemplate ComboTextTemplate(string path)
        {
            var result = new DataTemplate();
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(path));
            text.SetResourceReference(TextBlock.StyleProperty, "UiComboText");
            result.VisualTree = text;
            return result;
        }
        var regularItem = ComboTextTemplate(".");
        foreach (var combo in new[] { provider, ollamaFormat, deeplPlan, mouse, credentialProvider })
            combo.ItemTemplate = regularItem;
        credentialField.DisplayMemberPath = "";
        credentialField.ItemTemplate = ComboTextTemplate(nameof(TranslationCredentialField.Label));
        localModelChoice.DisplayMemberPath = "";
        localModelChoice.ItemTemplate = ComboTextTemplate(nameof(LocalTranslationModelInfo.DisplayName));
        Resources[typeof(HotkeyCaptureBox)] = Theme(typeof(HotkeyCaptureBox),
            new Setter(TextBox.BackgroundProperty, inputBackground),
            new Setter(TextBox.ForegroundProperty, ink),
            new Setter(TextBox.BorderBrushProperty, outline),
            new Setter(TextBox.PaddingProperty, new Thickness(11, 8, 11, 8)),
            new Setter(TextBox.MarginProperty, new Thickness(0, 5, 0, 10)));
        Resources[typeof(RadioButton)] = Theme(typeof(RadioButton),
            new Setter(RadioButton.ForegroundProperty, ink),
            new Setter(RadioButton.MarginProperty, new Thickness(0, 6, 0, 6)));
        var buttons = Theme(typeof(Button),
            new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(42, 69, 94))),
            new Setter(Button.ForegroundProperty, ink),
            new Setter(Button.BorderBrushProperty, outline),
            new Setter(Button.PaddingProperty, new Thickness(12, 8, 12, 8)),
            new Setter(Button.MarginProperty, new Thickness(0, 5, 8, 6)));
        buttons.BasedOn = (Style)FindResource("DarkDisabledButton");
        Resources[typeof(Button)] = buttons;
    }
    private void RefreshOllamaFormatHelp()
    {
        ollamaHelp.Text = ollamaFormat.SelectedItem switch
        {
            OllamaRequestFormat.Chat => "일반 대화형 모델에 사용합니다. 문장 해설은 모델에 따라 지원 여부가 달라집니다.",
            OllamaRequestFormat.TranslateGemma => "번역 전용 방식입니다. 문장 해설과 캐릭터 사전 지시를 지원하지 않습니다.",
            OllamaRequestFormat.HyMt => "HY-MT 계열 번역 모델에 사용합니다. 이전 원문과 캐릭터 이름 표기를 참고하며, 문장 해설은 지원하지 않습니다.",
            _ => "사용할 모델에 맞는 요청 방식을 선택하세요."
        };
    }

    private void CancelConnectionCheck()
    {
        var request = connectionRequest; connectionRequest = null;
        request?.Cancel();
        if (!disposed) connectionCheck.IsEnabled = canEditProfile() && draft.Provider == TranslationProvider.Ollama;
    }

    internal async Task CheckOllamaConnectionAsync()
    {
        if (disposed || draft.Provider != TranslationProvider.Ollama || !canEditProfile()) return;
        CancelConnectionCheck();
        var request = new CancellationTokenSource(); connectionRequest = request;
        var address = endpoint.Text.Trim();
        connectionCheck.IsEnabled = false;
        connectionStatus.Visibility = Visibility.Visible;
        connectionStatus.Text = "Ollama 서버 연결 확인 중…";
        try
        {
            var models = await ollamaLookup(address, request.Token);
            if (!disposed && ReferenceEquals(connectionRequest, request) && endpoint.Text.Trim() == address && draft.Provider == TranslationProvider.Ollama)
                connectionStatus.Text = models.Count == 0 ? "Ollama 연결됨 · 설치된 모델 없음" : $"Ollama 연결됨 · 설치된 모델 {models.Count}개";
        }
        catch (OperationCanceledException)
        {
            if (!disposed && ReferenceEquals(connectionRequest, request) && endpoint.Text.Trim() == address && draft.Provider == TranslationProvider.Ollama)
                connectionStatus.Text = "연결 확인을 취소했습니다.";
        }
        catch (Exception ex) when (ex is TranslationException or IOException or UnauthorizedAccessException)
        {
            if (!disposed && ReferenceEquals(connectionRequest, request) && endpoint.Text.Trim() == address && draft.Provider == TranslationProvider.Ollama)
                connectionStatus.Text = ex is TranslationException ? ex.Message : "Ollama 서버에 연결할 수 없습니다. 주소와 실행 상태를 확인하세요.";
        }
        finally
        {
            if (ReferenceEquals(connectionRequest, request))
            {
                connectionRequest = null;
                if (!disposed) connectionCheck.IsEnabled = canEditProfile() && draft.Provider == TranslationProvider.Ollama;
            }
            request.Dispose();
        }
    }

    private void RememberOllamaFields(string id)
    {
        if (!string.IsNullOrWhiteSpace(id) && id.Length <= 200 && !id.Any(char.IsControl) && ollamaFormat.SelectedItem is OllamaRequestFormat format && int.TryParse(timeout.Text, out var seconds) && seconds is >= 5 and <= 300)
            if (int.TryParse(keepAlive.Text,out var minutes) && minutes is >= 0 and <= 60)
                draft.OllamaModels = draft.OllamaModels.SetItem(id, new(format, seconds, minutes));
    }
    private void RefreshGlobalNavigation()
    {
        bool show = GlobalSettingsRequested is not null && section == ProfileEditorSection.Translation
            && TranslationProviderCatalog.Get(draft.Provider).Credentials.Count > 0;
        globalNavigation.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        bool isDefault = canEditGlobal();
        globalNavigationHelp.Text = isDefault
            ? "현재 기본 프로필입니다. API 키는 모든 프로필이 공유하며 공통 설정에서 관리합니다."
            : "API 키는 모든 프로필이 공유합니다. 이동하면 번역이 중지됩니다.";
        goToGlobal.Content = isDefault ? "API 키 관리" : "기본 프로필에서 API 키 관리 →";
    }
    private void RefreshProviderCredentialState()
    {
        var info = TranslationProviderCatalog.Get(draft.Provider);
        providerCredentialState.Text = info.Credentials.Count == 0
            ? $"{info.DisplayName} · API 키가 필요하지 않습니다."
            : info.Credentials.All(field => vault.ContainsCredential(info.Provider, field.Id))
                ? $"{info.DisplayName} · API 키 등록됨"
                : $"{info.DisplayName} · API 키 미등록 · 기본 프로필에서 등록하세요.";
    }
    private void LoadProviderFields()
    {
        var info = TranslationProviderCatalog.Get(draft.Provider);
        RefreshLocalModelState();
        timeoutCaption.Visibility = timeout.Visibility = draft.Provider == TranslationProvider.HyMtLocal ? Visibility.Collapsed : Visibility.Visible;
        model.Text = draft.Model; timeout.Text = draft.RequestTimeoutSeconds.ToString();
        var showModel = info.RequiresModel ? Visibility.Visible : Visibility.Collapsed;
        modelCaption.Visibility = modelRow.Visibility = model.Visibility = showModel;
        modelList.Visibility = draft.Provider is TranslationProvider.Gemini or TranslationProvider.OpenAI or TranslationProvider.Ollama ? Visibility.Visible : Visibility.Collapsed;
        var showOllama = draft.Provider == TranslationProvider.Ollama ? Visibility.Visible : Visibility.Collapsed;
        ollamaHelp.Visibility = keepAliveHelp.Visibility = showOllama;
        endpointCaption.Visibility = endpointRow.Visibility = showOllama;
        connectionCheck.IsEnabled = canEditProfile() && showOllama == Visibility.Visible && connectionRequest is null;
        if (showOllama == Visibility.Visible) {
            if (connectionStatus.Text.Length == 0) connectionStatus.Text = "연결 확인을 눌러 서버 상태를 확인하세요.";
            connectionStatus.Visibility = Visibility.Visible;
        } else connectionStatus.Visibility = Visibility.Collapsed;
        ollamaFormatCaption.Visibility = ollamaFormat.Visibility = showOllama;
        keepAliveCaption.Visibility = keepAlive.Visibility = showOllama;
        ollamaFormat.IsEnabled = draft.Provider == TranslationProvider.Ollama;
        keepAlive.IsEnabled = ollamaFormat.IsEnabled;
        keepAlive.Text = draft.SettingsForOllama(draft.Provider == TranslationProvider.Ollama ? draft.Model : draft.OllamaModel).KeepAliveMinutes.ToString();
        ollamaFormat.SelectedItem = draft.SettingsForOllama(draft.Provider == TranslationProvider.Ollama ? draft.Model : draft.OllamaModel).Format;
        RefreshOllamaFormatHelp();
        var showDeepL = draft.Provider == TranslationProvider.DeepL ? Visibility.Visible : Visibility.Collapsed;
        deeplPlanCaption.Visibility = deeplPlan.Visibility = showDeepL;
        deeplPlan.SelectedItem = draft.DeepLPlan;
        if (section == ProfileEditorSection.All && info.Credentials.Count > 0)
            credentialProvider.SelectedItem = info.Provider;
        // Keep global credentials independent of translation/model edits.
        RefreshCredentialVisibility();
        providerHelp.Visibility = showOllama == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        providerHelp.Text = draft.Provider switch
        {
            TranslationProvider.HyMtLocal => "인터넷 연결 없이 이 PC에서 번역합니다. 문장 해설은 지원하지 않습니다.",
            TranslationProvider.DeepL => "번역 전용 서비스입니다. 문장 해설과 캐릭터 사전 기능은 지원하지 않습니다.",
            _ when info.SupportsStudy => "문장 해설 지원",
            _ => "번역 전용 서비스입니다. 문장 해설은 지원하지 않습니다."
        };
        configuredOllamaModel = draft.Model;
    }
    internal ModelListWindow CreateModelListWindow(Func<CancellationToken, Task<System.Collections.Generic.IReadOnlyList<ProviderModel>>>? loader = null)
    {
        var selectedProvider = draft.Provider;
        var address = endpoint.Text.Trim();
        var dialog = new ModelListWindow(selectedProvider, loader ?? (token => selectedProvider == TranslationProvider.Ollama
            ? ollamaLookup(address, token) : ProviderModelList.LoadAsync(selectedProvider, vault, token)));
        dialog.ModelSelected += id => {
            if (!canEditProfile() || selectedProvider != draft.Provider ||
                (selectedProvider == TranslationProvider.Ollama && endpoint.Text.Trim() != address)) return;
            model.Text = id; dirty = true; SetStatus("모델 ID를 입력했습니다. 설정 저장을 누르면 적용됩니다.");
            if (selectedProvider == TranslationProvider.Ollama) FlushOllamaModelChange();
        };
        return dialog;
    }
    private void LoadCredentialFields()
    {
        if (credentialProvider.SelectedItem is not TranslationProvider chosen) return;
        var info = TranslationProviderCatalog.Get(chosen);
        credentialField.ItemsSource = info.Credentials;
        credentialField.SelectedIndex = info.Credentials.Count > 0 ? 0 : -1;
        RefreshCredentialVisibility();
        RefreshCredentialMask();
    }
    private void RefreshCredentialVisibility()
    {
        bool available = canEditGlobal() && globalKeys.Visibility == Visibility.Visible
            && credentialProvider.SelectedItem is TranslationProvider selected
            && TranslationProviderCatalog.Get(selected).Credentials.Count > 0
            && (section != ProfileEditorSection.All || TranslationProviderCatalog.Get(draft.Provider).Credentials.Count > 0);
        credentialCaption.Visibility = credentialField.Visibility = available && credentialField.Items.Count > 1
            ? Visibility.Visible : Visibility.Collapsed;
        secretCaption.Visibility = secret.Visibility = keyButtons.Visibility = credentialState.Visibility =
            available ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClearStoredMaskForEditing()
    {
        if (!showingStoredKeyMask) return;
        showingStoredKeyMask = false;
        secret.Clear();
    }
    private void RefreshCredentialMask()
    {
        var stored = canEditGlobal() && secret.Visibility == Visibility.Visible && credentialProvider.SelectedItem is TranslationProvider providerId
            && credentialField.SelectedItem is TranslationCredentialField field
            && vault.ContainsCredential(providerId, field.Id);
        updatingStoredKeyMask = true;
        try {
            secret.Password = stored ? StoredKeyMask : "";
            showingStoredKeyMask = stored;
        }
        finally { updatingStoredKeyMask = false; }
        credentialState.Text = stored ? "등록됨 · 저장된 인증 정보가 있습니다." : "미등록 · API 키를 입력하고 등록하세요.";
        secret.ToolTip = stored
            ? "인증 정보가 등록되어 있습니다. 새 키를 입력하거나 붙여넣은 뒤 등록하면 변경됩니다."
            : "새 API 키를 입력하세요. Windows 계정으로 암호화하여 저장됩니다.";
    }
    private void FlushOllamaModelChange()
    {
        var id = model.Text.Trim();
        if (draft.Provider != TranslationProvider.Ollama || id == configuredOllamaModel) return;
        RememberOllamaFields(configuredOllamaModel); draft.Model = id; LoadProviderFields();
    }
    internal TranslationOptions ReadTranslationOptions()
    {
        if (!int.TryParse(timeout.Text, out var seconds)) throw new TranslationException("제한 시간을 정수로 입력하세요.");
        if (seconds is < 5 or > 300) throw new TranslationException("제한 시간은 5–300초로 입력하세요.");
        var selected = SelectedProvider;
        var info = TranslationProviderCatalog.Get(selected);
        var options = draft with { Provider = selected, Model = info.RequiresModel ? model.Text.Trim() : "",
            OllamaAddress = endpoint.Text.Trim(), TimeoutSeconds = selected == TranslationProvider.Ollama ? draft.TimeoutSeconds : seconds,
            DeepLPlan = (DeepLApiPlan)deeplPlan.SelectedItem };
        if (selected == TranslationProvider.Ollama && options.Model.Length > 0) {
            if (!int.TryParse(keepAlive.Text,out var minutes) || minutes is < 0 or > 60) throw new TranslationException("모델 메모리 유지는 0–60분으로 입력하세요.");
            options.OllamaModels = options.OllamaModels.SetItem(options.Model, new((OllamaRequestFormat)ollamaFormat.SelectedItem, seconds, minutes));
        }
        options.Validate();
        return options;
    }
    internal bool ApplyChanges(Profile profile, HotkeyOptions hotkeys)
    {
        try
        {
            RefreshAccess();
            if (!canEditProfile() || !ReferenceEquals(profile, boundProfile) || !ReferenceEquals(hotkeys, boundHotkeys))
                throw new TranslationException("프로필이 변경되었습니다. 현재 프로필의 설정창을 다시 여세요.");
            FlushOllamaModelChange();
            var options = ReadTranslationOptions();
            var triggers = new TriggerOptions { Enabled = enabled.IsChecked == true, Keys = keys.Value, MouseButton = (string)mouse.SelectedItem, GamepadButtons = pad.Value }; triggers.Validate(); DesktopInput.ParseTriggers(triggers);
            HotkeyOptions? hotkey = null;
            if (canEditGlobal()) {
                hotkey = new HotkeyOptions { ShowSettings = show.Text.Trim(), ToggleTranslation = toggle.Text.Trim(), Explain = explain.Text.Trim(), StudyScrollUp = scrollUp.Text.Trim(), StudyScrollDown = scrollDown.Text.Trim(), SaveGameQa = saveQa.Text.Trim() }; hotkey.Validate(); DesktopInput.ParseGameQaHotkey(hotkey.SaveGameQa);
                // Validate the same commands that are actually registered. The dormant QA
                // preference remains compatible, but must not reserve its hidden F8 chord.
                var activeHotkeys = DesktopInput.RegisteredHotkeys(hotkey);
                if (activeHotkeys.Select(DesktopInput.ParseHotkey).Distinct().Count() != activeHotkeys.Length) throw new TranslationException("단축키를 서로 다르게 지정하세요.");
            }
            if (!int.TryParse(delay.Text, out var delayMs) || delayMs is < 0 or > 5000) throw new TranslationException("입력 후 대기는 0–5000ms로 입력하세요.");
            if (dictionary.Text.Trim().Length > 1024) throw new TranslationException("사전 경로가 너무 깁니다.");
            if (dictionaryEnabled.IsChecked == true) CharacterDictionary.Load(dictionary.Text.Trim());
            draft = options with { };
            profile.Translation = options; profile.Triggers = triggers; profile.CharacterDictionaryPath = dictionary.Text.Trim();
            profile.CharacterDictionaryEnabled = dictionaryEnabled.IsChecked == true;
            profile.TriggerDelayMs = delayMs; profile.AutomaticWatch = watch.IsChecked == true;
            if (hotkey is not null) { hotkeys.ShowSettings = hotkey.ShowSettings; hotkeys.ToggleTranslation = hotkey.ToggleTranslation; hotkeys.Explain = hotkey.Explain; hotkeys.StudyScrollUp = hotkey.StudyScrollUp; hotkeys.StudyScrollDown = hotkey.StudyScrollDown; hotkeys.SaveGameQa = hotkey.SaveGameQa; }
            dirty = false;
            SetStatus("");
            return true;
        }
        catch (Exception ex) when (ex is TranslationException or InvalidDataException or IOException or UnauthorizedAccessException) { SetStatus(ex is IOException or UnauthorizedAccessException ? "사전 파일을 읽을 수 없습니다." : ex.Message); return false; }
    }
}

internal sealed class TranslationSettingsWindow : Window
{
    private readonly TranslationSettingsEditor editor;
    internal TranslationSettingsWindow(Profile profile, HotkeyOptions hotkeys, ApiKeyStore vault, Func<bool>? canEditGlobal = null, Func<bool>? canEditProfile = null)
    {
        Title = "번역 · 입력 설정";
        Width = 650; Height = 740; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "적용", Height = 36 };
        var cancel = new Button { Content = "닫기", Height = 36, IsCancel = true };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        editor = new TranslationSettingsEditor(profile, hotkeys, vault, canEditGlobal, canEditProfile);
        Closed += (_, _) => editor.Dispose();
        root.Children.Add(editor);
        Content = root;
        apply.Click += (_, _) => { if (editor.ApplyChanges(profile, hotkeys)) DialogResult = true; };
    }
    internal bool ApplyChanges(Profile profile, HotkeyOptions hotkeys) => editor.ApplyChanges(profile, hotkeys);
    internal void RefreshAccess() => editor.RefreshAccess();
    internal TranslationSettingsEditor Editor => editor;
}
