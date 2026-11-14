using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace KoTrans.App;

public partial class MainWindow
{
    private readonly OperationToast operationToast = new();
    private readonly DispatcherTimer feedbackTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly long feedbackEpoch = Stopwatch.GetTimestamp();
    private Func<TimeSpan> feedbackClock = null!;
    private TimeSpan? toastExpires;
    private (long Revision, Guid Profile, TimeSpan Until)? completionStatus;
    private bool toastFading;
    internal OperationToast FeedbackToast => operationToast;
    internal int FeedbackCount { get; private set; }
    internal bool IsFeedbackTimerRunning => feedbackTimer.IsEnabled;
    private long saveRequestSerial, saveSettledSerial;
    private bool feedbackOperationBusy;
    private int? saveFailureFeedback;
    private bool formValidationFailed;
    private string? saveFailureStatus;
    private (Guid Profile, string Engine, string Preset)? unsavedOcrNotice;
    private TimeSpan? saveButtonStarted, saveButtonUntil;
    internal bool IsUserSavePending => feedbackOperationBusy;
    private sealed class SaveFeedback
    {
        internal string? Snapshot;
        internal long Serial;
        internal Guid ProfileId;
        internal string ProfileName = "";
        internal string? SuccessDetail;
        internal bool FailureReported;
    }

    private void InitializeFeedback(Func<TimeSpan>? clock)
    {
        feedbackClock = clock ?? (() => Stopwatch.GetElapsedTime(feedbackEpoch));
        feedbackTimer.Tick += (_, _) => AdvanceFeedback();
        Closed += (_, _) => ShutdownFeedback();
        PlaceFeedback();
    }
    private void PlaceFeedback()
    {
        var host = profileSettingsOpen ? SettingsFeedbackHost : LauncherFeedbackHost;
        if (!ReferenceEquals(host.Content, operationToast)) {
            LauncherFeedbackHost.Content = null; SettingsFeedbackHost.Content = null;
            host.Content = operationToast;
        }
    }
    internal void ShowFeedback(FeedbackKind kind, string message, string detail = "", bool replaceError = false, TimeSpan? duration = null)
    {
        if (closing || closeApproved) return;
        if (!replaceError && operationToast.Visibility == Visibility.Visible && operationToast.Kind == FeedbackKind.Error && kind != FeedbackKind.Error) return;
        FeedbackCount++; toastFading = false;
        unsavedOcrNotice = null;
        operationToast.Present(kind, message, detail); PlaceFeedback();
        toastExpires = feedbackClock() + (duration ?? TimeSpan.FromMilliseconds(kind == FeedbackKind.Error ? 3000 : 2400));
        if (!IsInTray) feedbackTimer.Start();
    }
    internal void AdvanceFeedback()
    {
        var now = feedbackClock();
        if (completionStatus is { } completed && now >= completed.Until) {
            completionStatus = null;
            if (!closing && !closeApproved && completed.Profile == Current.Id && completed.Revision == model.StatusRevision)
                model.Status = ReadyStatus();
        }
        if (saveButtonStarted is { } started && now - started >= TimeSpan.FromMilliseconds(120)
            && SaveSpinner.Visibility != Visibility.Visible) {
            SaveCaption.Text = "저장 중…"; SaveSpinner.Visibility = Visibility.Visible;
            ((RotateTransform)SaveSpinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        if (saveButtonUntil is { } until && now >= until) ResetSaveButton();
        if (toastExpires is { } expires) {
            if (now >= expires + TimeSpan.FromMilliseconds(180)) {
                operationToast.Dismiss(); toastExpires = null; toastFading = false; PlaceFeedback();
            } else if (now >= expires && !toastFading) { operationToast.FadeOut(); toastFading = true; }
        }
        if (toastExpires is null && saveButtonStarted is null && saveButtonUntil is null && completionStatus is null) feedbackTimer.Stop();
    }
    internal void ShowCompletionStatus(string message)
    {
        // Active pipeline status (including unresolved errors) has priority over a side action.
        if (closing || closeApproved || pipeline?.IsRunning == true || startingTranslation) return;
        model.Status = message;
        completionStatus = (model.StatusRevision, Current.Id, feedbackClock() + TimeSpan.FromMilliseconds(2400));
        if (!IsInTray) feedbackTimer.Start();
    }
    private void SuspendFeedback()
    {
        feedbackTimer.Stop();
        ResetSaveButton();
        if (operationToast.Kind != FeedbackKind.Error) { operationToast.Dismiss(); toastExpires = null; PlaceFeedback(); }
    }
    private void ResumeFeedback()
    {
        AdvanceFeedback();
        if (toastExpires is not null || saveButtonStarted is not null || saveButtonUntil is not null || completionStatus is not null) feedbackTimer.Start();
    }
    private void ShutdownFeedback()
    {
        feedbackTimer.Stop(); toastExpires = null; completionStatus = null; operationToast.Dismiss(); ResetSaveButton();
    }

    internal Task<bool> SaveWithFeedbackAsync() => PerformProfileFeedbackAsync(
        !settings.CanEditGlobal && ProfileName.Text.Trim() != Current.Name ? "프로필 이름을 변경했어요" : "설정이 저장되었어요",
        SaveCurrentProfileCoreAsync, true);

    private async Task<bool> PerformProfileFeedbackAsync(string success, Func<SaveFeedback, Task<bool>> action, bool button = false)
    {
        if (feedbackOperationBusy || closing || closeApproved) return false;
        feedbackOperationBusy = true; SuspendFeedback(); SetProfileActionAvailability(false);
        if (!IsInTray && toastExpires is not null) feedbackTimer.Start();
        var feedback = new SaveFeedback();
        if (button) { ResetSaveButton(); saveButtonStarted = feedbackClock(); feedbackTimer.Start(); }
        try {
            bool saved = await action(feedback);
            if (closing || closeApproved) return saved;
            if (!saved) {
                if (!feedback.FailureReported) ReportSaveFailure(false);
                ResetSaveButton(); return false;
            }
            if (feedback.Snapshot is null || feedback.Serial != saveRequestSerial) { ResetSaveButton(); return true; }
            bool changed = feedback.ProfileId == Current.Id && (embeddedProfileEditor?.HasChanges == true
                || ProfileName.Text.Trim() != Current.Name || feedback.Snapshot != System.Text.Json.JsonSerializer.Serialize(settings));
            bool pendingKey = button && embeddedProfileEditor?.HasUnregisteredCredential == true;
            string detail = changed ? "요청 당시 설정 저장 완료 · 추가 변경은 저장 필요" : feedback.SuccessDetail ?? feedback.ProfileName;
            if (pendingKey) detail += " · 입력한 API 키는 ‘키 등록/변경’을 눌러 등록하세요.";
            if (hotkeyWarning.Length > 0) detail += " · " + hotkeyWarning;
            if (!IsInTray) ShowFeedback(FeedbackKind.Success, pendingKey ? "일반 설정을 저장했어요" : success, detail, replaceError: true);
            if (button && !changed && !pendingKey && !IsInTray && feedback.ProfileId == Current.Id) {
                ResetSaveButton(); SaveCheck.Visibility = Visibility.Visible; SaveCaption.Text = "저장 완료";
                SaveCheck.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                saveButtonUntil = feedbackClock() + TimeSpan.FromMilliseconds(1300); feedbackTimer.Start();
            } else ResetSaveButton();
            return true;
        }
        finally {
            feedbackOperationBusy = false;
            SetProfileActionAvailability(!closing && !closeApproved);
        }
    }
    private void ReportSaveFailure(bool persistence, string? cause = null)
    {
        if (!persistence) formValidationFailed = true;
        ShowFeedback(FeedbackKind.Error, "설정을 저장하지 못했어요",
            persistence ? "저장 위치와 접근 권한을 확인하고 다시 시도해 주세요." : "입력값을 확인하고 다시 저장해 주세요.", replaceError: true);
        saveFailureFeedback = FeedbackCount; saveFailureStatus = model.Status;
        LastSaveFailure.Text = persistence
            ? "설정 저장 실패 · 저장 위치와 권한을 확인하세요."
            : "설정 저장 실패 · 입력값을 확인한 뒤 다시 저장하세요.";
        LastSaveFailure.ToolTip = LastSaveFailure.Text + (cause is null ? "" : "\n원인: " + cause);
        LastSaveFailure.Visibility = Visibility.Visible;
        StatusMessageText.Visibility = Visibility.Collapsed;
    }
    internal static string SaveFailureDetail(Exception error) => error switch {
        UnauthorizedAccessException => "설정 파일에 쓸 수 없습니다. 파일이 읽기 전용인지, 저장 폴더에 쓰기 권한이 있는지 확인하세요.",
        DirectoryNotFoundException => "설정 폴더를 찾을 수 없습니다. 저장 드라이브가 연결되어 있는지 확인하세요.",
        IOException io when (io.HResult & 0xffff) is 32 or 33 => "다른 프로그램이 설정 파일을 사용 중입니다. 파일을 사용하는 프로그램을 닫고 다시 저장하세요.",
        IOException io when (io.HResult & 0xffff) is 39 or 112 => "저장 드라이브의 여유 공간이 부족합니다. 공간을 확보한 뒤 다시 저장하세요.",
        IOException io when (io.HResult & 0xffff) is 5 or 19 => "저장 위치에 쓸 수 없습니다. 읽기 전용 상태와 폴더 쓰기 권한을 확인하세요.",
        InvalidDataException => "설정 값이 올바르지 않습니다. 프로필 설정의 입력값을 확인한 뒤 다시 저장하세요.",
        IOException => "설정 파일을 읽거나 쓸 수 없습니다. 드라이브 연결, 여유 공간 및 파일 사용 여부를 확인한 뒤 다시 저장하세요.",
        _ => "현재 설정을 저장할 수 없습니다. 진행 중인 설정 변경이 끝난 뒤 다시 저장하세요."
    };
    private void ClearSavedFailure()
    {
        if (operationToast.Kind == FeedbackKind.Error && saveFailureFeedback == FeedbackCount) { operationToast.Dismiss(); toastExpires = null; PlaceFeedback(); }
        if (model.Status == saveFailureStatus) model.Status = ReadyStatus();
        saveFailureFeedback = null; saveFailureStatus = null;
        formValidationFailed = false;
        LastSaveFailure.Visibility = Visibility.Collapsed;
        StatusMessageText.Visibility = Visibility.Visible;
    }
    private void ResetSaveButton()
    {
        saveButtonStarted = saveButtonUntil = null;
        ((RotateTransform)SaveSpinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
        SaveCheck.BeginAnimation(OpacityProperty, null); SaveSpinner.Visibility = SaveCheck.Visibility = Visibility.Collapsed;
        SaveCaption.Text = "설정 저장";
    }
    private void SetProfileActionAvailability(bool enabled)
    {
        SaveSettingsButton.IsEnabled = Profiles.IsEnabled = CreateProfileButton.IsEnabled = enabled;
        RenameProfileButton.IsEnabled = DeleteProfileButton.IsEnabled = enabled && !settings.CanEditGlobal;
    }
    private void ClearSavedOcrInformation(Guid profile, string engine, string preset)
    {
        if (unsavedOcrNotice == (profile, engine, preset) && operationToast.Kind == FeedbackKind.Information) {
            unsavedOcrNotice = null; toastExpires = null; operationToast.Dismiss(); PlaceFeedback();
        }
    }
}
