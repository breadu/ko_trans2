using KoTrans.Infrastructure;

namespace KoTrans.App;

// UI screenshot capture is explicitly enabled only when --debug-overlay was
// present at process launch. Subtitle text AND its chrome stay excluded in all
// modes to avoid capturing translated subtitles back into OCR.
internal static class CaptureExclusionPolicy
{
    internal static bool NeedsExclusion(bool debugOverlayRequested, bool subtitleWindow)
        => !debugOverlayRequested || subtitleWindow;

    internal static bool DebugCaptureEnabled => App.DebugCaptureMode;

    internal static bool TryExclude(IntPtr handle, bool subtitleWindow = false)
        => NeedsExclusion(App.DebugCaptureMode, subtitleWindow)
            && WindowTargets.TryExcludeFromCapture(handle);
}
