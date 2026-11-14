using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class SubtitlePolishTests
{
    internal static void AlignmentAndErrorRecovery()
    {
        string dir = FeedbackTests.DirectoryPath(); var now = TimeSpan.Zero;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [], feedbackClock: () => now));
        var settings = new AppSettings(); main.ApplyLoadedSettings(settings);
        try {
            var korean = Named<CheckBox>(main, "ShowKorean"); var original = Named<CheckBox>(main, "ShowOriginal");
            foreach (var size in new[] { new Size(1060, 740), new Size(400, 620), new Size(640, 1008) }) {
                main.ApplyAvailableWindowSize(size); main.ShowProfileSettingsPage(3); Layout(main, size.Width, size.Height);
                var ko = FeedbackTests.Bounds(main, korean); var src = FeedbackTests.Bounds(main, original);
                Check(ko.Y == src.Y && ko.X < src.X, "Subtitle visibility checkboxes have different Y coordinates");
                Check(ko.Y >= FeedbackTests.Bounds(main, Named<Button>(main, "TranslationColorPicker")).Bottom
                    && src.Y >= FeedbackTests.Bounds(main, Named<Button>(main, "OriginalColorPicker")).Bottom,
                    "Visibility checkboxes are not below both color pickers");
            }
            foreach (bool koreanFirst in new[] { true, false }) {
                korean.IsChecked = original.IsChecked = true;
                var first = koreanFirst ? korean : original; var last = koreanFirst ? original : korean;
                first.IsChecked = false; last.IsChecked = false;
                Check(last.IsChecked == true && main.FeedbackToast.Kind == FeedbackKind.Error && main.FeedbackToast.Visibility == Visibility.Visible,
                    "Last subtitle visibility uncheck was not rejected with an error");
                first.IsChecked = true;
                Check(main.FeedbackToast.Visibility == Visibility.Hidden && !main.IsFeedbackTimerRunning,
                    "Corrected subtitle settings retained validation toast/timer");
                now += TimeSpan.FromSeconds(5); main.AdvanceFeedback();
                Check(main.FeedbackToast.Visibility == Visibility.Hidden && settings.DefaultProfile.ShowOriginal && settings.DefaultProfile.ShowTranslation,
                    "Old validation feedback reappeared or left invalid profile options");
            }
            korean.IsChecked = false; original.IsChecked = false;
            main.ShowFeedback(FeedbackKind.Error, "다른 작업의 오류", "이 오류는 유지해야 합니다", replaceError: true);
            korean.IsChecked = true;
            Check(main.FeedbackToast.Visibility == Visibility.Visible && main.FeedbackToast.Message == "다른 작업의 오류",
                "Subtitle checkbox correction dismissed another operation's error");
            korean.IsChecked = false; original.IsChecked = false;
            Check(main.FeedbackToast.Message == "다른 작업의 오류" && original.IsChecked == true,
                "Subtitle validation covered another persistent error or allowed both channels off");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void ButtonInteractionContrast()
    {
        string dir = FeedbackTests.DirectoryPath();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(new AppSettings());
        try {
            main.ShowProfileSettingsPage(3); Layout(main, 1060, 740);
            foreach (var button in new[] { Button(main, "메인으로"), Named<Button>(main, "SaveSettingsButton"),
                Named<Button>(main, "OverlayVisibilityButton"), Named<Button>(main, "TranslationColorPicker"), Named<Button>(main, "ProfileSettingsButton") }) {
                button.ApplyTemplate(); var surface = (Border)button.Template.FindName("ButtonSurface", button);
                var presenter = (ContentPresenter)button.Template.FindName("ButtonCaption", button);
                var normal = ((SolidColorBrush)surface.Background).Color;
                var originalBrush = ((SolidColorBrush)button.Background).Color;
                var bounds = FeedbackTests.Bounds(main, button);
                foreach (var state in new[] { "MouseOver", "Pressed", "Focused" }) {
                    VisualStateManager.GoToState(button, "Normal", false); VisualStateManager.GoToState(button, "Unfocused", false);
                    Check(VisualStateManager.GoToState(button, state, false), "Button missing framework interaction state: " + state);
                    var expectedBackground = state == "Pressed" ? Color.FromRgb(0x20, 0x3E, 0x55) : Color.FromRgb(0x31, 0x55, 0x71);
                    Until(() => ((SolidColorBrush)surface.Background).Color == expectedBackground
                        && ((SolidColorBrush)TextElement.GetForeground(presenter)).Color == Colors.White,
                        "Button interaction visual state did not settle: " + state);
                    button.UpdateLayout();
                    var background = ((SolidColorBrush)surface.Background).Color;
                    var foreground = ((SolidColorBrush)TextElement.GetForeground(presenter)).Color;
                    Check(Contrast(background, foreground) >= 4.5, "Low button interaction contrast: " + state + "/" + button.Content);
                    foreach (var text in OverlayActivityTests.Visuals<TextBlock>(presenter))
                        if (!string.IsNullOrEmpty(text.Text) && text.Visibility == Visibility.Visible)
                            Check(Contrast(background, ((SolidColorBrush)text.Foreground).Color) >= 4.5,
                                "Rendered button text lost contrast in " + state);
                    Check(FeedbackTests.Bounds(main, button) == bounds, "Button state changed layout size");
                }
                VisualStateManager.GoToState(button, "Normal", false); VisualStateManager.GoToState(button, "Unfocused", false); Pump();
                // WPF removes state storyboard clocks on its next rendering tick.
                Until(() => ((SolidColorBrush)surface.Background).Color == normal, "Normal button visual state did not settle");
                Check(((SolidColorBrush)surface.Background).Color == normal && ((SolidColorBrush)button.Background).Color == originalBrush,
                    $"Interaction state overwrote normal background/color swatch: {button.Name}/{button.Content}; surface={((SolidColorBrush)surface.Background).Color}, normal={normal}; button={((SolidColorBrush)button.Background).Color}, original={originalBrush}");
            }
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    private static double Contrast(Color a, Color b)
    {
        static double L(Color c) {
            static double Linear(byte channel) { double s = channel / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
