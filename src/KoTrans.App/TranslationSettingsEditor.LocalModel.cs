namespace KoTrans.App;

internal sealed partial class TranslationSettingsEditor
{
    private readonly Border localModelCard = new() { Name = "LocalModelCard", CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 14, 0, 12), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
    private readonly StackPanel localModelPanel = new() { Name = "LocalModelPanel", Visibility = Visibility.Collapsed };
    private readonly Button localModelButton = new() { Name = "LocalModelAction", Content = "설치", MinWidth = 86, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0) };
    private readonly TextBlock localModelState = new() { Name = "LocalModelState", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly ComboBox localModelChoice = new() { Name = "LocalModelChoice", ItemsSource = LocalTranslationModels.All, DisplayMemberPath = "DisplayName", SelectedValuePath = "Id", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock localModelDescription = new() { Name = "LocalModelDescription", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private Func<LocalTranslationModel, CancellationToken, Task<bool>>? localModelQuery;
    private Func<bool, LocalTranslationModel, Task<bool>>? localModelManage;
    private Action? localModelChanged;
    private Action? cancelLocalModel;
    private CancellationTokenSource? localStateCancellation;
    private bool localModelInstalled, localModelKnown, localModelBusy, localModelDisposed;
    internal Task LocalModelActionTask { get; private set; } = Task.CompletedTask;
    private void BuildLocalModelControls(bool darkTheme)
    {
        localModelPanel.Children.Add(new TextBlock { Text = "로컬 AI 모델", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        localModelChoice.SelectedValue = draft.LocalModel;
        localModelPanel.Children.Add(localModelChoice); localModelPanel.Children.Add(localModelDescription);
        localModelPanel.Children.Add(new TextBlock { Text = "모델 관리", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        localModelPanel.Children.Add(localModelState); localModelPanel.Children.Add(localModelButton);
        localModelCard.Background = new SolidColorBrush(darkTheme ? Color.FromRgb(20, 35, 51) : Color.FromRgb(243, 246, 249));
        localModelCard.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromRgb(69, 96, 122) : Color.FromRgb(198, 211, 222));
        localModelCard.Child = localModelPanel;
        translationPanel.Children.Add(localModelCard);
        localModelButton.Click += async (_, _) => { LocalModelActionTask = ManageLocalModelFromEditorAsync(); await LocalModelActionTask; };
        Loaded += (_, _) => RefreshLocalModelState();
        localModelChoice.SelectionChanged += (_, _) => {
            if (localModelChoice.SelectedValue is not LocalTranslationModel selected || selected == draft.LocalModel) return;
            if (localModelBusy || !canEditProfile()) { localModelChoice.SelectedValue = draft.LocalModel; return; }
            draft.LocalModel = selected; dirty = true;
            localModelChanged?.Invoke(); RefreshLocalModelState();
            SetStatus("로컬 AI 모델을 변경했습니다. 설정을 저장한 뒤 시작하면 적용됩니다.");
            ServiceSelectionChanged?.Invoke(this, EventArgs.Empty);
        };
    }
    internal void ConfigureLocalModelManagement(Func<LocalTranslationModel, CancellationToken, Task<bool>> query, Func<bool, LocalTranslationModel, Task<bool>> manage, Action? cancel = null, Action? changed = null)
    { localModelQuery = query; localModelManage = manage; cancelLocalModel = cancel; localModelChanged = changed; RefreshLocalModelState(); }
    private void RefreshLocalModelAccess()
    {
        localModelButton.IsEnabled = canEditProfile() && localModelKnown && !localModelBusy && localModelManage is not null;
        provider.IsEnabled = !localModelBusy;
        localModelChoice.IsEnabled = canEditProfile() && !localModelBusy;
    }
    internal async void RefreshLocalModelState()
    {
        if (localModelDisposed) return;
        bool local = draft.Provider == TranslationProvider.HyMtLocal;
        var info = LocalTranslationModels.Get(draft.LocalModel);
        localModelDescription.Text = info.Description;
        localModelCard.Visibility = localModelPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        if (localModelBusy) return;
        localStateCancellation?.Cancel(); localStateCancellation?.Dispose();
        localStateCancellation = new(); var token = localStateCancellation.Token;
        localModelKnown = false; RefreshLocalModelAccess();
        if (!local || localModelQuery is null) return;
        localModelState.Text = "설치 상태 확인 중…";
        try
        {
            bool installed = await localModelQuery(draft.LocalModel, token);
            if (token.IsCancellationRequested || localModelDisposed) return;
            localModelInstalled = installed; localModelKnown = true;
            localModelButton.Content = installed ? "삭제" : "설치";
            localModelState.Text = installed ? "설치됨 · 모든 프로필에서 사용 가능" : info.DownloadSize + " · 설치 후 인터넷 없이 사용 가능";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KoTrans.Core.TranslationException)
        { if (!token.IsCancellationRequested && !localModelDisposed) localModelState.Text = "설치 상태를 확인하지 못했습니다. 번역 제공자를 다시 선택해 주세요."; }
        finally { if (!token.IsCancellationRequested && !localModelDisposed) RefreshLocalModelAccess(); }
    }
    private async Task ManageLocalModelFromEditorAsync()
    {
        if (localModelBusy || !localModelButton.IsEnabled || localModelManage is null || draft.Provider != TranslationProvider.HyMtLocal) return;
        localStateCancellation?.Cancel(); localModelBusy = true; RefreshLocalModelAccess();
        string? error = null;
        try { await localModelManage(localModelInstalled, draft.LocalModel); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KoTrans.Core.TranslationException)
        { error = "로컬 AI 데이터를 관리하지 못했습니다. 사용 중인 번역을 중지하고 다시 시도해 주세요."; }
        finally
        {
            localModelBusy = false;
            if (!localModelDisposed) { RefreshLocalModelState(); if (error is not null) SetStatus(error); }
        }
    }
    private void DisposeLocalModel()
    { localModelDisposed = true; if (localModelBusy) cancelLocalModel?.Invoke(); localStateCancellation?.Cancel(); localStateCancellation?.Dispose(); }
}
