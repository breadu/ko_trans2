using System.Windows.Interop;
using System.Collections.Generic;
using KoTrans.Infrastructure;
using Forms = System.Windows.Forms;

namespace KoTrans.App;

public partial class MainWindow
{
    private Func<IReadOnlyList<PixelRect>>? captureMapDisplays;
    // UI order is independent of the persisted CaptureMode enum values.
    // Never renumber the enum: existing settings.json files use those values.
    private static int CaptureModeToSelectionIndex(CaptureMode mode) => mode switch
    {
        CaptureMode.Window => 0,
        CaptureMode.Region => 1,
        CaptureMode.Clipboard => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    private static CaptureMode CaptureModeFromSelectionIndex(int index) => index switch
    {
        0 => CaptureMode.Window,
        1 => CaptureMode.Region,
        2 => CaptureMode.Clipboard,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    private void InvalidateCaptureInput(string reason)
    {
        InvalidateOcr(reason);
        captureCancellation?.Cancel();
        lastFrame = null;
        model.Preview = null;
    }
    internal void SetCaptureMapDisplays(Func<IReadOnlyList<PixelRect>> displays) { captureMapDisplays = displays; RefreshCaptureMap(); }
    private void RefreshCaptureModeControls()
    {
        bool window = Current.CaptureMode == CaptureMode.Window;
        CaptureModeHelp.Text = Current.CaptureMode switch
        {
            CaptureMode.Window => "게임 창 (추천): 창을 옮겨도 인식 영역이 유지됩니다. [대상 창 선택]에서 게임을 연결하세요. 연결이 어려우면 '화면 영역'을 사용할 수 있습니다.",
            CaptureMode.Region => "화면 영역: 지정한 화면 좌표를 사용합니다. 게임 창을 옮기면 영역을 다시 지정해야 할 수 있습니다.",
            CaptureMode.Clipboard => "클립보드: 다른 프로그램에서 복사한 텍스트를 읽습니다. 화면 캡처는 사용하지 않습니다.",
            _ => ""
        };
        PickWindowButton.IsEnabled = PickWindowDirectButton.IsEnabled = window;
        SelectAreaButton.IsEnabled = Current.CaptureMode != CaptureMode.Clipboard;
        overlay?.SetCaptureAreaAvailable(SelectAreaButton.IsEnabled);
        CopyOcrResultToClipboard.IsEnabled = Current.CaptureMode != CaptureMode.Clipboard;
        OriginalCaptureWhenScaled.IsEnabled=window;
        RefreshMarkerLearningControls();
        if (!window) TargetLabel.Text = Current.CaptureMode == CaptureMode.Clipboard
            ? "텍스트 후킹 프로그램 등이 클립보드에 복사한 게임 텍스트를 읽습니다. 화면 캡처나 대상 창은 사용하지 않습니다."
            : "전체 바탕화면에서 지정한 영역만 캡처합니다. 대상 창 선택과 관계없이 저장된 화면 좌표를 사용합니다.";
    }
    private void OriginalCaptureWhenScaledChanged(object sender,RoutedEventArgs e)
    {
        if(!ready || changing || !OriginalCaptureWhenScaled.IsEnabled)return;
        bool enabled=OriginalCaptureWhenScaled.IsChecked==true;
        if(Current.OriginalCaptureWhenScaled==enabled)return;
        InvalidateCaptureInput("확대 화면 캡처 설정이 변경되어 이전 문장 해설을 비웠습니다.");
        Current.OriginalCaptureWhenScaled=enabled;
        model.Status="확대 화면 캡처 설정을 변경했습니다. 설정을 저장한 뒤 다시 시작하세요.";
        ShowFeedback(FeedbackKind.Information,"확대 화면 캡처 설정을 변경했어요",
            $"{(Current.ShowTranslation ? "번역" : "원문 인식")}을 중지했습니다. 설정을 저장한 뒤 다시 시작하세요.");
    }
    internal bool CaptureOriginalWindow(WindowTarget chosen,WindowScaleTransform? scale)
        =>scale is not null ? Current.OriginalCaptureWhenScaled : useOriginalWindowCapture(chosen);
    private void OcrClipboardChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || changing) return;
        Current.CopyOcrToClipboard = CopyOcrResultToClipboard.IsChecked == true;
    }
    internal async Task CopyAcceptedOcrAsync(TranslationPipeline active, Guid profileId, long version, string text, CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() => {
            token.ThrowIfCancellationRequested();
            if (closing || pipeline != active || active.Version != version || Current.Id != profileId
                || !Current.CopyOcrToClipboard || Current.CaptureMode == CaptureMode.Clipboard) return;
            try { copyOcrText(text); }
            catch (ExternalException) {
                ShowFeedback(FeedbackKind.Error, "OCR 결과를 클립보드로 복사하지 못했어요",
                    "다른 프로그램이 클립보드를 사용 중일 수 있습니다. 원문 인식과 번역은 계속합니다.");
            }
        });
    }
    private void RefreshCaptureMap()
    {
        if (AreaMap is null || !ready) return;
        PixelRect? selected = Current.CaptureAreaConfigured ? Current.CaptureArea : null;
        IReadOnlyList<PixelRect> screens = [];
        if (Current.CaptureMode == CaptureMode.Window && target is not null) {
            try {
                var bounds = getWindowBounds(target);
                screens = [new(0, 0, bounds.Width, bounds.Height)];
                selected = Current.WindowCaptureRegion;
                AreaMap.ToolTip = "게임 창 크기 기준 · 파란색: 인식 영역";
            } catch (Exception ex) when (ex is InvalidDataException or CaptureUnavailableException) { }
        } else if (Current.CaptureMode == CaptureMode.Region) {
            // Read monitor bounds only after native app initialization. Background tests inject geometry.
            screens = captureMapDisplays?.Invoke() ?? (new WindowInteropHelper(this).Handle == IntPtr.Zero ? []
                : Forms.Screen.AllScreens.Select(s => new PixelRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height)).ToArray());
            AreaMap.ToolTip = "전체 모니터 배치 기준 · 파란색: 인식 영역";
        }
        AreaMap.SetGeometry(screens, selected);
        // CaptureAreaMap deliberately collapses when geometry is unavailable.
        // Keep its parent frame fixed and show a contextual placeholder instead.
        bool hasPreview = AreaMap.Visibility == Visibility.Visible;
        CaptureMapEmptyState.Visibility = hasPreview ? Visibility.Collapsed : Visibility.Visible;
        CaptureMapEmptyReason.Text = Current.CaptureMode switch
        {
            CaptureMode.Clipboard => "클립보드 입력",
            CaptureMode.Region when !Current.CaptureAreaConfigured => "영역을 지정하세요",
            CaptureMode.Region => "모니터 정보 확인 대기",
            CaptureMode.Window when Current.GameWindow is null => "게임 창을 선택하세요",
            CaptureMode.Window when target is null => "게임 창 연결 대기",
            CaptureMode.Window when Current.WindowCaptureRegion is null => "영역 미지정 · 창 전체",
            CaptureMode.Window => "게임 창 정보 확인 대기",
            _ => "영역을 확인하세요"
        };
        CaptureMapFrame.ToolTip = hasPreview ? AreaMap.ToolTip
            : "미리보기 없음 · " + CaptureMapEmptyReason.Text;
    }
}
