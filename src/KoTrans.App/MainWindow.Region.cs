using KoTrans.Infrastructure;

namespace KoTrans.App;
public partial class MainWindow
{
    private bool selectingArea;
    internal sealed record AreaEditContext(WindowTarget Target,PixelRect Window,WindowScaleTransform? Scale,PixelRect? SavedRegion,bool OriginalPreview=false);
    internal AreaEditContext? CaptureAreaEditContext()
    {
        if(Current.CaptureMode!=CaptureMode.Window) return null;
        var chosen=RequireGameWindow();var scale=getWindowScale(chosen);
        if(scale?.SourceWindow!=chosen.Handle)scale=null;
        return new(chosen,getWindowBounds(chosen),scale,Current.WindowCaptureRegion,CaptureOriginalWindow(chosen,scale));
    }
    internal PixelRect? InitialCaptureArea(AreaEditContext? edit=null)
    {
        if (Current.CaptureMode==CaptureMode.Window && Current.WindowCaptureRegion is { } relative) {
            try {
                var chosen=edit?.Target ?? RequireGameWindow();
                var window=edit?.Window ?? getWindowBounds(chosen);
                // Editing must remain possible when a resized game no longer contains the saved crop.
                long x=(long)window.X+relative.X,y=(long)window.Y+relative.Y;
                if(x>=int.MinValue && x<=int.MaxValue && y>=int.MinValue && y<=int.MaxValue)
                {
                    var original=relative with {X=(int)x,Y=(int)y};
                    var scale=edit is null ? getWindowScale(chosen) : edit.Scale;
                    bool preview=edit?.OriginalPreview ?? (scale is not null && Current.OriginalCaptureWhenScaled);
                    return !preview && scale?.SourceWindow==chosen.Handle ? scale.ToDisplay(original) : original;
                }
            }
            catch(CaptureUnavailableException) { /* Show the last saved screen position; confirmation still checks the game. */ }
            return Current.CaptureArea;
        }
        return Current.CaptureAreaConfigured ? Current.CaptureArea : null;
    }
    internal async Task<bool> ConfirmCaptureAreaAsync(PixelRect? area, Guid expectedProfile,AreaEditContext? edit=null)
    {
        if(closing || Current.Id!=expectedProfile) {model.Status="프로필이 변경되어 영역 편집을 폐기했습니다.";return false;}
        if(area is not {} value) {model.Status="영역 편집을 취소했습니다. 저장된 영역을 유지합니다.";return false;}
        if(!value.IsValid || value.Width<4 || value.Height<4)throw new InvalidDataException("영역은 4 × 4 px 이상으로 지정하세요.");
        PixelRect? relative=null;
        if(Current.CaptureMode==CaptureMode.Window) {
            var chosen=RequireGameWindow();var window=getWindowBounds(chosen);var scale=getWindowScale(chosen);
            if(scale?.SourceWindow!=chosen.Handle) scale=null;
            if(edit is not null && (edit.Target!=chosen || edit.Window!=window || edit.Scale!=scale))
                throw new CaptureUnavailableException("게임 창이나 확대 화면이 바뀌었습니다. 영역을 다시 지정하세요. 기존 영역은 보존했습니다.");
            // Keep canonical game coordinates, so toggling or moving the scaler never rewrites the profile.
            if(scale is not null && edit?.OriginalPreview!=true) {
                if(edit?.SavedRegion is {} saved && value==InitialCaptureArea(edit)) value=WindowCaptureBounds.Resolve(window,saved);
                else value=scale.ToSource(value);
            }
            relative=WindowCaptureBounds.FromScreen(window,value)
                ?? throw new CaptureUnavailableException("게임 창 내부에 영역을 배치하세요. 기존 영역은 보존했습니다.");
        }
        InvalidateCaptureInput("캡처 영역이 변경되어 이전 문장 해설을 비웠습니다.");
        Current.CaptureArea=value;Current.CaptureAreaConfigured=true;Current.WindowCaptureRegion=relative;
        RefreshCaptureAreaSummary();
        bool stored=await SaveSettings();
        if(Current.Id==expectedProfile && FirstRunWizard is {} wizard
            && wizard.Controller.ProfileId==expectedProfile) {
            if(stored) wizard.RequiresAreaConfirmation=false;
            else if(wizard.Controller.Step==FirstRunStep.Area)
                // Do not advance with an in-memory area whose persistence failed,
                // even when a previously saved area was already valid.
                wizard.RequiresAreaConfirmation=true;
        }
        return stored;
    }
    private async void SelectArea(object sender, RoutedEventArgs e)=>await SelectCaptureAreaAsync();
    internal async Task<bool> SelectCaptureAreaAsync()
    {
        if (Current.CaptureMode == CaptureMode.Clipboard) return false;
        if(captureCancellation is not null || selectingArea || closing)return false;
        if (Current.CaptureMode == CaptureMode.Window && Current.GameWindow is null)
        {
            model.Status = ChooseGameWindowMessage;
            ShowFeedback(FeedbackKind.Error, ChooseGameWindowMessage);
            return false;
        }
        selectingArea=true;StopTranslation();var profile=Current.Id;Window[] windows=[];
        try {
            var edit=CaptureAreaEditContext();var initial=InitialCaptureArea(edit);windows=HideWindows();
            if(edit is {OriginalPreview:true}) {
                var frame=await originalWindowCapture.CaptureAsync(edit.Target,null);
                if(closing || Current.Id!=profile)return false;
                if(frame.Width!=edit.Window.Width || frame.Height!=edit.Window.Height)throw new CaptureUnavailableException("게임 창 크기가 바뀌었습니다. 영역을 다시 지정하세요.");
                var selected=await SourceRegionSelector.PickAsync(frame,edit.SavedRegion);
                return await ConfirmSourceCaptureAreaAsync(selected,profile,edit);
            }
            else {
                var area=await RegionSelector.PickAsync(initial);
                return await ConfirmCaptureAreaAsync(area,profile,edit);
            }
        }
        catch(Exception ex) when(ex is CaptureUnavailableException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception) {model.Status=ex.Message;return false;}
        finally {selectingArea=false;if(!closing){RestoreWindows(windows);Activate();}}
    }
    internal Task<bool> ConfirmSourceCaptureAreaAsync(PixelRect? relative,Guid profile,AreaEditContext edit)
    {
        var area=relative is {} crop ? WindowCaptureBounds.Resolve(edit.Window,crop) : (PixelRect?)null;
        return ConfirmCaptureAreaAsync(area,profile,edit);
    }
}
