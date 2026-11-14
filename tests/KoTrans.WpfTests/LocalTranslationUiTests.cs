using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class LocalTranslationUiTests
{
    internal static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans.LocalUi", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings = new AppSettings(); settings.Validate(); var p = settings.DefaultProfile;
        p.CaptureMode = CaptureMode.Clipboard; p.Translation.SwitchProvider(TranslationProvider.HyMtLocal);
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        main.PresentStartingOverlayOverride = _ => { };
        var editor = Track(new TranslationSettingsWindow(p, settings.Hotkeys, new ApiKeyStore(dir), () => false, () => true));
        try {
            main.ApplyLoadedSettings(settings);
            Layout(editor, 800, 700);
            Check(Named<TextBox>(editor, "Model").Visibility == Visibility.Collapsed, "Local model field visible");
            Check(Elements<PasswordBox>(editor).All(c => !c.IsVisible), "Local provider asks for a key");
            var selector = Named<ComboBox>(editor, "Provider");
            Check(Equals(new ProviderNameConverter().Convert(TranslationProvider.HyMtLocal, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture), "로컬 AI 번역"), "Internal provider name shown");
            selector.SelectedItem = TranslationProvider.OpenAI; Named<TextBox>(editor, "Model").Text = "preserved-cloud";
            selector.SelectedItem = TranslationProvider.HyMtLocal;
            Check(editor.ApplyChanges(p, settings.Hotkeys), "Local provider requires hidden fields");
            Check(p.Translation.OpenAIModel == "preserved-cloud", "Local selection changed cloud model");
            int prepared = 0, resumed = 0;
            TaskCompletionSource<bool>? gate = null;
            main.PrepareLocalOverride = _ => { prepared++; return gate?.Task ?? Task.FromResult(true); };
            main.StartPreparedLocalOverride = () => { resumed++; return Task.FromResult(true); };
            var start = main.StartTranslationAsync(true); Complete(start);
            Check(start.Result && prepared == 1 && resumed == 1, "Preparation did not resume original Start action automatically");
            main.PrepareLocalOverride = _ => Task.FromResult(false);
            start = main.StartTranslationAsync(true); Complete(start);
            Check(!start.Result && resumed == 1, "Declined installation started translation");
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.PrepareLocalOverride = _ => gate.Task;
            start = main.StartTranslationAsync(true);
            var duplicate = main.StartTranslationAsync(true); Complete(duplicate);
            Check(!duplicate.Result, "Repeated start duplicated installation");
            var other = settings.CreateGameProfile("other"); Complete(main.SwitchProfileAsync(other));
            gate.SetResult(true); Complete(start);
            Check(!start.Result && resumed == 1, "Old profile resumed after preparation");
            Complete(main.SwitchProfileAsync(p)); gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.PrepareLocalOverride = token => gate.Task.WaitAsync(token);
            start = main.StartTranslationAsync(true);
            // The production provider-change path invalidates a pending local preparation.
            p.Translation.SwitchProvider(TranslationProvider.DeepL); main.ApplyLoadedSettings(settings);
            gate.SetResult(true); Complete(start);
            Check(!start.Result && resumed == 1, "Changed provider resumed old local action");
            Pump(); Check(!main.IsVisible && !editor.IsVisible, "Background test opened a window");
        }
        finally { Close(editor); Close(main); Directory.Delete(dir, true); }
    }
}
