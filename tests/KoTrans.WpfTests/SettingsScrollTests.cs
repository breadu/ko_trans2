using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class SettingsScrollTests
{
    internal static void SubtitleWheel()
    {
        string dir = FeedbackTests.DirectoryPath(); int saves = 0;
        var settings = new AppSettings(); settings.Validate();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [],
            persistSettings: _ => { saves++; return Task.CompletedTask; }));
        var baseline = new ScrollViewer { Content = new Border { Height = 6000 } };
        var host = Track(new Window { Content = baseline });
        try {
            Layout(host, 500, 400); Wheel(baseline); Layout(host, 500, 400);
            double jump = baseline.VerticalOffset;
            Console.WriteLine($"Default WPF wheel: immediate offset {jump:F1} DIP; Windows lines {SystemParameters.WheelScrollLines}");

            main.ApplyLoadedSettings(settings); main.ShowProfileSettingsPage(3); Layout(main, 1060, 700);
            var page = Named<SmoothScrollViewer>(main, "SubtitlePage");
            Check(page.ScrollableHeight > 0 && !page.CanContentScroll, "Real subtitle settings have no pixel scroll range");
            double font = settings.DefaultProfile.OverlayFontSize, opacity = settings.DefaultProfile.OverlayOpacity;
            // Wheel input bubbles from the actual settings controls, not a second mock layout.
            var wheel = Wheel(Named<Slider>(main, "FontSlider"));
            Check(wheel.Handled && (SystemParameters.WheelScrollLines == 0 ? page.TargetOffset == 0 : page.TargetOffset > 0),
                "Subtitle settings did not handle descendant wheel input or respect Windows scrolling preferences");
            if (SystemParameters.ClientAreaAnimation && SystemParameters.WheelScrollLines != 0)
                Check(page.IsAnimating && page.VerticalOffset < page.TargetOffset, "Settings still jump a whole wheel notch immediately");
            Console.WriteLine($"Subtitle settings wheel: immediate {page.VerticalOffset:F1}, target {page.TargetOffset:F1} DIP; smooth={page.IsAnimating}");
            Until(() => !page.IsAnimating, "Settings wheel animation did not finish"); Layout(main, 1060, 700);
            Check(Math.Abs(page.VerticalOffset - page.TargetOffset) < 1, "Settings wheel missed its destination");
            Check(settings.DefaultProfile.OverlayFontSize == font && settings.DefaultProfile.OverlayOpacity == opacity && saves == 0,
                "Scrolling changed or saved subtitle settings");

            page.ScrollWheel(-120, 3); page.ScrollWheel(-120, 3); double target = page.TargetOffset;
            Check(target > page.VerticalOffset || page.VerticalOffset == page.ScrollableHeight || !SystemParameters.ClientAreaAnimation,
                "Rapid settings wheel input was lost");
            main.ShowProfileSettingsPage(0);
            Check(!page.IsAnimating, "Leaving the settings page retained a rendering callback");
            main.ShowProfileSettingsPage(3); Layout(main, 1060, 700);
            Check(ReferenceEquals(page, Named<SmoothScrollViewer>(main, "SubtitlePage")), "Navigation recreated the settings scroll control");
            page.ScrollWheel(-120, 3); Close(main);
            Check(!page.IsAnimating && !page.ScrollWheel(-120, 3), "Closing settings did not dispose its scroll control");
        } finally { Close(main); Close(host); Directory.Delete(dir, true); }
    }
    internal static void AllSettingsPages()
    {
        string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate();
        var other = data.CreateGameProfile("other");
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        try {
            main.ApplyLoadedSettings(data);
            SmoothScrollViewer? previous = null;
            for (int section = 0; section <= 7; section++) {
                main.ShowProfileSettingsPage(section); Layout(main, 860, 650);
                Check(previous?.IsAnimating != true, "Changing settings sections kept the previous scroll animation");
                var editor = (TranslationSettingsEditor)Named<ContentControl>(main, "ProfileEditorHost").Content;
                var viewer = section switch {
                    0 => Named<ScrollViewer>(main, "BasicPage"),
                    1 => Named<ScrollViewer>(main, "CapturePage"),
                    3 => Named<ScrollViewer>(main, "SubtitlePage"),
                    6 => Named<ScrollViewer>(main, "AdvancedPage"),
                    _ => (ScrollViewer)editor.Content
                };
                Check(viewer is SmoothScrollViewer && !viewer.CanContentScroll, $"Settings section {section} kept default wheel scrolling");
                previous = (SmoothScrollViewer)viewer; previous.ScrollWheel(-120, 3);
            }
            var retired = previous!;
            Complete(main.SwitchProfileAsync(other));
            Check(!retired.IsAnimating && !retired.ScrollWheel(-120, 3), "Profile switch retained the retired settings scroll control");
            foreach (string name in new[] { "BasicPage", "CapturePage", "SubtitlePage", "AdvancedPage" }) {
                var viewer = Named<SmoothScrollViewer>(main, name);
                viewer.ScrollWheel(-120, 3);
            }
            Close(main);
            foreach (string name in new[] { "BasicPage", "CapturePage", "SubtitlePage", "AdvancedPage" })
                Check(!Named<SmoothScrollViewer>(main, name).IsAnimating, "Settings exit left a wheel animation running");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    private static MouseWheelEventArgs Wheel(UIElement element)
    {
        var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent };
        element.RaiseEvent(e); return e;
    }
}
