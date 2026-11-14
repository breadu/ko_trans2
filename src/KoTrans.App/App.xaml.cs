using System.Diagnostics;
namespace KoTrans.App;

public partial class App : System.Windows.Application
{
    private InstanceActivation? instance;
    private bool windowReady;
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoTrans2");
    public static bool IsDev { get; private set; }
    public static bool MockAi { get; private set; }
    // Set from the explicit --debug-overlay launch flag before any window is created.
    internal static bool DebugCaptureMode { get; private set; }
    public static double ProcessElapsedMs => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
    protected override async void OnStartup(StartupEventArgs e)
    {
        bool debugRequested = false;
        base.OnStartup(e);
        IsDev = e.Args.Contains("--dev-run") || AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);
        // Validate before opening/creating any settings directory or single-instance lock.
        try
        {
            var launch = KoTrans.Core.AppLaunchOptions.Parse(e.Args, DataDirectory);
            DataDirectory = launch.DataDirectory; MockAi = launch.MockAi;
            debugRequested = launch.DebugOverlay;
            DebugCaptureMode = debugRequested;
        }
        catch (ArgumentException ex)
        { MessageBox.Show(ex.Message, "KO Trans 2 · 실행 인자 오류"); Shutdown(2); return; }
        try
        {
            instance = InstanceActivation.TryOwn(DataDirectory, () => Dispatcher.BeginInvoke(new Action(() => {
                if (windowReady && MainWindow is KoTrans.App.MainWindow existing) existing.RestoreMainWindow();
            })));
            if (instance is null) {
                bool activated = await InstanceActivation.RequestAsync(DataDirectory, pid => AllowSetForegroundWindow(pid));
                if (!activated) MessageBox.Show("실행 중인 KO Trans 2 창을 열지 못했습니다. 알림 영역 아이콘의 ‘열기’를 선택하세요. 설정 폴더 접근 권한도 확인해 주세요.", "KO Trans 2");
                else if (debugRequested) MessageBox.Show("디버그 창은 새 실행에서만 사용할 수 있습니다. 알림 영역 아이콘 우클릭 → 종료로 KO Trans 2를 완전히 종료한 뒤 디버그 옵션으로 다시 실행하세요.", "KO Trans 2 · DEBUG");
                Shutdown(activated ? 0 : 2); return;
            }
        }
        catch (IOException)
        {
            MessageBox.Show("KO Trans 2 설정 폴더를 사용할 수 없습니다. 폴더 접근 권한을 확인해 주세요.", "KO Trans 2");
            Shutdown(2); return;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("KO Trans 2 설정 폴더에 쓰기 권한이 없습니다.", "KO Trans 2"); Shutdown(2); return;
        }
        var window = new MainWindow(); MainWindow = window;
        // Settings are small and asynchronous. Apply them and register pre-show placement
        // before creating the visible HWND; OCR/voice/dictionary engines remain lazy.
        await window.PrepareStartupAsync();
        window.Show(); windowReady = true;
        if (debugRequested) await window.EnableDebugOverlayAsync(DataDirectory);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    protected override void OnExit(ExitEventArgs e) { (MainWindow as KoTrans.App.MainWindow)?.DisposeTray(); instance?.Dispose(); base.OnExit(e); }
}
