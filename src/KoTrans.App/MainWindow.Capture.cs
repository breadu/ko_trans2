using KoTrans.Infrastructure;

namespace KoTrans.App;
public partial class MainWindow
{
    internal PixelRect? WindowRegion(Profile profile, WindowTarget chosen)
    {
        if (profile.WindowCaptureRegion is { } saved) {
            try { WindowCaptureBounds.Resolve(getWindowBounds(chosen), saved); }
            catch (InvalidDataException) { throw new CaptureUnavailableException("게임 창 크기가 바뀌었습니다. 저장 영역을 보존했습니다. 대사 영역을 다시 지정하세요."); }
            return saved;
        }
        // Preserve an existing explicitly selected dialogue area when migrating a window profile.
        return profile.CaptureArea == new PixelRect(0, 0, 800, 300) ? null
            : WindowCaptureBounds.FromScreen(getWindowBounds(chosen), profile.CaptureArea)
                ?? throw new CaptureUnavailableException("지정한 대사 영역이 대상 창 밖에 있습니다. 게임 창 안에서 영역을 다시 지정하세요.");
    }
    private async Task<ImageFrame> CaptureProfileAsync(CaptureMode mode, PixelRect area, WindowTarget? chosen, PixelRect? region, bool manual, CancellationToken token)
    {
        // Reject the placeholder rectangle before hiding/activating windows or reading pixels.
        RecognitionPreflight.ValidateCapture(Current, mode);
        if (mode == CaptureMode.Window) chosen = RequireGameWindow();
        bool hidden = false;
        try
        {
            if (mode == CaptureMode.Clipboard) throw new CaptureUnavailableException("클립보드 모드에서는 화면을 캡처하지 않습니다.");
            if (mode == CaptureMode.Window && manual && chosen is not null && !WindowTargets.IsForeground(chosen))
            {
                if (IsVisible) { Hide(); hidden = true; }
                if (!WindowTargets.ActivateForManual(chosen)) throw new CaptureUnavailableException("게임 창을 앞으로 가져온 뒤 다시 요청하세요.", CaptureWaitReason.NotForeground);
                await Task.Delay(120, token);
            }
            token.ThrowIfCancellationRequested();
            var client=mode==CaptureMode.Window ? WindowTargets.Resolve(chosen!) : area;
            var scale=mode==CaptureMode.Window ? getWindowScale(chosen!) : null;
            bool original=mode==CaptureMode.Window && CaptureOriginalWindow(chosen!,scale);
            var plan = mode == CaptureMode.Window
                ? WindowCapturePlan.Create(chosen!.Handle,client,region,scale,original)
                : new WindowCapturePlan(area,area.Width,area.Height,null);
            // A direct client render cannot contain covering settings/overlay windows.
            if(!original) EnsureCaptureSafe(plan.ScreenArea,plan.Scale is {} scaled ? new CaptureCover(scaled.OutputWindow,scaled.Destination) : null);
            var frame=original ? await originalWindowCapture.CaptureAsync(chosen!,region,token)
                : await Task.Run(() => ScreenCapture.Capture(plan, token), token);
            token.ThrowIfCancellationRequested();
            // Revalidate the live window identity, foreground and geometry after the
            // asynchronous pixel read, just as the marker observation path does.
            plan.ValidateCompletion(frame, client, mode == CaptureMode.Window ? WindowTargets.Resolve(chosen!) : area,
                mode == CaptureMode.Window ? getWindowScale(chosen!) : null);
            debugTelemetry?.SetContext(v=>v with {CapturePath=original ? "Window-PrintClient" : plan.Scale is null ? "GDI-Screen" : "GDI-Magpie",
                ScreenCaptureWidth=original ? client.Width : plan.ScreenArea.Width,ScreenCaptureHeight=original ? client.Height : plan.ScreenArea.Height});
            return frame;
        }
        finally
        {
            if (hidden && !closing) { var activated = ShowActivated; ShowActivated = false; Show(); ShowActivated = activated; }
        }
    }
}
