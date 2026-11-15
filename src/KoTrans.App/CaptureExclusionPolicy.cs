using KoTrans.Infrastructure;

namespace KoTrans.App;

// Startup-only policy. Debug preserves subtitle exclusion; recording exposes all
// windows. A false exclusion result deliberately keeps OCR overlap protection active.
internal static class CaptureExclusionPolicy
{
    internal static bool NeedsExclusion(bool debugOverlayRequested, bool subtitleWindow, bool recordingRequested = false)
        => !recordingRequested && (!debugOverlayRequested || subtitleWindow);

    internal static bool UiCaptureEnabled => App.DebugCaptureMode || App.RecordingMode;

    internal static bool TryExclude(IntPtr handle, bool subtitleWindow = false)
        // Each HWND is new: not setting affinity leaves WDA_NONE. No runtime toggle.
        => NeedsExclusion(App.DebugCaptureMode, subtitleWindow, App.RecordingMode)
            && WindowTargets.TryExcludeFromCapture(handle);
}
