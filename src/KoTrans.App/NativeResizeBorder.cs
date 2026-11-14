using System.Runtime.InteropServices;

namespace KoTrans.App;

// Some high-DPI native frames return HTCLIENT/HTCAPTION between two sizing
// pixels inside the system's own frame width. Fill only that gap: never extend
// into the client area or replace native caption-button/corner hit tests.
internal static class NativeResizeBorder
{
    internal const int HitTestMessage = 0x84;
    internal static IntPtr Handle(Window window, IntPtr handle, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (window.WindowState != WindowState.Normal || window.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            || !GetWindowRect(handle, out var bounds)) return IntPtr.Zero;
        int native = DwmDefWindowProc(handle, HitTestMessage, wParam, lParam, out var result)
            ? result.ToInt32() : DefWindowProc(handle, HitTestMessage, wParam, lParam).ToInt32();
        if (native is not (1 or 2)) return IntPtr.Zero;
        uint dpi = GetDpiForWindow(handle);
        int padding = GetSystemMetricsForDpi(92, dpi); // SM_CXPADDEDBORDER
        int frameX = GetSystemMetricsForDpi(32, dpi) + padding; // SM_CXSIZEFRAME
        int frameY = GetSystemMetricsForDpi(33, dpi) + padding; // SM_CYSIZEFRAME
        int hit = Resolve(native, ScreenPoint(lParam), new Rect(bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), frameX, frameY);
        if (hit == native) return IntPtr.Zero;
        handled = true;
        return new IntPtr(hit);
    }
    internal static Point ScreenPoint(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        return new(unchecked((short)value), unchecked((short)(value >> 16)));
    }
    internal static int Resolve(int native, Point point, Rect bounds, int frameX, int frameY)
    {
        if (native is not (1 or 2) || bounds.IsEmpty || frameX <= 0 || frameY <= 0
            || point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom)
            return native;
        bool left = point.X < bounds.Left + frameX, right = point.X >= bounds.Right - frameX;
        bool top = point.Y < bounds.Top + frameY, bottom = point.Y >= bounds.Bottom - frameY;
        if (top) return left ? 13 : right ? 14 : 12;
        if (bottom) return left ? 16 : right ? 17 : 15;
        return left ? 10 : right ? 11 : native;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern bool DwmDefWindowProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, out IntPtr result);
}
