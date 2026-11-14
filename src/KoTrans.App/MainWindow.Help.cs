using System.ComponentModel;
using System.Diagnostics;

namespace KoTrans.App;

public partial class MainWindow
{
    private bool openingHelp;

    private async void OpenLauncherHelp(object sender, RoutedEventArgs e) =>
        await OpenLauncherHelpAsync(AppContext.BaseDirectory, App.IsDev);

    internal async Task OpenLauncherHelpAsync(string baseDirectory, bool development, Action<ProcessStartInfo>? launch = null)
    {
        if (closing || closeApproved || openingHelp) return;
        openingHelp = true;
        try
        {
            string? file = ResolveLauncherHelp(baseDirectory, development);
            if (file is null)
            {
                ShowFeedback(FeedbackKind.Error, "도움말 파일을 찾을 수 없습니다. 설치 폴더의 help 폴더를 확인하세요.");
                return;
            }

            var info = new ProcessStartInfo { FileName = file, UseShellExecute = true };
            await Task.Run(() =>
            {
                if (launch is not null) launch(info);
                else Process.Start(info)?.Dispose();
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowFeedback(FeedbackKind.Error, "도움말을 열지 못했습니다. Windows 기본 브라우저 설정을 확인하세요.");
        }
        finally { openingHelp = false; }
    }

    internal static string? ResolveLauncherHelp(string baseDirectory, bool development)
    {
        if (!Path.IsPathFullyQualified(baseDirectory)) return null;
        var output = new DirectoryInfo(baseDirectory);
        string installed = Path.Combine(output.FullName, "help", "index.html");
        if (File.Exists(installed)) return installed;
        if (!development) return null;

        // Normal DEV-RUN output is src/KoTrans.App/bin/<configuration>/<framework>.
        var project = output.Parent?.Parent is { Name: "bin" } bin ? bin.Parent : null;
        if (project is not { Name: "KoTrans.App", Parent: { Name: "src", Parent: { } root } }
            || !File.Exists(Path.Combine(project.FullName, "KoTrans.App.csproj"))
            || !File.Exists(Path.Combine(root.FullName, "KoTrans.slnx")))
            return null;

        string source = Path.Combine(root.FullName, "docs", "help", "index.html");
        return File.Exists(source) ? source : null;
    }
}
