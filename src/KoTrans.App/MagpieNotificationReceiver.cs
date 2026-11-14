using System.Windows.Interop;

namespace KoTrans.App;

// Created on the overlay's UI thread only after SourceInitialized. Never shown,
// never owned by a WPF Window, and deliberately NOT an HWND_MESSAGE window:
// message-only HWNDs do not receive Magpie's HWND_BROADCAST notifications.
internal sealed class MagpieNotificationReceiver : IDisposable
{
    private HwndSource? source;
    private readonly Action<int, nint, nint> received;
    internal int Message { get; }
    internal nint Handle => source?.Handle ?? 0;
    internal static HwndSourceParameters Parameters() => new("KO Trans 2 Magpie notifications") {
        WindowStyle = unchecked((int)0x80000000), // WS_POPUP; no WS_VISIBLE/WS_CHILD
        ExtendedWindowStyle = 0x08000080, // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        ParentWindow = IntPtr.Zero, Width = 0, Height = 0
    };
    internal MagpieNotificationReceiver(int message, Action<int, nint, nint> received)
    {
        if (message is < 0xC000 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(message));
        Message = message; this.received = received;
        source = new HwndSource(Parameters());
        source.AddHook(Receive);
        // Allow only the registered notification at differing integrity levels.
        ChangeWindowMessageFilterEx(source.Handle, (uint)message, 1, IntPtr.Zero);
    }
    private nint Receive(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (source is not null && message == Message) {
            received(message, wParam, lParam); handled = true;
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        var old = source; source = null;
        if (old is null) return;
        old.RemoveHook(Receive); old.Dispose();
    }
    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint status);
}
