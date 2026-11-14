using KoTrans.Infrastructure;
namespace KoTrans.App;

public partial class MainWindow
{
    // Internal test windows use injected temporary data; the public app constructor enables the real model store.
    private readonly bool useLocalModelData;
    private CancellationTokenSource? localStartCancellation;
    private Task<bool>? localPreparationTask;
    internal Func<CancellationToken, Task<bool>>? PrepareLocalOverride { get; set; }
    internal Func<Task<bool>>? StartPreparedLocalOverride { get; set; }
    internal LocalTranslationPreparation? LocalPreparationOverride { get; set; }
    internal Func<LocalTranslationModel, LocalTranslationPreparation>? LocalPreparationFactoryOverride { get; set; }
    internal Func<Window, string, string, bool, bool>? ConfirmLocalOverride { get; set; }
    internal Action<LocalPreparationProgress>? LocalProgressOverride { get; set; }
    private Task localModelRelease = Task.CompletedTask;
    private bool CanManageLocalModel => useLocalModelData || LocalPreparationOverride is not null || LocalPreparationFactoryOverride is not null;
    private LocalTranslationPreparation PreparationFor(LocalTranslationModel selection) => LocalPreparationFactoryOverride?.Invoke(selection)
        ?? LocalPreparationOverride ?? LocalTranslationEnvironment.PreparationFor(selection);
    private void ReleaseLocalModelForChange()
    {
        if (!CanManageLocalModel) return;
        var previous = localModelRelease; var preparation = PreparationFor(Current.Translation.LocalModel);
        async Task Release() { await previous; await preparation.ReleaseAsync(); }
        localModelRelease = Release(); retiring.Add(localModelRelease);
    }
    private bool ConfirmLocal(Window owner, string title, string text, bool deleting, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        if (ConfirmLocalOverride is not null) return ConfirmLocalOverride(owner, title, text, deleting);
        var dialog = new LocalConfirmationWindow(title, text, deleting) { Owner = owner };
        if (owner.IsVisible && owner.WindowState == WindowState.Normal) dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        using var cancellation = token.Register(() => Dispatcher.BeginInvoke(() => dialog.Close()));
        return dialog.ShowDialog() == true && !token.IsCancellationRequested;
    }
    private void ConfigureLocalModelEditor(TranslationSettingsEditor editor, Guid profileId, Window owner) =>
        editor.ConfigureLocalModelManagement((selection, token) => CanManageLocalModel ? PreparationFor(selection).IsInstalledAsync(token) : Task.FromResult(false),
            (delete, selection) => ManageLocalModelAsync(delete, profileId, owner, selection), () => { if (Current.Id == profileId) localStartCancellation?.Cancel(); },
            () => {
                if (Current.Id != profileId || closing) return;
                if (Current.ShowTranslation && Current.Translation.Provider == TranslationProvider.HyMtLocal) StopTranslation();
                ReleaseLocalModelForChange();
                model.Status = "로컬 AI 모델을 변경했습니다. 설정을 저장한 뒤 번역을 다시 시작하세요.";
            });
    internal async Task<bool> ManageLocalModelAsync(bool delete, Guid profileId, Window owner, LocalTranslationModel? selected = null)
    {
        if (!CanManageLocalModel || startingTranslation || closing || Current.Id != profileId) return false;
        var selection = selected ?? Current.Translation.LocalModel;
        var info = LocalTranslationModels.Get(selection);
        var preparation = PreparationFor(selection);
        startingTranslation = true;
        using var cancellation = new CancellationTokenSource(); localStartCancellation = cancellation;
        RefreshOverlayControls();
        try
        {
            async Task<bool> Manage()
            {
                try
                {
                    await localModelRelease.WaitAsync(cancellation.Token);
                    if (!delete) return await PrepareLocalTranslationAsync(profileId, cancellation.Token, owner, forSettings: true, selected: selection);
                    if (!ConfirmLocal(owner, "로컬 AI 데이터를 삭제하시겠습니까?",
                        info.DisplayName + "\n\n선택한 모델과 해당 다운로드 데이터만 삭제합니다. 다른 모델은 보존합니다.\n모든 프로필이 이 설치를 공유하므로 다시 사용하려면 모델을 다시 다운로드해야 합니다.", true, cancellation.Token)) return false;
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (closing || Current.Id != profileId) return false;
                    if (Current.ShowTranslation && Current.Translation.Provider == TranslationProvider.HyMtLocal && Current.Translation.LocalModel == selection) StopTranslation(cancelLocalPreparation: false);
                    await preparation.DeleteAsync(cancellation.Token);
                    contexts.ResetLocalModel(selection);
                    ShowCompletionStatus("선택한 로컬 AI 모델 데이터를 삭제했습니다.");
                    return true;
                }
                catch (OperationCanceledException) { return false; }
            }
            localPreparationTask = Manage();
            return await localPreparationTask;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            if (ReferenceEquals(localStartCancellation, cancellation)) localStartCancellation = null;
            localPreparationTask = null; startingTranslation = false;
            embeddedProfileEditor?.RefreshLocalModelState(); FillTranslation(); RefreshOverlayControls();
        }
    }
    private async Task<bool> PrepareLocalTranslationAsync(Guid profileId, CancellationToken token, Window? owner = null, bool forSettings = false, LocalTranslationModel? selected = null)
    {
        if (PrepareLocalOverride is not null) return await PrepareLocalOverride(token);
        var selection = selected ?? Current.Translation.LocalModel;
        var info = LocalTranslationModels.Get(selection);
        var preparation = PreparationFor(selection);
        await localModelRelease.WaitAsync(token);
        owner ??= this;
        bool consentGiven = false, finished = false;
        LocalPreparationWindow? dialog = null;
        bool CurrentRequest() => !finished && !closing && Current.Id == profileId && !token.IsCancellationRequested &&
            (forSettings || Current.ShowTranslation && Current.Translation.Provider == TranslationProvider.HyMtLocal && Current.Translation.LocalModel == selection);
        LocalPreparationWindow CreateProgress() => new(() => localStartCancellation?.Cancel()) { Owner = owner,
            WindowStartupLocation = owner.IsVisible && owner.WindowState == WindowState.Normal ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen };
        var progress = new Progress<LocalPreparationProgress>(value =>
        {
            if (!CurrentRequest()) return;
            if (LocalProgressOverride is not null) { LocalProgressOverride(value); return; }
            dialog ??= CreateProgress();
            if (!dialog.IsVisible) dialog.Show();
            dialog.Report(value); model.Status = value.Stage;
        });
        try
        {
            while (CurrentRequest())
            {
                try
                {
                    bool prepared = await preparation.EnsureAsync(async () => await Dispatcher.InvokeAsync(() =>
                    {
                        if (!CurrentRequest()) return false;
                        if (!consentGiven) consentGiven = ConfirmLocal(owner, "로컬 AI 번역을 설치하시겠습니까?",
                            info.DisplayName + "\n\n" + info.Description + "\n\n선택한 모델(" + info.DownloadSize + ")을 다운로드하고 필요한 환경을 자동으로 준비합니다.\n설치 후에는 인터넷 연결 없이 번역할 수 있습니다.", false, token);
                        return consentGiven;
                    }), progress, token);
                    return prepared && CurrentRequest();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or TranslationException)
                {
                    LocalDiagnosticMessage(ex);
                    if (!CurrentRequest()) return false;
                    dialog ??= CreateProgress();
                    if (!dialog.IsVisible) dialog.Show();
                    var message = ex is TranslationException ? ex.Message : ex is UnauthorizedAccessException
                        ? "로컬 번역 폴더에 저장할 수 없습니다. 폴더 권한을 확인한 뒤 다시 시도해 주세요."
                        : "로컬 AI 번역을 준비하지 못했습니다. 인터넷 연결과 저장 공간을 확인한 뒤 다시 시도해 주세요.";
                    if (!await dialog.RetryAsync(message).WaitAsync(token)) return false;
                }
            }
            return false;
        }
        catch (OperationCanceledException) { return false; }
        finally { finished = true; dialog?.Finish(); embeddedProfileEditor?.RefreshLocalModelState(); }
    }
    private static void LocalDiagnosticMessage(Exception error) => LocalDiagnostic.Write($"Local preparation: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}" +
        (error is System.Net.Http.HttpRequestException http ? "; HTTP=" + http.StatusCode : ""));
}
