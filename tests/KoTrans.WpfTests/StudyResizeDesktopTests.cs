using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

// Explicit diagnostic only. No game capture, settings, AI or mouse clicks.
internal static class StudyResizeDesktopTests
{
    internal static void Run()
    {
        var display = DesktopWindows.Displays().Where(d => !d.Primary)
            .OrderByDescending(d => d.WorkArea.Height > d.WorkArea.Width).FirstOrDefault()
            ?? throw new DesktopEnvironmentException("No secondary monitor found.");
        if (!GetCursorPos(out var savedMouse)) throw new DesktopEnvironmentException("GetCursorPos failed.");
        var foreground = GetForegroundWindow();
        var settings = new AppSettings { StudyTopmost = true };
        settings.Validate();
        settings.DefaultProfile.StudyWindow = new() { X = display.WorkArea.X + 50, Y = display.WorkArea.Y + 70,
            Width = 560, Height = 650, MonitorId = display.Id, MonitorDevice = display.Device };
        var reader = Track(new StudyWindow(settings, resolveVoiceDirectory: () => null));
        try {
            reader.Render(WpfTests.Example("Synthetic English study sentence. No game or API data."));
            reader.ShowReader(); Until(() => reader.IsLoaded, "Reader did not load");
            Complete(Task.Delay(350));
            var hwnd = new WindowInteropHelper(reader).Handle;
            if (!GetWindowRect(hwnd, out var bounds)) throw new DesktopEnvironmentException("GetWindowRect failed.");
            double scale = GetDpiForWindow(hwnd) / 96.0;
            Console.WriteLine($"StudyResize display={display.Device}, workArea={display.WorkArea.Width}x{display.WorkArea.Height}, DPI={scale * 100:F0}%, outer={bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top}, ResizeMode={reader.ResizeMode}, WindowStyle={reader.WindowStyle}");
            ReportFrame("study-restored", hwnd);
            // The production fix must cover the system frame continuously and
            // still leave the first actual client pixel available to scrolling.
            uint windowDpi = GetDpiForWindow(hwnd);
            int pad = GetSystemMetricsForDpi(92, windowDpi);
            foreach (string edge in new[] { "left", "right", "top", "bottom" }) {
                int frame = GetSystemMetricsForDpi(edge is "left" or "right" ? 32 : 33, windowDpi) + pad;
                int expected = edge switch { "left" => 10, "right" => 11, "top" => 12, _ => 15 };
                for (int inset = 0; inset < frame; inset++)
                    Check(SendMessage(hwnd, 0x84, IntPtr.Zero, Pack(EdgePoint(edge, bounds, inset))).ToInt32() == expected,
                        $"Native {edge} frame still has a gap at {inset}px");
            }
            foreach (bool active in new[] { false, true }) {
                if (active && !SetForegroundWindow(hwnd)) { Console.WriteLine("ENVIRONMENT UNAVAILABLE: active-window comparison denied by Windows."); continue; }
                Complete(Task.Delay(150));
                foreach (string edge in new[] { "left", "right", "top", "bottom" }) {
                    var readings = new List<string>(); PointNative? hold = null;
                    foreach (int inset in new[] { 1, 3, 6, 8, 10, 12, 14, 18, 24, 30, 36, 42 }) {
                        var p = EdgePoint(edge, bounds, inset);
                        int hit = SendMessage(hwnd, 0x84, IntPtr.Zero, Pack(p)).ToInt32();
                        if (!SetCursorPos(p.X, p.Y)) throw new DesktopEnvironmentException($"SetCursorPos failed: Win32 {Marshal.GetLastWin32Error()}");
                        Complete(Task.Delay(45));
                        var cursor = ReadCursor(); bool over = WindowFromPoint(p) == hwnd;
                        readings.Add($"{inset}px:HT{hit}/cursor={cursor.Cursor.ToInt64():X}{(over ? "" : "/other-window")}");
                        if (over && hit is >= 10 and <= 17) hold = p;
                    }
                    Console.WriteLine($"StudyResize active={active} {edge} sweep " + string.Join(", ", readings));
                    if (hold is not { } stablePoint) throw new DesktopEnvironmentException($"No reachable resize point at {edge}.");
                    if (!SetCursorPos(stablePoint.X, stablePoint.Y)) throw new DesktopEnvironmentException("SetCursorPos failed at hold.");
                    Complete(Task.Delay(100));
                    var shapes = new HashSet<IntPtr>(); int hitChanges = 0, occluded = 0;
                    int initialHit = SendMessage(hwnd, 0x84, IntPtr.Zero, Pack(stablePoint)).ToInt32();
                    for (int sample = 0; sample < 15; sample++) {
                        Complete(Task.Delay(50));
                        var cursor = ReadCursor();
                        if (cursor.Position.X != stablePoint.X || cursor.Position.Y != stablePoint.Y)
                            throw new DesktopEnvironmentException("Mouse moved during stationary measurement.");
                        shapes.Add(cursor.Cursor);
                        if (WindowFromPoint(stablePoint) != hwnd) occluded++;
                        if (SendMessage(hwnd, 0x84, IntPtr.Zero, Pack(stablePoint)).ToInt32() != initialHit) hitChanges++;
                    }
                    Console.WriteLine($"StudyResize active={active} {edge} stationary750ms cursorShapes={shapes.Count}, hitChanges={hitChanges}, otherWindowSamples={occluded}");
                    if (occluded != 0) throw new DesktopEnvironmentException("Another window covered the sampled reader border.");
                    Check(shapes.Count == 1 && hitChanges == 0, "Resize cursor/hit changed at a stationary point");
                }
            }
            if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x37)) throw new DesktopEnvironmentException("Frame refresh failed.");
            Complete(Task.Delay(100)); ReportFrame("study-after-frame-refresh", hwnd);
            reader.Hide();
            double systemScale = GetDpiForSystem() / 96.0;
            var basic = Track(new Window { Title = "KO Trans 2 · native-frame comparison", Width = 560, Height = 650,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = (display.WorkArea.X + 50) / systemScale,
                Top = (display.WorkArea.Y + 70) / systemScale, ShowActivated = false, Topmost = true });
            try { basic.Show(); Complete(Task.Delay(200)); ReportFrame("plain-WPF-created-on-secondary", new WindowInteropHelper(basic).Handle); }
            finally { Close(basic); }
            string nativeClass = "KoTrans2.ResizeDiagnostic." + Guid.NewGuid().ToString("N");
            var instance = GetModuleHandle(null);
            var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(DefaultProcedure), ClassName = nativeClass,
                Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), Background = new IntPtr(6) };
            if (RegisterClassEx(ref definition) == 0) throw new DesktopEnvironmentException("RegisterClassEx failed.");
            var nativeWindow = CreateWindowEx(8, nativeClass, "KO Trans 2 · Win32 default-frame comparison", 0x10CF0000,
                display.WorkArea.X + 50, display.WorkArea.Y + 70, 1200, 1600, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (nativeWindow == IntPtr.Zero) throw new DesktopEnvironmentException($"CreateWindowEx failed: Win32 {Marshal.GetLastWin32Error()}");
            try { Complete(Task.Delay(200)); ReportFrame("Win32-created-on-secondary", nativeWindow); }
            finally { DestroyWindow(nativeWindow); UnregisterClass(nativeClass, instance); }
            Check(true, "Native resize diagnosis completed");
            Console.WriteLine("Diagnostic completion is not proof that an intermittent user symptom is absent. No drag, game, API or screenshot was used.");
        } finally {
            Close(reader);
            SetCursorPos(savedMouse.X, savedMouse.Y);
            if (foreground != IntPtr.Zero) SetForegroundWindow(foreground);
        }
    }
    private static void ReportFrame(string label, IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r) || !GetClientRect(hwnd, out var client)) throw new DesktopEnvironmentException("Frame bounds unavailable.");
        var origin = new PointNative(0, 0); ClientToScreen(hwnd, ref origin);
        uint dpi = GetDpiForWindow(hwnd);
        Console.WriteLine($"Frame {label}: DPI={dpi}, clientInsetLeft={origin.X - r.Left}px, systemResizeMetric={GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi)}px, outerOrigin={r.Left},{r.Top}");
        foreach (string edge in new[] { "left", "right", "top", "bottom" }) {
            int[] resizeHits = Enumerable.Range(0, 45).Where(inset => SendMessage(hwnd, 0x84, IntPtr.Zero, Pack(EdgePoint(edge, r, inset))).ToInt32() is >= 10 and <= 17).ToArray();
            var p = EdgePoint(edge, r, 14);
            bool dwmHandled = DwmDefWindowProc(hwnd, 0x84, IntPtr.Zero, Pack(p), out var dwmHit);
            Console.WriteLine($"Frame {label} {edge}: resizeInsets=[{string.Join(",", resizeHits)}], at14px actualHT={SendMessage(hwnd, 0x84, IntPtr.Zero, Pack(p))}, DefWindowProcHT={DefWindowProc(hwnd, 0x84, IntPtr.Zero, Pack(p))}, DwmHandled={dwmHandled}/HT{dwmHit}");
        }
    }
    internal static int InspectFrameAndCursor(string label, IntPtr hwnd)
    {
        ReportFrame(label, hwnd);
        if (!GetWindowRect(hwnd, out var bounds)) throw new DesktopEnvironmentException("Window bounds unavailable.");
        uint dpi = GetDpiForWindow(hwnd); int pad = GetSystemMetricsForDpi(92, dpi), gaps = 0;
        foreach (string edge in new[] { "left", "right", "top", "bottom" }) {
            int frame = GetSystemMetricsForDpi(edge is "left" or "right" ? 32 : 33, dpi) + pad;
            PointNative? hold = null;
            for (int inset = 0; inset < frame; inset++) {
                var point = EdgePoint(edge, bounds, inset);
                int hit = SendMessage(hwnd, 0x84, IntPtr.Zero, Pack(point)).ToInt32();
                // Prefer the formerly missing inner pixels, not just the
                // already-working one-pixel inner outline of the native frame.
                if (hit is >= 10 and <= 17) { if (hold is null || inset < frame - 1) hold = point; } else gaps++;
            }
            if (hold is not { } target) throw new DesktopEnvironmentException(label + ": no resize edge reachable.");
            if (!SetCursorPos(target.X, target.Y)) throw new DesktopEnvironmentException("SetCursorPos failed.");
            Complete(Task.Delay(100));
            var shapes = new HashSet<IntPtr>();
            var expectedCursor = LoadCursor(IntPtr.Zero, new IntPtr(edge is "left" or "right" ? 32644 : 32645));
            for (int i = 0; i < 10; i++) {
                Complete(Task.Delay(50)); var current = ReadCursor();
                if (current.Position.X != target.X || current.Position.Y != target.Y || WindowFromPoint(target) != hwnd)
                    throw new DesktopEnvironmentException(label + ": mouse moved or another window covers the edge.");
                shapes.Add(current.Cursor);
                Check(current.Cursor == expectedCursor, label + ": native resize hit did not produce a resize cursor.");
            }
            Check(shapes.Count == 1, label + ": stationary resize cursor changed.");
            Console.WriteLine($"Cursor {label} {edge}: stationary500ms shapes={shapes.Count}");
        }
        Console.WriteLine($"Frame {label}: total non-sizing pixels inside system frame={gaps}");
        return gaps;
    }
    private static PointNative EdgePoint(string edge, RectNative r, int inset) => edge switch {
        "left" => new(r.Left + inset, (r.Top + r.Bottom) / 2),
        "right" => new(r.Right - 1 - inset, (r.Top + r.Bottom) / 2),
        "top" => new((r.Left + r.Right) / 2, r.Top + inset),
        _ => new((r.Left + r.Right) / 2, r.Bottom - 1 - inset)
    };
    private static IntPtr Pack(PointNative p) => new(unchecked((int)((uint)(ushort)p.X | ((uint)(ushort)p.Y << 16))));
    private static CursorInfo ReadCursor() {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info)) throw new DesktopEnvironmentException($"GetCursorInfo failed: Win32 {Marshal.GetLastWin32Error()}");
        return info;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Cursor; public PointNative Position; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    private static readonly WindowProcedure DefaultProcedure = DefWindowProc;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("dwmapi.dll")] private static extern bool DwmDefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RectNative rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out RectNative rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref PointNative point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
