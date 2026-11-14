using KoTrans.Infrastructure;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace KoTrans.App;

internal static class DesktopWindows
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    internal static Size ChromeSize(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var outer) && GetClientRect(handle, out var inner)) {
            double dpi = GetDpiForWindow(handle) / 96.0;
            if (dpi <= 0) dpi = 1;
            return new(Math.Max(0, outer.Right - outer.Left - inner.Right + inner.Left) / dpi,
                Math.Max(0, outer.Bottom - outer.Top - inner.Bottom + inner.Top) / dpi);
        }
        // No HWND is created for background layout tests or pre-source initialization.
        return new(16, 38);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    internal static void MovePhysical(Window window, int x, int y, int width = 0, int height = 0)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, width, height, 0x14u | (width == 0 ? 1u : 0u)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    internal static IReadOnlyList<DisplayArea> Displays() => Forms.Screen.AllScreens.Select(screen => {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        var id = EnumDisplayDevices(screen.DeviceName, 0, ref device, 1) ? device.Id : "";
        var r = screen.WorkingArea;
        return new DisplayArea(id ?? "", screen.DeviceName, new(r.X,r.Y,r.Width,r.Height), 1, screen.Primary);
    }).ToArray();
    internal static DisplayArea CurrentDisplay(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screen = Forms.Screen.FromHandle(handle); var area = screen.WorkingArea;
        double dpi = GetDpiForWindow(handle) / 96.0;
        return new("", screen.DeviceName, new(area.X, area.Y, area.Width, area.Height), dpi > 0 ? dpi : 1, screen.Primary);
    }
    internal static void Restore(Window w, WindowPlacement p)
    {
        w.Width = Math.Max(w.MinWidth, p.Width); w.Height = Math.Max(w.MinHeight, p.Height);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.SourceInitialized += (_, _) => ApplyPlacement(w, p);
    }
    internal static PlacementResolution ApplyPlacement(Window w, WindowPlacement p)
    {
        // Move first: GetDpiForWindow then gives the target monitor DPI for this PerMonitorV2 HWND.
        var displays = Displays(); var initial = OverlayPlacementPolicy.Resolve(p, displays);
        MovePhysical(w, initial.X, initial.Y);
        double dpi = GetDpiForWindow(new WindowInteropHelper(w).Handle) / 96.0;
        if (dpi <= 0) dpi = 1;
        var target = displays.First(d => initial.X >= d.WorkArea.X && initial.Y >= d.WorkArea.Y
            && (long)initial.X < (long)d.WorkArea.X + d.WorkArea.Width && (long)initial.Y < (long)d.WorkArea.Y + d.WorkArea.Height);
        var resolved = OverlayPlacementPolicy.Resolve(p, displays.Select(d => d == target ? d with { DpiScale = dpi } : d).ToArray());
        w.Width = Math.Max(w.MinWidth, resolved.Width); w.Height = Math.Max(w.MinHeight, resolved.Height);
        MovePhysical(w, resolved.X, resolved.Y);
        return resolved;
    }
    internal static WindowPlacement RememberOverlay(Window w)
    {
        var p = Remember(w); var displays = Displays();
        var bounds = WindowTargets.Bounds(new WindowInteropHelper(w).Handle) ?? throw new InvalidOperationException("자막창 위치를 읽을 수 없습니다.");
        var screen = displays.OrderByDescending(d => Intersection(bounds, d)).First();
        p.MonitorId = screen.Id; p.MonitorDevice = screen.Device;
        p.MonitorWorkX = screen.WorkArea.X; p.MonitorWorkY = screen.WorkArea.Y; return p;
    }
    private static double Intersection(PixelRect p, DisplayArea d) =>
        Math.Max(0, Math.Min(p.X + p.Width, (double)d.WorkArea.X + d.WorkArea.Width) - Math.Max(p.X,d.WorkArea.X))
        * Math.Max(0, Math.Min(p.Y + p.Height, (double)d.WorkArea.Y + d.WorkArea.Height) - Math.Max(p.Y,d.WorkArea.Y));
    internal static WindowPlacement Remember(Window w)
    {
        if (!GetWindowRect(new WindowInteropHelper(w).Handle, out var r)) return new();
        return new() { X = r.Left, Y = r.Top, Width = w.ActualWidth, Height = w.ActualHeight };
    }
}
