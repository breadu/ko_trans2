using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class RecordingModeTests
{
    private static string[] Flags(string variant) => variant switch {
        "Normal" => [], "Debug" => ["--debug-overlay"], "Recording" => ["--recording"],
        "RecordingDebug" => ["--recording", "--debug-overlay"], _ => throw new ArgumentException(variant)
    };
    internal static void Policy()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTransRecordingArgs-" + Guid.NewGuid().ToString("N"));
        foreach (var mode in new[] { "Normal", "Debug", "Recording", "RecordingDebug" }) {
            var launch = AppLaunchOptions.Parse(Flags(mode), root);
            bool recording = mode.StartsWith("Recording"), debug = mode.Contains("Debug");
            Check(launch.Recording == recording && launch.DebugOverlay == debug && launch.DataDirectory == root && !launch.MockAi,
                "Capture options changed another launch option");
            Check(CaptureExclusionPolicy.NeedsExclusion(debug, false, recording) == (!recording && !debug)
                && CaptureExclusionPolicy.NeedsExclusion(debug, true, recording) == !recording,
                "Normal/debug/recording capture matrix differs from the launch contract");
            var isolated = AppLaunchOptions.Parse(Flags(mode).Concat(new[] { "--test-data", root + "-qa", "--mock-ai" }).ToArray(), root);
            Check(isolated.Recording == recording && isolated.DebugOverlay == debug && isolated.MockAi,
                "Recording did not compose with isolated settings/mock AI");
            Check(MainWindow.NeedsCaptureOverlapCheck(true, false, false, captureExcluded: !recording) == recording,
                "Recording subtitles bypassed overlap safety");
        }
        Check(!Directory.Exists(root) && !Directory.Exists(root + "-qa"), "Parsing recording options touched settings");
        KoTrans.App.App.InitializeCapturePolicy(AppLaunchOptions.Parse([], root));
        bool rejected = false;
        try { KoTrans.App.App.InitializeCapturePolicy(AppLaunchOptions.Parse(["--recording", "--debug-overlay"], root)); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !KoTrans.App.App.RecordingMode && !KoTrans.App.App.DebugCaptureMode, "Capture policy changed after startup");
        Check(KoTrans.App.App.CaptureModeRestartNotice(false, true)?.Contains("--recording") == true
            && KoTrans.App.App.CaptureModeRestartNotice(true, true)?.Contains("완전히 종료") == true
            && KoTrans.App.App.CaptureModeRestartNotice(false, false) is null, "Secondary launch lacks restart guidance");
    }

    internal static void Desktop(string variant)
    {
        string root = FeedbackTests.DirectoryPath();
        var launch = AppLaunchOptions.Parse(Flags(variant), root);
        KoTrans.App.App.InitializeCapturePolicy(launch);
        var settings = new AppSettings(); settings.Validate(); settings.StudyTopmost = true;
        var profile = settings.DefaultProfile; profile.Name = "Synthetic recording test"; profile.OverlayClickThrough = true;
        var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => [])); main.ApplyLoadedSettings(settings);
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        var study = main.EnsureStudy(); Track(study);
        var wizard = Track(new FirstRunWizardWindow(main));
        var popup = new Popup { AllowsTransparency = true, Placement = PlacementMode.AbsolutePoint,
            Child = new Border { Background = Brushes.Gold, Width = 220, Height = 70, Child = new TextBlock { Text = "합성 팝업 가림 검사" } } };
        try {
            main.Show(); Pump(); Affinity(main, false); main.Hide();
            Check(main.DebugWindow is null, "Recording automatically created a debug window");
            overlay.SetResult("Synthetic source for recording.", "녹화 모드 자막 표시 확인", "");
            overlay.Show(); Pump(); overlay.Left = SystemParameters.WorkArea.Left + 70; overlay.Top = SystemParameters.WorkArea.Top + 100; Pump();
            Affinity(overlay, !launch.Recording); Affinity(overlay.Chrome, !launch.Recording);
            Check(overlay.CaptureExcluded == !launch.Recording && overlay.Chrome.CaptureExcluded == !launch.Recording,
                "Subtitle/chrome exclusion state disagrees with native affinity");
            Check(overlay.ClickThrough && overlay.Chrome.IsVisible && overlay.Chrome.InputAvailable, "Recording changed click-through or toolbar input");
            ExpectOverlap(main, overlay, launch.Recording);
            ExpectOverlap(main, overlay.Chrome, launch.Recording);
            overlay.Hide(); Pump();

            study.SetSource("A synthetic sentence.", "en"); study.Render(WpfTests.Example("문장 해설도 녹화할 수 있습니다."));
            study.ShowReader(); Pump(); study.Left = SystemParameters.WorkArea.Left + 450; study.Top = SystemParameters.WorkArea.Top + 80; Pump();
            Affinity(study, !launch.Recording && !launch.DebugOverlay);
            Check(study.CaptureExcluded == (!launch.Recording && !launch.DebugOverlay), "Study exclusion status is stale");
            ExpectOverlap(main, study, launch.Recording || launch.DebugOverlay);
            study.Close(); study.ShowReader(); Pump(); Affinity(study, !launch.Recording && !launch.DebugOverlay); study.Hide();

            wizard.Show(); Pump(); Affinity(wizard, !launch.Recording && !launch.DebugOverlay);
            Check(wizard.CaptureExcluded == (!launch.Recording && !launch.DebugOverlay), "Wizard reports failed/active exclusion incorrectly");
            wizard.Hide();
            if (launch.DebugOverlay) {
                Complete(main.EnableDebugOverlayAsync(root)); var debug = Track(main.DebugWindow!);
                Affinity(debug, false); Check(!debug.CaptureExcluded, "Debug + recording excluded the debug window"); debug.Hide();
            }
            Check(!launch.DebugOverlay || main.DebugWindow is not null, "Combined launch lost debug behavior");

            if (launch.Recording) {
                popup.HorizontalOffset = SystemParameters.WorkArea.Left + 100; popup.VerticalOffset = SystemParameters.WorkArea.Top + 150;
                popup.IsOpen = true; Pump();
                var source = (HwndSource)PresentationSource.FromVisual(popup.Child)!;
                Check(WindowTargets.CurrentProcessWindows().Contains(source.Handle), "Native popup missing from recording protection");
                var bounds = WindowTargets.Bounds(source.Handle)!.Value;
                bool blocked = false;
                try { main.EnsureCaptureSafe(bounds); } catch (CaptureUnavailableException ex) { blocked = ex.Message.Contains("팝업 창") && ex.IsExpectedWait; }
                Check(blocked, "Recording native popup entered OCR capture"); popup.IsOpen = false; Pump();
                main.EnsureCaptureSafe(bounds);
            }
            var store = new SettingsStore(root); Complete(store.SaveAsync(settings)); var loaded = store.LoadAsync(); Complete(loaded);
            Check(loaded.Result.DefaultProfile.Name == profile.Name && loaded.Result.DefaultProfile.OverlayClickThrough
                && !JsonSerializer.Serialize(loaded.Result).Contains("Recording"), "Recording changed persisted settings/profile schema");
            Console.WriteLine($"Native policy: {variant}; subtitle/chrome={(launch.Recording ? "WDA_NONE" : "WDA_EXCLUDEFROMCAPTURE")}; study/wizard={(launch.Recording || launch.DebugOverlay ? "WDA_NONE" : "WDA_EXCLUDEFROMCAPTURE")}; overlap, popup, click-through, show/hide and settings PASS.");

            // Optional explicit visual/OBS observation; test-only rendezvous outside product settings.
            string? hold = Environment.GetEnvironmentVariable("KOTRANS_RECORDING_TEST_HOLD");
            if (!string.IsNullOrEmpty(hold)) {
                overlay.Show(); study.ShowReader(); Pump(); File.WriteAllText(hold + ".ready", variant);
                var deadline = DateTime.UtcNow.AddMinutes(8);
                while (!File.Exists(hold + ".done") && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(30); }
            }
        } finally {
            popup.IsOpen = false;
            if (main.DebugWindow is { } debug) Close(debug);
            Close(wizard); Close(study); Close(overlay);
            main.RequestApplicationExit();
            Until(() => !Application.Current.Windows.Cast<Window>().Contains(main), "Recording main cleanup timed out");
            Close(main);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void ExpectOverlap(MainWindow main, Window window, bool expected)
    {
        var bounds = WindowTargets.Bounds(new WindowInteropHelper(window).Handle)!.Value;
        var inner = new PixelRect(bounds.X + 5, bounds.Y + 5, Math.Min(100, bounds.Width - 10), Math.Min(30, bounds.Height - 10));
        bool blocked = false;
        try { main.EnsureCaptureSafe(inner); } catch (CaptureUnavailableException ex) { blocked = ex.IsExpectedWait; }
        Check(blocked == expected, "Native OCR overlap behavior disagrees with capture exclusion");
    }
    private static void Affinity(Window window, bool excluded)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowDisplayAffinity(handle, out uint affinity)) throw new DesktopEnvironmentException("GetWindowDisplayAffinity unavailable.");
        Check(affinity == (excluded ? 0x11u : 0u), $"Unexpected capture affinity: {window.GetType().Name} {affinity:X}");
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
}
