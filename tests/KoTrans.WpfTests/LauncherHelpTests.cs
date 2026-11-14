using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class LauncherHelpTests
{
    internal static void Run()
    {
        string directory = FeedbackTests.DirectoryPath();
        var window = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => []));
        try
        {
            window.ApplyLoadedSettings(new AppSettings());
            TestLayout(window);
            TestOpening(window, directory);
        }
        finally { Close(window); Directory.Delete(directory, true); }
    }

    private static void TestLayout(MainWindow window)
    {
        var card = Named<Border>(window, "LauncherTranslationCard");
        var mascot = Named<Border>(window, "LauncherMascotCard");
        var first = Named<Button>(window, "FirstRunWizardButton");
        var hide = Named<Button>(window, "HideSettingsButton");
        var help = Named<Button>(window, "LauncherHelpButton");
        var start = Named<Button>(window, "WatchButton");
        Rect Bounds(FrameworkElement element) => FeedbackTests.Bounds(window, element);
        hide.Content = MainWindow.HideSettingsCaption(true, false, "F1");
        Check(Equals(help.Content, "도움말") && help.Style == hide.Style && help.MinHeight == hide.MinHeight,
            "Help must use the same quiet style and height as the original actions");

        foreach (double dpi in new[] { 1d, 1.25d, 1.5d, 2.5d })
        {
            window.ApplyDisplayLayout(new DisplayArea("help-test", "help-test", new(0, 0, 3072, 2520), dpi));
            foreach (double width in new[] { 820d, 761d, 760d, 759d, 640d, 400d, 360d, 1060d, 820d, 360d, 1060d })
            {
                help.Visibility = Visibility.Collapsed;
                Layout(window, width, 485);
                var originalStart = Bounds(start);
                help.Visibility = Visibility.Visible;
                Layout(window, width, 485);
                double min = window.MinHeight;
                Layout(window, width, Math.Max(485, min));
                var left = Bounds(first);
                var tray = Bounds(hide);
                var right = Bounds(help);
                var translation = Bounds(card);
                Check(new[] { left, tray, right }.All(b => b.Width > 0 && b.Height >= 38 &&
                    b.Left >= translation.Left - .1 && b.Right <= translation.Right + .1 &&
                    b.Bottom <= Math.Max(485, min) + .1), $"Help actions were clipped at {width} DIP/{dpi}");
                Check(Math.Abs(right.Right - translation.Right) < .1 && !left.IntersectsWith(tray)
                    && !right.IntersectsWith(left) && !right.IntersectsWith(tray),
                    $"Help alignment or action overlap at {width} DIP/{dpi}");
                Check(mascot.Visibility == (width < 760 ? Visibility.Collapsed : Visibility.Visible) &&
                    (mascot.Visibility == Visibility.Collapsed || !right.IntersectsWith(Bounds(mascot))),
                    "Help actions intersected the mascot or changed the narrow breakpoint");
                Check(Math.Abs(originalStart.X - Bounds(start).X) < .1 && Math.Abs(originalStart.Y - Bounds(start).Y) < .1
                    && originalStart.Size == Bounds(start).Size, "Help changed the translation start button bounds");
                if (width == 360)
                    Check(right.Top >= tray.Bottom, "Help should wrap below the full tray caption on a narrow window");
                if (width == 1060 || width == 640)
                    Check(Math.Abs(right.Top - left.Top) < .1, "Help did not return to the first row");
                Layout(window, width, Math.Max(485, min));
                Check(window.MinHeight == min && Bounds(help) == right, "Help layout did not settle after resize");
            }
        }
    }

    private static void TestOpening(MainWindow window, string directory)
    {
        var root = Path.Combine(directory, "이동한 폴더");
        var packaged = Directory.CreateDirectory(Path.Combine(root, "배포 앱", "help")).FullName;
        var dev = Directory.CreateDirectory(Path.Combine(root, "src", "KoTrans.App", "bin", "Release", "net10.0-windows")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "docs", "help")).FullName;
        File.WriteAllText(Path.Combine(root, "KoTrans.slnx"), "");
        File.WriteAllText(Path.Combine(root, "src", "KoTrans.App", "KoTrans.App.csproj"), "");
        var installedPage = Path.Combine(packaged, "index.html");
        var sourcePage = Path.Combine(source, "index.html");
        File.WriteAllText(installedPage, "help");
        File.WriteAllText(sourcePage, "help");
        string packagedOutput = Directory.GetParent(packaged)!.FullName;
        Check(MainWindow.ResolveLauncherHelp(dev, true) == sourcePage &&
            MainWindow.ResolveLauncherHelp(dev, false) is null &&
            MainWindow.ResolveLauncherHelp(packagedOutput, false) == installedPage &&
            MainWindow.ResolveLauncherHelp("relative", true) is null, "Help paths do not respect the launch location/mode");
        Directory.CreateDirectory(Path.Combine(dev, "help"));
        var stagedPage = Path.Combine(dev, "help", "index.html");
        File.WriteAllText(stagedPage, "help");
        Check(MainWindow.ResolveLauncherHelp(dev, true) == stagedPage, "A colocated help page must take precedence");

        var watch = Named<Button>(window, "WatchButton");
        var previousContent = watch.Content;
        var previousEnabled = watch.IsEnabled;
        ProcessStartInfo? request = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = window.OpenLauncherHelpAsync(packagedOutput, false, info =>
        {
            request = info;
            release.Task.GetAwaiter().GetResult();
        });
        try
        {
            Until(() => request is not null, "Help click did not request a shell launch");
            Complete(window.OpenLauncherHelpAsync(packagedOutput, false, _ => throw new Exception("Duplicate launch")));
            Check(request is { UseShellExecute: true } && request.FileName == installedPage, "Help uses a quoted command or the wrong file");
        }
        finally { release.SetResult(); Complete(launched); }
        Complete(window.OpenLauncherHelpAsync(packagedOutput, false, _ => throw new Win32Exception()));
        Check(window.FeedbackToast.Kind == FeedbackKind.Error &&
            window.FeedbackToast.Message.Contains("기본 브라우저"), "Shell failure did not show a non-blocking error");
        File.Delete(installedPage);
        int launches = 0;
        Complete(window.OpenLauncherHelpAsync(packagedOutput, false, _ => launches++));
        Check(launches == 0 && window.FeedbackToast.Message == "도움말 파일을 찾을 수 없습니다. 설치 폴더의 help 폴더를 확인하세요.",
            "A missing help file attempted to open the shell or hid the required message");
        Check(Equals(watch.Content, previousContent) && watch.IsEnabled == previousEnabled &&
            Named<Grid>(window, "LauncherView").Visibility == Visibility.Visible, "Opening help changed translation or navigation");
    }
}
