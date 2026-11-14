using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class UiPolishTests
{
    internal static void SpacingAndDisabledButtons()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.UiPolish", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings = new AppSettings(); settings.Validate();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(settings);
        try
        {
            main.ShowProfileSettingsPage(0); Layout(main, 1060, 900);
            var rename = Named<Button>(main, "RenameProfileButton");
            var delete = Named<Button>(main, "DeleteProfileButton");
            Check(!rename.IsEnabled && !delete.IsEnabled, "Default profile protection changed");
            CheckDarkSurface(rename); CheckDarkSurface(delete);
            var disabledText = OverlayActivityTests.Visuals<Border>(rename).First();
            Check(System.Windows.Documents.TextElement.GetForeground(disabledText) is SolidColorBrush ink
                && ink.Color.R < 180 && ink.Color.G < 180 && ink.Color.B < 180,
                "Disabled button has bright normal-state ink");
            Check(Named<Slider>(main, "OriginalFontSlider").Value == 21, "Fresh profile slider did not load 21 DIP");
            var checkedButtons = new HashSet<Button>();
            foreach (int page in new[] { 0, 1, 2, 3, 4, 5, 6 })
            {
                main.ShowProfileSettingsPage(page); Layout(main, 1060, 900);
                foreach (var button in Elements<Button>(main).Where(checkedButtons.Add).ToArray()) VerifyDisabledAndRestore(button);
            }
            main.ShowProfileSettingsPage(4); Layout(main, 1060, 950);
            var card = Named<Border>(main, "ProfileEditorPage");
            var fields = Named<StackPanel>(main, "ProfileTriggerFields");
            var first = fields.Children.OfType<Border>().First();
            var last = Named<TextBlock>(main, "RecognitionModeHelp");
            double top = first.TranslatePoint(new Point(), card).Y;
            double bottom = card.ActualHeight - last.TranslatePoint(new Point(0, last.ActualHeight), card).Y;
            Check(Math.Abs(top - bottom) <= 2, $"Trigger card margins are asymmetric: top={top:F1}, bottom={bottom:F1}");
            Check(Named<TextBlock>(main, "SettingsStatus").Visibility == Visibility.Collapsed,
                "Empty status message still reserves bottom space");
            Layout(main, 820, 475);
            var editor = (FrameworkElement)Named<ContentControl>(main, "ProfileEditorHost").Content;
            var scroll = Elements<ScrollViewer>(editor).Single();
            Check(scroll.ScrollableHeight > 0 && card.ActualHeight < 475,
                "Content-sized card lost scrolling at a short window height");
            var game = settings.CreateGameProfile("Game"); Complete(main.SwitchProfileAsync(game));
            main.ShowProfileSettingsPage(0); Layout(main, 1060, 900);
            Check(rename.IsEnabled && delete.IsEnabled && rename.Template.FindName("DisabledButtonSurface", rename) is null,
                "Game profile retained disabled button chrome after activation");
            var picker = Track(main.CreateTargetPicker());
            try { Layout(picker, 700, 420); foreach (var b in Elements<Button>(picker)) VerifyDisabledAndRestore(b); }
            finally { Close(picker); }
            var guide = Track(new CharacterDictionaryHelpWindow());
            try { Layout(guide, 790, 650); VerifyDisabledAndRestore(Named<Button>(guide, "CloseDictionaryGuide")); }
            finally { Close(guide); }
            var region = Track(new RegionSelector(new(0, 0, 1000, 700), new RegionSelectionState(), _ => { }));
            try { Layout(region, 1000, 700); foreach (var b in Elements<Button>(region)) VerifyDisabledAndRestore(b); }
            finally { Close(region); }
            var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
            try { Layout(overlay.Chrome, 640, 280); foreach (var b in Elements<Button>(overlay.Chrome)) VerifyDisabledAndRestore(b); }
            finally { Close(overlay); Close(overlay.Chrome); }
        }
        finally { Close(main); Directory.Delete(dir, true); }
    }

    private static void VerifyDisabledAndRestore(Button button)
    {
        bool enabled = button.IsEnabled;
        button.ApplyTemplate();
        var originalTemplate = button.Template;
        button.IsEnabled = false; button.ApplyTemplate(); button.UpdateLayout();
        CheckDarkSurface(button);
        button.IsEnabled = enabled; button.ApplyTemplate(); button.UpdateLayout();
        Check(button.Template == originalTemplate, "Button template did not restore: " + button.Name);
    }
    private static void CheckDarkSurface(Button button)
    {
        button.ApplyTemplate();
        var chrome = OverlayActivityTests.Visuals<Border>(button).FirstOrDefault();
        Check(chrome?.Background is SolidColorBrush brush && brush.Color.R < 110 && brush.Color.G < 120 && brush.Color.B < 130,
            "Disabled button has light native chrome: " + button.Name + "/" + button.Content);
    }
}
