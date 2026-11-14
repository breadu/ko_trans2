using System.Windows.Interop;
using System.Windows.Threading;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed partial class TranslationWindow
{
    private MagpieOverlayOrder? magpieOrder;
    private void AttachMagpieOrder(nint handle, Func<WindowTarget?>? getGameWindow)
    {
        // Created only after SourceInitialized. Background WPF tests never register
        // a message, query the desktop, create a native window, or touch its order.
        magpieOrder = new(new NativeMagpieOverlayWindows(), () => new(
            new WindowInteropHelper(this).Handle, new WindowInteropHelper(Chrome).Handle,
            !closed && IsVisible && WindowState != WindowState.Minimized, profile.CaptureMode,
            getGameWindow?.Invoke(), profile.CaptureAreaConfigured ? profile.CaptureArea : null),
            action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action));
        magpieOrder.Attach(handle);
    }
}
