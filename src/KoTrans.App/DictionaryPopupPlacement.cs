// Adapted independently from breadu/MovieMalhae 88dfc708; word dictionaries only.
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using KoTrans.Infrastructure;

namespace KoTrans.App;

public readonly record struct DictionaryPopupBounds(int Left, int Top, int Width, int Height);

public static class DictionaryPopupPlacement
{
    internal sealed record RestoreResult(DictionaryPopupBounds Bounds, bool Temporary);
    internal static RestoreResult Resolve(WindowPlacement? saved, IReadOnlyList<DisplayArea> displays, DictionaryPopupBounds fallback)
    {
        if (saved is null) return new(fallback, false);
        var resolved = OverlayPlacementPolicy.Resolve(saved, displays);
        var display = displays.First(d => resolved.X >= d.WorkArea.X && resolved.Y >= d.WorkArea.Y
            && (long)resolved.X < (long)d.WorkArea.X + d.WorkArea.Width && (long)resolved.Y < (long)d.WorkArea.Y + d.WorkArea.Height);
        return new(new(resolved.X, resolved.Y, (int)Math.Round(resolved.Width * display.DpiScale),
            (int)Math.Round(resolved.Height * display.DpiScale)), resolved.Temporary);
    }
    internal static IReadOnlyList<DisplayArea> IdentifiedDisplays()
    {
        var scales = Displays();
        return DesktopWindows.Displays().Select(d => {
            var scale = scales.FirstOrDefault(s => s.Work.Left == d.WorkArea.X && s.Work.Top == d.WorkArea.Y
                && s.Work.Width == d.WorkArea.Width && s.Work.Height == d.WorkArea.Height);
            return d with { DpiScale = scale.Dpi >= 96 ? scale.Dpi / 96d : 1 };
        }).ToArray();
    }
    internal static DictionaryPopupSnapshot? Remember(IntPtr hwnd)
    {
        using var context = new PhysicalCoordinates();
        if (!IsWindow(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd) || WindowTargets.Bounds(hwnd) is not { } bounds || !bounds.IsValid) return null;
        var displays = DesktopWindows.Displays();
        if (displays.Count == 0) return null;
        double dpi = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
        var display = displays.OrderByDescending(d => Math.Max(0, Math.Min((long)bounds.X + bounds.Width, (long)d.WorkArea.X + d.WorkArea.Width) - Math.Max(bounds.X, d.WorkArea.X))
            * (double)Math.Max(0, Math.Min((long)bounds.Y + bounds.Height, (long)d.WorkArea.Y + d.WorkArea.Height) - Math.Max(bounds.Y, d.WorkArea.Y))).First();
        return new(new WindowPlacement { X = bounds.X, Y = bounds.Y, Width = bounds.Width / dpi, Height = bounds.Height / dpi,
            MonitorId = display.Id, MonitorDevice = display.Device, MonitorWorkX = display.WorkArea.X, MonitorWorkY = display.WorkArea.Y },
            new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }
    public static DictionaryPopupBounds Calculate(int left, int top, int width, int height, int preferredWidth)
    {
        var popupWidth = Math.Clamp(preferredWidth, Math.Min(420, width), width);
        var popupHeight = Math.Max(1, (int)Math.Round(height * 2.0 / 3));
        return new(left + (width - popupWidth) / 2, top + (height - popupHeight) / 2, popupWidth, popupHeight);
    }
    public static DictionaryPopupBounds ForOwner(nint owner, int preferredWidth)
    {
        using var dpi = new PhysicalCoordinates();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(owner, 2), ref info)) throw new Win32Exception();
        var scale = owner == 0 ? 96 : GetDpiForWindow(owner);
        return Calculate(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top,
            (int)Math.Round(Math.Clamp(preferredWidth, 420, 1600) * Math.Max(96u, scale) / 96.0));
    }
    public static IReadOnlyList<DictionaryDisplay> Displays()
    {
        using var dpi = new PhysicalCoordinates();
        var displays = new List<DictionaryDisplay>();
        MonitorCallback collect = (nint monitor, nint dc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || GetDpiForMonitor(monitor, 0, out var scale, out _) != 0) return true;
            displays.Add(new(Bounds(info.Monitor), Bounds(info.Work), scale));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, collect, 0)) return [];
        return displays;
    }
    private static DictionaryPopupBounds Bounds(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    public static bool Present(int browserProcessId, DictionaryPopupBounds bounds)
        => Present(browserProcessId, bounds, out _);
    internal static bool Present(int browserProcessId, DictionaryPopupBounds bounds, out IntPtr popupHandle)
    {
        using var dpi = new PhysicalCoordinates();
        nint popup = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId != browserProcessId || !IsWindowVisible(window)) return true;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() != "Chrome_WidgetWin_1") return true;
            popup = window; return false;
        }, 0);
        popupHandle = popup;
        if (popup == 0) return false;
        if (IsIconic(popup) || IsZoomed(popup)) ShowWindow(popup, 9); // restore minimized/maximized
        if (!SetWindowPos(popup, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040)) throw new Win32Exception();
        SetForegroundWindow(popup); // Windows may decline activation; never send synthetic input.
        return true;
    }
    private sealed class PhysicalCoordinates : IDisposable
    {
        private readonly nint previous = SetThreadDpiAwarenessContext(new nint(-4));
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool EnumWindow(nint window, nint parameter);
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
