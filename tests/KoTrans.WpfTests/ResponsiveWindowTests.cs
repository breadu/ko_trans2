using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class ResponsiveWindowTests
{
    internal static void LauncherMinimumAndDisplayReturn()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.LayoutMinimum", Guid.NewGuid().ToString("N"));
        var window = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        var portrait = new DisplayArea("portrait", "portrait", new(0, 0, 1600, 2520), 2.5);
        var landscape = new DisplayArea("landscape", "landscape", new(1600, 0, 2560, 1400), 1);
        try {
            var settings = new AppSettings();
            settings.Profiles[0].GameWindow = new("synthetic", "C:/synthetic/game.exe", "GameClass", "Synthetic game");
            window.ApplyLoadedSettings(settings);
            var summary = Named<TextBlock>(window, "TranslationSummary");
            foreach (string service in new[] { "로컬 AI 번역 · 내 PC에서 처리", "로컬 AI 번역 · CPU 모드 · 번역이 느릴 수 있음", "OpenAI · " + new string('W', 200) }) {
                summary.Text = service;
                window.ApplyAvailableWindowSize(new(1536, 832));
                var chrome = DesktopWindows.ChromeSize(window);
                var root = (FrameworkElement)window.Content;
                var client = new Size(window.Width - chrome.Width, window.Height - chrome.Height);
                // Inspect the first layout before queued minimum-height corrections run.
                root.Measure(client); root.Arrange(new Rect(client)); root.UpdateLayout();
                Check(Named<Button>(window, "LaunchGameButton").Visibility == Visibility.Visible,
                    "Startup fixture must include the game launch action");
                AssertLauncherFits(window);
                double expectedHeight = service.Length <= 20 ? 20 : 40;
                Check(Math.Abs(summary.ActualHeight - expectedHeight) < .1,
                    $"Service summary should use only the lines it needs: {summary.ActualHeight} instead of {expectedHeight}");
            }
            foreach (double width in new[] { 640d, 400d, 820d }) {
                window.ApplyAvailableWindowSize(new(width, 1008));
                LayoutAtMinimum(window);
                AssertLauncherFits(window);
                // WPF constrains native resizing with MinHeight; no HWND is needed to
                // verify that its boundary contains all controls (Height itself is not coerced).
                Check(window.Height >= window.MinHeight && window.MinHeight >= 400,
                    "Launcher did not apply its measured content minimum");
                summary.Text = "Gemini · a-long-selected-model-id-that-wraps-over-multiple-lines";
                LayoutAtMinimum(window); LayoutAtMinimum(window);
                AssertLauncherFits(window);
            }
            for (int cycle = 0; cycle < 3; cycle++) {
                window.ShowLauncher();
                Check(window.ApplyDisplayLayout(portrait), "Portrait monitor transition was ignored");
                LayoutAtMinimum(window); AssertLauncherFits(window);
                Check(window.Width == 640 && Named<Border>(window, "LauncherMascotCard").Visibility == Visibility.Collapsed,
                    "Portrait monitor did not select narrow launcher");
                Check(window.ApplyDisplayLayout(landscape), "Landscape monitor transition was ignored");
                LayoutAtMinimum(window); AssertLauncherFits(window);
                Check(window.Width == 820 && Named<Border>(window, "LauncherMascotCard").Visibility == Visibility.Visible,
                    "Display move retained the portrait launcher width");
                window.Width = 790;
                Check(!window.ApplyDisplayLayout(landscape) && window.Width == 790, "Same-monitor move reset the user's window size");
                window.ShowProfileSettingsPage(2);
                window.ApplyDisplayLayout(portrait); Layout(window, 624, 700);
                window.ApplyDisplayLayout(landscape); Layout(window, 1044, 700);
                Check(window.Width == 1060 && Grid.GetRow(Named<DockPanel>(window, "ProfileContent")) == 0,
                    "Display move retained the portrait settings layout");
            }
            window.ShowLauncher(); window.ApplyAvailableWindowSize(new(400, 300)); Layout(window, 384, 262);
            Check(window.MinHeight == 300 && window.Height <= 300 && Named<ScrollViewer>(window, "LauncherScroll").ScrollableHeight > 0,
                "Physically too-small work area must remain bounded with a scroll fallback");
        }
        finally { Close(window); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    private static void LayoutAtMinimum(MainWindow window)
    {
        var chrome = DesktopWindows.ChromeSize(window);
        Layout(window, window.Width - chrome.Width, window.MinHeight - chrome.Height);
    }
    private static void AssertLauncherFits(MainWindow window)
    {
        var scroll = Named<ScrollViewer>(window, "LauncherScroll");
        Check(scroll.ScrollableHeight < 0.1 && scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible,
            $"Launcher scrolls at its minimum height: width={window.Width}, min={window.MinHeight}, overflow={scroll.ScrollableHeight}");
        var button = Named<Button>(window, "WatchButton");
        var origin = button.TranslatePoint(new Point(), scroll);
        Check(origin.Y >= 0 && origin.Y + button.ActualHeight <= scroll.ActualHeight + 0.1,
            "Auto-translation button is clipped at the minimum height");
    }
    internal static void StablePortraitTabs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.StableTabs", Guid.NewGuid().ToString("N"));
        var window = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        try {
            window.ApplyLoadedSettings(new AppSettings());
            foreach (double width in new[] { 384d, 430d, 500d, 624d, 730d }) {
                window.ApplyAvailableWindowSize(new(width + 16, 1008));
                window.ShowProfileSettingsPage(0); Layout(window, width, 800);
                var tabs = Named<ListBox>(window, "ProfilePages").Items.Cast<ListBoxItem>().ToArray();
                var before = tabs.Select(t => (Bounds: FeedbackTests.Bounds(window, t), t.FontWeight)).ToArray();
                for (int selected = 1; selected < tabs.Length; selected++) {
                    window.ShowProfileSettingsPage(selected); Layout(window, width, 800);
                    for (int i = 0; i < tabs.Length; i++)
                        Check(FeedbackTests.Bounds(window, tabs[i]) == before[i].Bounds && tabs[i].FontWeight == before[i].FontWeight,
                            $"Selecting tab {selected} changed menu geometry/weight at width {width}, item {i}");
                }
            }
        }
        finally { Close(window); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    internal static void StartupAndPortrait()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Responsive", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir); var settings = new AppSettings(); settings.Validate();
        settings.MainWindow = new() { X = -1400, Y = 300, Width = 1060, Height = 740 };
        Complete(store.SaveAsync(settings));
        var window = Track(new MainWindow(store, new ApiKeyStore(dir), () => []));
        var tray = new Tray(); window.AttachTray(tray);
        try {
            Complete(window.PrepareStartupAsync());
            Check(window.PendingStartupPlacement is { X: -1400, Y: 300, Width: 820, Height: 485 }
                && window.WindowStartupLocation == WindowStartupLocation.Manual,
                "Saved physical position was not prepared before first display");
            Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero
                && Named<Grid>(window, "Workspace").IsEnabled, "Startup required a visible HWND or left settings unloaded");
            // 1600x2560 at 250%, allowing a 40px taskbar: 640x1008 logical pixels.
            var portrait = new Size(1600 / 2.5, 2520 / 2.5);
            window.ApplyAvailableWindowSize(portrait);
            Check(window.Width <= 640 && window.Height <= 1008 && window.MinWidth <= 640, "Portrait monitor cannot contain launcher");
            Layout(window, 620, 450);
            Check(Grid.GetRow(Named<Grid>(window, "HeaderProfile")) == 1
                && Named<Border>(window, "LauncherMascotCard").Visibility == Visibility.Collapsed, "Narrow launcher did not reflow");
            AssertHeader(window);
            for (int page = 0; page < 8; page++) {
                window.ShowProfileSettingsPage(page); Layout(window, 620, 700);
                Check(window.Width <= 640 && window.Height <= 1008, "Settings expanded beyond portrait work area");
                Check(Grid.GetRow(Named<DockPanel>(window, "ProfileContent")) == 1
                    && Grid.GetColumn(Named<Border>(window, "ProfileNavigation")) == 1,
                    "Profile navigation stayed beside narrow content");
                AssertHeader(window);
                var content = Named<DockPanel>(window, "ProfileContent");
                var origin = content.TranslatePoint(new Point(), (UIElement)window.Content);
                Check(origin.Y > 0 && origin.Y + content.ActualHeight <= 701, "Profile content or save actions extend beyond available height");
                var actions = Named<WrapPanel>(window, "ProfileSaveActions");
                var actionOrigin = actions.TranslatePoint(new Point(), (UIElement)window.Content);
                Check(actions.ActualHeight > 0 && actionOrigin.Y + actions.ActualHeight <= 701,
                    "Settings save/navigation buttons were clipped on portrait screen");
                if (page is 2 or 4 or 5 or 7) {
                    var editor = (TranslationSettingsEditor)Named<ContentControl>(window, "ProfileEditorHost").Content;
                    var scroll = (ScrollViewer)editor.Content;
                    Check(scroll.ViewportHeight > 0 && scroll.ScrollableWidth == 0, "Narrow settings fields have no usable scroll viewport");
                    if (page == 7) {
                        Check(scroll.ScrollableHeight > 0, "Long common settings escaped the available height instead of scrolling");
                        scroll.ScrollToBottom(); Layout(window, 620, 700);
                        Check(scroll.VerticalOffset > 0, "Common settings cannot reach lower hotkey fields");
                    }
                }
            }
            for (int cycle = 0; cycle < 5; cycle++) {
                window.ShowProfileSettingsPage(cycle); Layout(window, 620, 700);
                Check(window.HideToTray(), "Settings could not hide through tray policy");
                window.RestoreMainWindow(); window.ShowLauncher(); Layout(window, 620, 450);
                Check(!window.IsInTray && Equals(Named<Button>(window, "ProfileSettingsButton").Content, "프로필 설정"), "Tray/settings round trip kept stale button content");
                AssertHeader(window);
            }
            window.ApplyAvailableWindowSize(new(1920, 1040));
            window.ShowProfileSettingsPage(3); Layout(window, 1040, 700);
            Check(window.Width == 1060 && window.Height == 740
                && Grid.GetRow(Named<Grid>(window, "HeaderProfile")) == 0
                && Grid.GetRow(Named<DockPanel>(window, "ProfileContent")) == 0,
                "Returning to landscape failed to restore normal layout");
            window.ShowLauncher(); Layout(window, 800, 450);
            Check(window.Width == 820 && Named<Border>(window, "LauncherMascotCard").Visibility == Visibility.Visible,
                "Landscape launcher failed to restore mascot or compact size");
            AssertHeader(window);
            window.ApplyAvailableWindowSize(new(400, 620)); Layout(window, 384, 580); AssertHeader(window);
            Check(window.Width <= 400 && window.Height <= 620, "Small work area was exceeded");
        }
        finally { window.DisposeTray(); Close(window); Directory.Delete(dir, true); }
    }
    private static void AssertHeader(MainWindow window)
    {
        var button = Named<Button>(window, "ProfileSettingsButton");
        var root = (FrameworkElement)window.Content;
        var point = button.TranslatePoint(new Point(), root);
        var text = new FormattedText((string)button.Content, System.Globalization.CultureInfo.GetCultureInfo("ko-KR"),
            FlowDirection.LeftToRight, new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.White, 1);
        Check(button.ActualWidth >= text.WidthIncludingTrailingWhitespace + button.Padding.Left + button.Padding.Right,
            "Profile settings button clipped its full label");
        Check(point.X >= 0 && point.X + button.ActualWidth <= root.ActualWidth + 1,
            "Profile settings button overflowed header");
    }
    internal static void BrokenStartup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.BrokenStartup", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{broken}");
        var window = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        try {
            Complete(window.PrepareStartupAsync());
            Check(!Named<Grid>(window, "Workspace").IsEnabled && window.PendingStartupPlacement is not null,
                "Corrupt settings produced an enabled translator or no safe initial placement");
            Close(window);
            Check(File.ReadAllText(Path.Combine(dir, "settings.json")) == "{broken}", "Failed startup overwrote original settings");
        } finally { Close(window); Directory.Delete(dir, true); }
    }
    private sealed class Tray : IApplicationTray
    {
        public bool IsAvailable => true;
        public event Action? OpenRequested { add { } remove { } }
        public event Action? ExitRequested { add { } remove { } }
        public void Dispose() { }
    }
}
