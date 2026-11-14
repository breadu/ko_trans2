using System.Windows.Interop;

namespace KoTrans.App;

public partial class MainWindow
{
    private IApplicationTray? tray;
    private bool exitRequested;
    internal bool IsInTray { get; private set; }
    internal bool HasSettingsSaveFailure => saveFailureFeedback is not null;

    // The F1 shortcut restores the main window, not the profile settings page.
    // Fallback captions must agree with HideSettings when the tray is unavailable.
    internal static string HideSettingsCaption(bool trayAvailable, bool canRestoreByHotkey, string hotkey)
        => trayAvailable
            ? canRestoreByHotkey ? $"트레이로 숨기기 ({hotkey}로 복원)" : "트레이로 숨기기 (트레이 아이콘으로 복원)"
            : canRestoreByHotkey ? $"메인 창 숨기기 ({hotkey}로 복원)" : "작업 표시줄로 최소화";

    private void RefreshHideSettingsButtonCaption()
    {
        if (HideSettingsButton is null) return;
        HideSettingsButton.Content = HideSettingsCaption(tray?.IsAvailable == true, input?.CanShowSettings == true, settings.Hotkeys.ShowSettings);
    }

    private void InitializeTray()
    {
        if (tray is not null) return;
        try { AttachTray(new ApplicationTray()); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or ExternalException)
        { model.Status = "알림 아이콘을 만들지 못했습니다. 창 숨기기는 최소화로 처리합니다. X로 프로그램을 종료할 수 있습니다."; }
    }
    internal void AttachTray(IApplicationTray value)
    {
        if (tray is not null) throw new InvalidOperationException("알림 아이콘은 이미 연결되어 있습니다.");
        tray = value; tray.OpenRequested += TrayOpenRequested; tray.ExitRequested += TrayExitRequested;
        RefreshTrayTranslationCommand();
        RefreshHideSettingsButtonCaption();
    }
    private void TrayOpenRequested() => Dispatcher.BeginInvoke(new Action(RestoreMainWindow));
    private void TrayExitRequested() => Dispatcher.BeginInvoke(new Action(RequestApplicationExit));
    private void RefreshTrayTranslationCommand() => tray?.SetTranslationCommand(
        TranslationButtonCaption(automatic && pipeline?.IsRunning == true), ready && !closing && !startingTranslation, TrayTranslationRequested);
    private void TrayTranslationRequested() => Dispatcher.BeginInvoke(new Action(async () => {
        if (!ready || closing || startingTranslation) return;
        await ToggleLauncherTranslationAsync();
    }));
    internal void RestoreMainWindow()
    {
        if (closing || closeApproved) return;
        ShowForExitOrRestore();
    }
    private void ShowForExitOrRestore()
    {
        IsInTray = false;
        ResumeFeedback();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // Real tray/hotkeys are initialized after render, so production always has
        // a handle. Do not materialize a never-rendered window during cleanup/tests.
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        Show(); Activate();
    }
    internal bool HideToTray()
    {
        if (closing || closeApproved) return false;
        if (tray?.IsAvailable != true) {
            WindowState = WindowState.Minimized;
            model.Status = "알림 아이콘을 사용할 수 없어 작업 표시줄로 최소화했습니다.";
            return false;
        }
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero) RememberWindows();
        SuspendFeedback(); Hide(); IsInTray = true; return true;
    }
    internal async Task<bool> StartWithMainHiddenAsync(Func<Task<bool>> start)
    {
        if (startingTranslation || closing || closeApproved || !ready) return false;
        if (!IsInTray) HideToTray();
        bool started = false;
        try
        {
            started = await start();
            return started;
        }
        finally
        {
            // A failed start or settings save needs a visible main window for its feedback.
            if ((!started || !automatic || pipeline?.IsRunning != true || HasSettingsSaveFailure)
                && (IsInTray || WindowState == WindowState.Minimized)) RestoreMainWindow();
        }
    }
    internal void RequestApplicationExit()
    {
        if (closing || closeApproved) return;
        exitRequested = true; Close();
    }
    private void CancelApplicationExit()
    {
        closing = exitRequested = false; RestoreMainWindow();
    }
    internal void DisposeTray()
    {
        var owned = tray; tray = null; IsInTray = false;
        if (owned is null) return;
        owned.SetTranslationCommand("", false, null);
        owned.OpenRequested -= TrayOpenRequested; owned.ExitRequested -= TrayExitRequested; owned.Dispose();
    }
}
