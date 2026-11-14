using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class ComboBoxStyleTests
{
    internal static void SharedAcrossScreens()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.ComboStyles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new AppSettings(); settings.Validate();
        var vault = new ApiKeyStore(directory);
        var main = Track(new MainWindow(new SettingsStore(directory), vault, () => []));
        main.ApplyLoadedSettings(settings);
        var standalone = Track(new TranslationSettingsWindow(settings.DefaultProfile, settings.Hotkeys, vault));
        var wizard = Track(new FirstRunWizardWindow(main));
        try
        {
            main.ShowProfileSettingsPage(7);
            Layout(main, 1060, 740); Layout(standalone, 650, 740);
            var shared = (Style)Application.Current.FindResource(typeof(ComboBox));
            var rows = (Style)Application.Current.FindResource("UiComboItem");
            var mainChoices = Elements<ComboBox>(main).ToArray();
            var standaloneChoices = Elements<ComboBox>(standalone).ToArray();
            Check(mainChoices.Length >= 14 && standaloneChoices.Length >= 7,
                "Shared-style regression did not reach main, embedded and standalone editor choices");
            var choices = mainChoices.Concat(standaloneChoices).Concat(new[] {
                wizard.ProfileChoice, wizard.SourceLanguageChoice, wizard.GameChoice
            });
            foreach (var choice in choices)
            {
                Check(ReferenceEquals(choice.Style, shared) && ReferenceEquals(choice.ItemContainerStyle, rows),
                    "ComboBox bypassed the shared application/row style: " + choice.Name);
                Check(choice.MinHeight == shared.Setters.OfType<Setter>()
                    .Where(s => s.Property == FrameworkElement.MinHeightProperty).Select(s => (double)s.Value).Single(),
                    "ComboBox overrode the common control height: " + choice.Name);
            }

            // The provider's display template must override the editor's white TextBlock ink.
            var provider = Named<ComboBox>(main, "Provider");
            var presenter = new ContentPresenter { Content = TranslationProvider.HyMtLocal, ContentTemplate = provider.ItemTemplate };
            var sample = Track(new Window { Content = presenter });
            sample.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock)) {
                Setters = { new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.White) }
            };
            try
            {
                Layout(sample, 240, 80);
                var caption = OverlayActivityTests.Visuals<TextBlock>(presenter).Single();
                Check(caption.Text == "로컬 AI 번역"
                    && Equals(caption.Foreground, Application.Current.FindResource("ComboBoxInk")),
                    "Custom provider caption lost its label or shared dark ink");
            }
            finally { Close(sample); }
        }
        finally { Close(wizard); Close(standalone); Close(main); Directory.Delete(directory, true); }
    }
}
