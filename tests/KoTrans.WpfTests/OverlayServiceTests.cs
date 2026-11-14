using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class OverlayServiceTests
{
    internal static void RestoreMain()
    {
        string directory = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate();
        var other = settings.CreateGameProfile("other");
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => [])); main.ApplyLoadedSettings(settings);
        main.AttachTray(new Tray()); var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        int captures = 0;
        var ocr = new UnconfiguredOcrService();
        var pipeline = new TranslationPipeline(ocr, new MockTranslationService(), _ => { captures++; throw new Exception("Background restore must not capture"); },
            new("en", "ADV", false), new(), new(), "synthetic");
        try {
            main.ObserveTranslation(pipeline, settings.ActiveProfileId, watch: true); pipeline.Start(false);
            var service = Named<Button>(overlay.Chrome, "OverlayService");
            foreach (bool through in new[] { true, false }) {
                overlay.SetClickThrough(through); Check(main.HideToTray(), "Tray restore fixture did not hide main"); main.WindowState = WindowState.Minimized;
                Click(service);
                Check(!main.IsInTray && main.WindowState == WindowState.Normal && pipeline.IsRunning && captures == 0
                    && Named<ListBox>(main, "ProfilePages").SelectedIndex == 2,
                    "Service button did not restore the Translation Service settings or changed translation state");
                Check(overlay.ClickThrough == through && ReferenceEquals(main.CreateOverlayWindow(), overlay),
                    "Opening main changed click-through or recreated the subtitle window");
            }
            settings.DefaultProfile.ShowTranslation = false; overlay.Apply(settings.DefaultProfile);
            Check(Equals(service.Content, "원문만 표시") && service.MinWidth == 112, "OCR-only caption was replaced with a paid service name");
            main.HideToTray(); Click(service); Check(!main.IsInTray && captures == 0
                && Named<ListBox>(main, "ProfilePages").SelectedIndex == 2,
                "OCR-only service button failed to restore Translation Service settings");
            Complete(main.SwitchProfileAsync(other)); main.HideToTray();
            Click(service); Check(main.IsInTray, "Retired overlay's service event restored main after a profile replacement");
            Check(!main.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero,
                "Background service restore test displayed a native main window");
        } finally {
            main.DisposeTray(); Complete(pipeline.DisposeAsync().AsTask()); Complete(ocr.DisposeAsync().AsTask());
            Close(overlay); Close(overlay.Chrome); Close(main); Directory.Delete(directory, true);
        }
    }
    private sealed class Tray : IApplicationTray
    {
        public bool IsAvailable { get; private set; } = true;
        public event Action? OpenRequested { add { } remove { } }
        public event Action? ExitRequested { add { } remove { } }
        public void Dispose() => IsAvailable = false;
    }
}
