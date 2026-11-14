using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class WindowResizeDesktopTests
{
    internal static void Run()
    {
        var display = DesktopWindows.Displays().Where(d => !d.Primary)
            .OrderByDescending(d => d.WorkArea.Height > d.WorkArea.Width).FirstOrDefault()
            ?? throw new DesktopEnvironmentException("No secondary monitor found.");
        if (!GetCursorPos(out var mouse)) throw new DesktopEnvironmentException("GetCursorPos failed.");
        var foreground = GetForegroundWindow();
        string directory = Path.Combine(Path.GetTempPath(), "KoTrans2.WindowResize", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => []));
        main.SuppressDesktopStartupForTest();
        var settings = new AppSettings(); settings.Validate(); main.ApplyLoadedSettings(settings);
        try {
            main.Topmost = true;
            main.Show(); Until(() => main.IsLoaded, "Main window did not load.");
            DesktopWindows.ApplyPlacement(main, new() { X = display.WorkArea.X + 50, Y = display.WorkArea.Y + 70,
                Width = 560, Height = 650, MonitorId = display.Id, MonitorDevice = display.Device });
            Complete(Task.Delay(250));
            int gaps = StudyResizeDesktopTests.InspectFrameAndCursor("main", new WindowInteropHelper(main).Handle);
            Check(gaps == 0, "Main native frame still contains non-sizing gaps.");
            Console.WriteLine($"Main fixed gaps={gaps}");
            main.ShowProfileSettingsPage(3); Complete(Task.Delay(150));
            Check(StudyResizeDesktopTests.InspectFrameAndCursor("main-profile-settings", new WindowInteropHelper(main).Handle) == 0,
                "Profile page lost the main window resize correction.");
            main.ShowLauncher(); Complete(Task.Delay(150));
            Check(StudyResizeDesktopTests.InspectFrameAndCursor("main-returned-launcher", new WindowInteropHelper(main).Handle) == 0,
                "Returning to launcher lost the main window resize correction.");
            main.Hide();
            CheckBrowser(directory, display);
        } finally {
            main.RequestApplicationExit(); Until(() => !Application.Current.Windows.Cast<Window>().Contains(main), "Main cleanup timed out.");
            Close(main);
            SetCursorPos(mouse.X, mouse.Y); if (foreground != IntPtr.Zero) SetForegroundWindow(foreground);
            // Delete only this fixture's generated GUID directory, never the user's browser profile.
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KoTrans2.WindowResize")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture path escaped temp root.");
            try { Directory.Delete(directory, true); } catch (IOException) { Console.WriteLine("Temporary browser profile still locked; retained: " + directory); }
        }
    }
    private static void CheckBrowser(string directory, DisplayArea display)
    {
        string profile = Path.Combine(directory, "browser"); Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "dictionary-start.html"), "<!doctype html><meta charset=utf-8><title>KO Trans 2 Dictionary resize test</title><p>Local synthetic dictionary page. No online lookup.</p>");
        var bounds = new DictionaryPopupBounds(display.WorkArea.X + 50, display.WorkArea.Y + 70,
            Math.Min(1400, display.WorkArea.Width - 100), Math.Min(1600, display.WorkArea.Height - 100));
        var launch = DictionaryBrowser.CreateStartInfo(profile, DictionaryBrowserCoordinates.Convert(bounds, DictionaryPopupPlacement.Displays()));
        launch.ArgumentList.Add("--disable-background-networking"); launch.ArgumentList.Add("--disable-component-update");
        using var process = Process.Start(launch) ?? throw new DesktopEnvironmentException("Dictionary browser could not start.");
        DictionaryConnection? connection = null; IntPtr popup = IntPtr.Zero;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
            var clock = Stopwatch.StartNew();
            while (connection is null) {
                if (clock.Elapsed > TimeSpan.FromSeconds(28)) throw new DesktopEnvironmentException("Isolated dictionary browser startup timed out.");
                var connect = DictionaryConnection.TryConnectAsync(profile, timeout.Token); Complete(connect); connection = connect.Result;
                if (connection is null) Complete(Task.Delay(100));
            }
            var info = connection.SendAsync("SystemInfo.getProcessInfo", null, timeout.Token); Complete(info);
            int pid = info.Result.GetProperty("processInfo").EnumerateArray().First(p => p.GetProperty("type").GetString() == "browser").GetProperty("id").GetInt32();
            Until(() => DictionaryPopupPlacement.Present(pid, bounds, out popup), "Isolated dictionary app window unavailable.");
            Complete(Task.Delay(300));
            Console.WriteLine("Dictionary browser executable=" + Path.GetFileName(launch.FileName) + "; isolated temporary profile, local file page only.");
            int gaps = StudyResizeDesktopTests.InspectFrameAndCursor("dictionary-browser", popup);
            Console.WriteLine($"Dictionary browser gaps={gaps}");
        } finally {
            // Close only the isolated browser created with this unique --user-data-dir.
            if (connection is not null) {
                try { using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); Complete(connection.SendAsync("Browser.close", null, closeTimeout.Token)); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
                connection.Dispose();
            }
            if (popup != IntPtr.Zero && IsWindow(popup)) { PostMessage(popup, 0x10, IntPtr.Zero, IntPtr.Zero); Until(() => !IsWindow(popup), "Isolated dictionary window did not close normally."); }
            Complete(Task.Delay(250));
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
