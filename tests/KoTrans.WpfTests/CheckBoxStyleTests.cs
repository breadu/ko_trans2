using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class CheckBoxStyleTests
{
    internal static void SharedStyleAndOverlaySwitches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.CheckStyles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new AppSettings(); settings.Validate();
        var vault = new ApiKeyStore(directory);
        var main = Track(new MainWindow(new SettingsStore(directory), vault, () => []));
        main.ApplyLoadedSettings(settings);
        var editor = Track(new TranslationSettingsWindow(settings.DefaultProfile, settings.Hotkeys, vault));
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        try
        {
            main.ShowProfileSettingsPage(3);
            Layout(main, 1060, 740); Layout(editor, 650, 740);
            var shared = (Style)Application.Current.FindResource(typeof(CheckBox));
            var mainChecks = Elements<CheckBox>(main).ToArray();
            var editorChecks = Elements<CheckBox>(editor).ToArray();
            var overlayChecks = Elements<CheckBox>(overlay.Chrome).ToArray();
            Check(mainChecks.Length >= 9 && editorChecks.Length >= 1 && overlayChecks.Length == 2,
                "Shared check style test did not reach profile, editor and overlay controls");
            foreach (var option in mainChecks.Concat(editorChecks).Concat(overlayChecks))
                Check(ReferenceEquals(option.Style, shared),
                    "CheckBox bypassed the shared application style: " + option.Name);

            foreach (var host in new Window[] { main, editor }) {
                var option = Named<CheckBox>(host, "DictionaryEnabled");
                double contrast = Contrast(((SolidColorBrush)option.Foreground).Color, ((SolidColorBrush)host.Background).Color);
                Console.WriteLine($"Checkbox contrast {host.GetType().Name}: {contrast:F2}");
                Check(contrast >= 4.5, "Shared checkbox text is unreadable on its host background: " + host.GetType().Name);
            }

            var dictionary = Named<CheckBox>(main, "WordDictionaryOption");
            var toggle = Named<CheckBox>(overlay.Chrome, "OverlayWordDictionary");
            Check(!dictionary.IsEnabled && dictionary.Opacity == .45 && !toggle.IsEnabled && toggle.Opacity == .45,
                "Common disabled appearance no longer matches the overlay dictionary switch");
            foreach (var option in overlayChecks)
                Check(option.Template.VisualTree is not null
                    && DependencyPropertyHelper.GetValueSource(option, Control.TemplateProperty).BaseValueSource == BaseValueSource.Local,
                    "Overlay click-through/dictionary switch lost its custom toggle shape");
        }
        finally { Close(overlay); Close(overlay.Chrome); Close(editor); Close(main); Directory.Delete(directory, true); }
    }
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c) {
            static double Linear(byte value) { double s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
