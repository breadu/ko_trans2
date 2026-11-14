using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public sealed record WindowTarget(IntPtr Handle, int ProcessId, long StartedTicks, string Title, string ProcessName, string ExecutablePath = "", string WindowClass = "")
{
    public GameWindowIdentity Identity => new(ProcessName, ExecutablePath, WindowClass, Title);
    public override string ToString() => $"{Title} · {ProcessName} ({ProcessId})";
}
public static partial class WindowTargets
{
    public static IReadOnlyList<WindowTarget> List(bool includeCurrentProcess = false)
    {
        var result = new List<WindowTarget>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            if (pid == Environment.ProcessId && !includeCurrentProcess) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
                var windowClass = new StringBuilder(512); GetClassName(handle, windowClass, windowClass.Capacity);
                string executable = "";
                try { executable = process.MainModule?.FileName ?? ""; } catch (System.ComponentModel.Win32Exception) { }
                result.Add(new(handle, (int)pid, process.StartTime.ToUniversalTime().Ticks, title.ToString(), process.ProcessName, executable, windowClass.ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return result.OrderBy(x => x.Title).ToArray();
    }
    public static WindowTarget? Reconnect(GameWindowIdentity identity, IReadOnlyList<WindowTarget> windows, out bool ambiguous)
    {
        var matches = ReconnectionCandidates(identity, windows);
        ambiguous = matches.Count > 1; return matches.Count == 1 ? matches[0] : null;
    }
    public static IReadOnlyList<WindowTarget> ReconnectionCandidates(GameWindowIdentity identity, IReadOnlyList<WindowTarget> windows)
    {
        var stable = windows.Where(w => identity.MatchesStableIdentity(w.Identity)).ToArray();
        if (stable.Length > 0) return stable;
        // Legacy metadata may lack an executable/class. Keep its exact matching
        // contract; never widen to process names/title fragments. Strong matches
        // remain ambiguous even if one mutable caption happens to match.
        return windows.Where(w => identity.Matches(w.Identity)).ToArray();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int max);
    public static PixelRect Resolve(WindowTarget target, bool requireForeground = true)
    {
        GetWindowThreadProcessId(target.Handle, out uint pid);
        if (!IsWindow(target.Handle) || pid != target.ProcessId) throw new CaptureUnavailableException("대상 창이 종료되었습니다. 창을 다시 선택하세요.");
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != target.StartedTicks) throw new CaptureUnavailableException("대상 프로세스가 변경되었습니다. 창을 다시 선택하세요.");
        }
        catch (ArgumentException) { throw new CaptureUnavailableException("대상 프로세스를 찾을 수 없습니다."); }
        var currentClass = new StringBuilder(512); GetClassName(target.Handle, currentClass, currentClass.Capacity);
        // Keep the selected live HWND bound to its PID/start time/class. A scene
        // title is mutable and must not stop capture from that same window.
        if (target.WindowClass.Length > 0 && currentClass.ToString() != target.WindowClass)
            throw new CaptureUnavailableException("대상 창 클래스가 변경되었습니다. 창을 다시 선택하세요.");
        if (!IsWindowVisible(target.Handle) || IsIconic(target.Handle)) throw new CaptureUnavailableException("대상 창이 숨겨지거나 최소화되었습니다. 복원하면 감시를 재개합니다.", CaptureWaitReason.HiddenOrMinimized);
        if (requireForeground && GetForegroundWindow() != target.Handle) throw new CaptureUnavailableException("대상 창을 맨 앞으로 가져오세요. GDI 창 캡처는 대상 창이 활성화된 동안만 진행합니다.", CaptureWaitReason.NotForeground);
        if (!GetClientRect(target.Handle, out var rect)) throw new CaptureUnavailableException("대상 창의 크기를 읽을 수 없습니다.");
        var point = new NativePoint();
        if (!ClientToScreen(target.Handle, ref point)) throw new CaptureUnavailableException("대상 창의 좌표를 읽을 수 없습니다.");
        var area = new PixelRect(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (!area.IsValid) throw new CaptureUnavailableException("대상 창 크기가 올바르지 않습니다.");
        return area;
    }
    public static bool ActivateForManual(WindowTarget target) { Resolve(target, false); return SetForegroundWindow(target.Handle); }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    public static bool IsForeground(WindowTarget? target)
    {
        var handle = GetForegroundWindow(); GetWindowThreadProcessId(handle, out uint pid);
        return target is null ? pid != Environment.ProcessId : handle == target.Handle;
    }
    public static bool TryExcludeFromCapture(IntPtr handle) => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && SetWindowDisplayAffinity(handle, 0x11);
    public static PixelRect? Bounds(IntPtr handle) => GetWindowRect(handle, out var rect) ? new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : null;
    public static bool Overlaps(PixelRect a, PixelRect b) => a.X < (long)b.X + b.Width && b.X < (long)a.X + a.Width && a.Y < (long)b.Y + b.Height && b.Y < (long)a.Y + a.Height;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate bool EnumCallback(IntPtr handle, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr handle, uint affinity);
}
