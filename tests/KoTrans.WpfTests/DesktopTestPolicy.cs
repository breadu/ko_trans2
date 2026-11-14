using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace KoTrans.WpfTests;

internal sealed class DesktopEnvironmentException(string message) : Exception(message);

internal static class DesktopTestPolicy
{
    // Called only after the explicit --desktop selection, never by Background.
    internal static void RequireInteractiveDesktop()
    {
        if (!Environment.UserInteractive) throw new DesktopEnvironmentException("No interactive user session.");
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS, no switching/unlocking.
        if (desktop == IntPtr.Zero) throw Error("OpenInputDesktop");
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)) throw Error("GetUserObjectInformation");
            if (!string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                throw new DesktopEnvironmentException("The input desktop is locked or not the normal interactive desktop.");
            if (!GetCursorPos(out _)) throw Error("GetCursorPos");
        }
        finally { CloseDesktop(desktop); }
    }
    private static DesktopEnvironmentException Error(string operation)
    { var code = Marshal.GetLastWin32Error(); return new($"{operation}: Win32 {code}, {new Win32Exception(code).Message}"); }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
}
