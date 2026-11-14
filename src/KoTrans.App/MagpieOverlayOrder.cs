using System.Text;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed record MagpiePresentation(nint Window, nint Source, nint Foreground, PixelRect Bounds);
internal sealed record OverlayOrderContext(nint Body, nint Chrome, bool Visible, CaptureMode Mode,
    WindowTarget? Target, PixelRect? CaptureArea);

internal interface IMagpieOverlayWindows
{
    int Listen(Action<int, nint, nint> received);
    void StopListening();
    MagpiePresentation? Find(nint candidate);
    bool MatchesTarget(WindowTarget target);
    bool IsVisible(nint window);
    PixelRect? Bounds(nint window);
    bool IsAbove(nint higher, nint lower);
    bool Position(nint window, nint after, uint flags);
}

// UI-thread only. No timer, activation, Show/Hide, or changes to another process's windows.
// Contract: https://github.com/Blinue/Magpie/wiki/Interact-with-Magpie-programally
internal sealed class MagpieOverlayOrder(IMagpieOverlayWindows windows, Func<OverlayOrderContext> context,
    Action<Action> dispatch) : IDisposable
{
    internal const uint PositionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
    private int message, generation;
    private bool pending, disposed;
    private nint candidate;

    internal void Attach(nint body)
    {
        if (!disposed && message == 0 && body != 0)
            message = windows.Listen((id, eventId, output) => HandleMessage(id, eventId, output));
    }
    internal bool HandleMessage(int id, nint eventId, nint output)
    {
        if (disposed || message == 0 || id != message) return false;
        // Event 0 includes Alt-Tab away. Invalidate queued work, but retain the
        // overlay's normal topmost setting (unlike Magpie's generic sample app).
        if (eventId == 0) CancelPending();
        else if (eventId == 1 && output != 0) Queue(output);
        // Events 2/3 only describe resizing/moving the scaling window.
        return true;
    }
    internal void Shown() => Queue(0); // Discover an already-running scaling session once.
    internal void Hidden() => CancelPending();
    private void Queue(nint output)
    {
        if (disposed || message == 0 || !context().Visible) return;
        candidate = output;
        if (pending) return;
        pending = true;
        int ticket = generation;
        dispatch(() => {
            if (disposed || !pending || ticket != generation) return;
            pending = false;
            RestoreOrder(candidate);
        });
    }
    private void CancelPending() { generation++; pending = false; }
    private void RestoreOrder(nint output)
    {
        var own = context();
        if (!own.Visible || own.Body == 0 || !windows.IsVisible(own.Body)) return;
        var scaling = windows.Find(output);
        if (scaling is null || scaling.Source == 0 || !scaling.Bounds.IsValid
            || (scaling.Foreground != scaling.Source && scaling.Foreground != scaling.Window)) return;
        if (own.Mode == CaptureMode.Window) {
            if (own.Target is null || own.Target.Handle != scaling.Source || !windows.MatchesTarget(own.Target)) return;
        } else {
            // Region/clipboard profiles may have no selected HWND. Limit the
            // adjustment to the active scaling display, not another monitor.
            var area = own.Mode == CaptureMode.Region ? own.CaptureArea : windows.Bounds(own.Body);
            if (area is not { IsValid: true } || !WindowTargets.Overlaps(area.Value, scaling.Bounds)) return;
        }
        bool bodyRaised = !windows.IsAbove(own.Body, scaling.Window);
        if (bodyRaised && !windows.Position(own.Body, -1, PositionFlags)) return; // HWND_TOPMOST
        if (own.Chrome != 0 && windows.IsVisible(own.Chrome)
            && (bodyRaised || !windows.IsAbove(own.Chrome, scaling.Window) || !windows.IsAbove(own.Chrome, own.Body)))
            windows.Position(own.Chrome, -1, PositionFlags);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; CancelPending(); windows.StopListening();
    }
}

internal sealed class NativeMagpieOverlayWindows : IMagpieOverlayWindows
{
    private const string ScalingClass = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";
    private MagpieNotificationReceiver? receiver;
    public int Listen(Action<int, nint, nint> received)
    {
        if (receiver is not null) return receiver.Message;
        uint id = RegisterWindowMessage("MagpieScalingChanged");
        if (id is < 0xC000 or > 0xFFFF) return 0;
        // A WPF ShowInTaskbar=false overlay has a hidden owner. HWND_BROADCAST
        // does not reach that owned, borderless window on the tested desktop.
        // Receive on a separate invisible, unowned top-level HWND instead.
        try { receiver = new((int)id, received); }
        catch (System.ComponentModel.Win32Exception) { return 0; }
        return (int)id;
    }
    public void StopListening() { receiver?.Dispose(); receiver = null; }
    public MagpiePresentation? Find(nint candidate)
    {
        nint output = FindWindow(ScalingClass, null);
        if (output == 0 || (candidate != 0 && candidate != output) || !IsVisible(output)) return null;
        var name = new StringBuilder(128);
        if (GetClassName(output, name, name.Capacity) == 0 || name.ToString() != ScalingClass) return null;
        nint source = GetProp(output, "Magpie.SrcHWND");
        if (source == 0 || source == output || !IsWindow(source) || !IsVisible(source)) return null;
        if (Bounds(output) is not { IsValid: true } bounds || GetProp(output, "Magpie.SrcHWND") != source) return null;
        return new(output, source, GetForegroundWindow(), bounds);
    }
    public bool MatchesTarget(WindowTarget target)
    {
        try { WindowTargets.Resolve(target, false); return true; }
        catch (Exception ex) when (ex is CaptureUnavailableException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
    public bool IsVisible(nint window) => window != 0 && IsWindowVisible(window) && !IsIconic(window);
    public PixelRect? Bounds(nint window) => WindowTargets.Bounds(window);
    public bool IsAbove(nint higher, nint lower) => WindowTargets.IsAbove(higher, lower);
    public bool Position(nint window, nint after, uint flags) => SetWindowPos(window, after, 0, 0, 0, 0, flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
