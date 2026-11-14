using System.Collections.Generic;
using System.Runtime.InteropServices;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public readonly record struct CaptureWindowState(bool Visible, bool Minimized, bool Cloaked, PixelRect? Bounds);
public sealed record CaptureCover(IntPtr Handle, PixelRect Bounds);

public static partial class WindowTargets
{
    public static CaptureWindowState CaptureState(IntPtr handle)
    {
        bool visible = handle != IntPtr.Zero && IsWindow(handle) && IsWindowVisible(handle);
        bool minimized = visible && IsIconic(handle);
        bool cloaked = visible && DwmGetCloaked(handle, 14, out uint value, sizeof(uint)) == 0 && value != 0;
        // Never use a minimized/hidden window's stale or iconic rectangle for capture blocking.
        if (!visible || minimized || cloaked) return new(visible, minimized, cloaked, null);
        PixelRect? bounds = null;
        if (DwmGetFrameBounds(handle, 9, out var rect, Marshal.SizeOf<NativeRect>()) == 0) {
            var frame = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (frame.IsValid) bounds = frame; // Excludes invisible resize borders/shadow.
        }
        return new(visible, minimized, cloaked, bounds ?? Bounds(handle));
    }
    public static CaptureCover? ForegroundCaptureCover()
    {
        var handle = GetForegroundWindow(); var state = CaptureState(handle);
        if (!state.Visible || state.Minimized || state.Cloaked || state.Bounds is null) return null;
        long style = CaptureGetWindowLongPtr(handle, -20).ToInt64();
        // A layered/transparent/direct-composition window does not prove the window below is hidden.
        if ((style & (0x80000 | 0x20 | 0x00200000)) != 0) return null;
        if (!GetClientRect(handle, out var rect)) return null;
        var origin = new NativePoint(); if (!ClientToScreen(handle, ref origin)) return null;
        var client = new PixelRect(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return client.IsValid ? new(handle, client) : null;
    }
    public static bool IsAbove(IntPtr higher, IntPtr lower)
    {
        if (higher == IntPtr.Zero || lower == IntPtr.Zero || higher == lower) return false;
        // Bound traversal and detect destroyed/reordered handles; uncertain order keeps the guard.
        var seen = new HashSet<IntPtr>(); var current = lower;
        for (int i = 0; i < 512; i++) {
            current = CaptureGetWindow(current, 3); // GW_HWNDPREV, higher in Z order.
            if (current == IntPtr.Zero || !seen.Add(current)) return false;
            if (current == higher) return true;
        }
        return false;
    }
    // Pure physical-pixel decision shared by the live guard and background regressions.
    public static bool BlocksCapture(CaptureWindowState own, PixelRect capture, PixelRect? cover, bool coverAbove)
    {
        if (!own.Visible || own.Minimized || own.Cloaked || own.Bounds is not { IsValid: true } window || !Overlaps(window, capture)) return false;
        if (!coverAbove || cover is not { IsValid: true } front) return true;
        // Ignore only the intersection hidden by the foreground client, not the whole app window.
        // A remaining exposed strip outside that client must still block the capture.
        long left = Math.Max(window.X, capture.X), top = Math.Max(window.Y, capture.Y);
        long right = Math.Min((long)window.X + window.Width, (long)capture.X + capture.Width);
        long bottom = Math.Min((long)window.Y + window.Height, (long)capture.Y + capture.Height);
        return left < front.X || top < front.Y || right > (long)front.X + front.Width || bottom > (long)front.Y + front.Height;
    }
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetCloaked(IntPtr handle, uint attribute, out uint value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetFrameBounds(IntPtr handle, uint attribute, out NativeRect value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr CaptureGetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr CaptureGetWindowLongPtr(IntPtr handle, int index);
}
