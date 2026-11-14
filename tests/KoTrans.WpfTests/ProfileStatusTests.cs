using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class ProfileStatusTests
{
    internal static void ProviderStatusAndNavigation()
    {
        string dir = FeedbackTests.DirectoryPath(); var vault = new ApiKeyStore(dir);
        vault.Save(TranslationProvider.Gemini, "synthetic-gemini"); vault.Save(TranslationProvider.DeepL, "synthetic-deepl");
        var data = new AppSettings(); data.Validate(); var game = data.CreateGameProfile("Game");
        var main = Track(new MainWindow(new SettingsStore(dir), vault, () => []));
        try {
            foreach (var initial in TranslationProviderCatalog.All.Select(p => p.Provider)) {
                data.DefaultProfile.Translation.SwitchProvider(initial); main.ApplyLoadedSettings(data);
                main.ShowProfileSettingsPage(2); Layout(main, 1060, 740);
                AssertProviderStatus(main, initial, vault);
                var editor = (TranslationSettingsEditor)Named<ContentControl>(main, "ProfileEditorHost").Content;
                Check(!editor.HasChanges, "Initial provider status refresh dirtied the profile");
                var selector = Named<ComboBox>(main, "Provider");
                foreach (var next in TranslationProviderCatalog.All.Select(p => p.Provider)) {
                    selector.SelectedItem = next; AssertProviderStatus(main, next, vault);
                    foreach (int page in new[] { 6, 4, 5, 0, 1, 3 }) {
                        main.ShowProfileSettingsPage(page); Layout(main, 1060, 740);
                        Check(Named<TextBlock>(main, "SettingsStatus").Text == ""
                            && Named<TextBlock>(main, "SettingsStatus").Visibility == Visibility.Collapsed,
                            "Provider credentials leaked into another page's bottom message");
                        Check(((FrameworkElement)Named<TextBlock>(main, "ProviderCredentialStatus").Parent).Visibility == Visibility.Collapsed,
                            "Translation status is visible outside its translation panel");
                        main.ShowProfileSettingsPage(2); AssertProviderStatus(main, next, vault);
                    }
                }
            }
            Complete(main.SwitchProfileAsync(game)); main.ShowProfileSettingsPage(2);
            AssertProviderStatus(main, game.Translation.Provider, vault);
            Complete(main.SwitchProfileAsync(data.DefaultProfile)); main.ShowProfileSettingsPage(7);
            Check(Named<TextBlock>(main, "SettingsStatus").Visibility == Visibility.Collapsed,
                "Returning from game to default left a generic credentials banner");
            main.ShowProfileSettingsPage(2); AssertProviderStatus(main, data.DefaultProfile.Translation.Provider, vault);
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void CredentialActionsAndCorrectedValidation()
    {
        string dir = FeedbackTests.DirectoryPath(); var vault = new ApiKeyStore(dir); var settings = new AppSettings(); settings.Validate();
        settings.DefaultProfile.Translation.Provider = TranslationProvider.Gemini;
        var main = Track(new MainWindow(new SettingsStore(dir), vault, () => [])); main.ApplyLoadedSettings(settings);
        try {
            main.ShowProfileSettingsPage(2); var editor = (TranslationSettingsEditor)Named<ContentControl>(main, "ProfileEditorHost").Content;
            AssertProviderStatus(main, TranslationProvider.Gemini, vault);
            main.ShowProfileSettingsPage(7);
            Named<ComboBox>(main, "CredentialProvider").SelectedItem = TranslationProvider.OpenAI;
            Named<ComboBox>(main, "CredentialProvider").SelectedItem = TranslationProvider.Gemini;
            Check(!editor.HasChanges, "Browsing credentials was treated as an edited game profile");
            var password = Elements<PasswordBox>(main).Single(); password.Password = "synthetic-key";
            var generalSave = main.SaveWithFeedbackAsync(); Complete(generalSave);
            Check(generalSave.Result && main.FeedbackToast.Detail.Contains("키 등록/변경")
                && Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Collapsed
                && !vault.ContainsCredential(TranslationProvider.Gemini, TranslationProviderCatalog.Get(TranslationProvider.Gemini).Credentials[0].Id),
                "General save acknowledged or persisted an unregistered credential");
            Click(Button(main, "키 등록/변경"));
            Complete(main.SaveWithFeedbackAsync());
            Check(!main.FeedbackToast.Detail.Contains("키 등록/변경"), "Stored mask mistaken for new input");
            Check(!editor.HasChanges && Named<TextBlock>(main, "CredentialStatus").Text.StartsWith("등록됨"),
                "Credential action dirtied profile or failed to update its own credential state");
            main.ShowProfileSettingsPage(2); AssertProviderStatus(main, TranslationProvider.Gemini, vault);
            main.ShowProfileSettingsPage(7); Click(Button(main, "키 삭제"));
            main.ShowProfileSettingsPage(2); AssertProviderStatus(main, TranslationProvider.Gemini, vault);
            main.ShowProfileSettingsPage(7); password.Password = "unsaved-synthetic"; main.ShowLauncher();
            Check(password.Password == "" && !editor.HasChanges, "Launcher retained hidden unsaved credentials or dirtied settings");
            main.ShowProfileSettingsPage(7);
            Check(password.Password == "" && Named<TextBlock>(main, "SettingsStatus").Visibility == Visibility.Collapsed,
                "Returning to credentials resurrected an old action message/edit");

            main.ShowProfileSettingsPage(2); var timeout = Named<TextBox>(main, "Timeout"); timeout.Text = "not-a-number";
            Check(!editor.ApplyChanges(settings.DefaultProfile, settings.Hotkeys) && editor.LastMessage.Contains("정수"), "Invalid timeout was not surfaced");
            timeout.Text = "30";
            Check(editor.ApplyChanges(settings.DefaultProfile, settings.Hotkeys) && editor.LastMessage == ""
                && Named<TextBlock>(main, "SettingsStatus").Visibility == Visibility.Collapsed, "Corrected form retained its old validation message");
            timeout.Text = "bad"; Check(!editor.ApplyChanges(settings.DefaultProfile, settings.Hotkeys), "Invalid timeout accepted");
            main.ShowProfileSettingsPage(4);
            Check(Named<TextBlock>(main, "SettingsStatus").Visibility == Visibility.Collapsed, "Translation validation error leaked into trigger page");
        } finally { Named<TextBox>(main, "Timeout").Text = "30"; Close(main); Directory.Delete(dir, true); }
    }
    private static void AssertProviderStatus(MainWindow main, TranslationProvider provider, ApiKeyStore vault)
    {
        var info = TranslationProviderCatalog.Get(provider); var summary = Named<TextBlock>(main, "ProviderCredentialStatus");
        string expected = info.Credentials.Count == 0 ? "API 키가 필요하지" : info.Credentials.All(f => vault.ContainsCredential(provider, f.Id)) ? "API 키 등록됨" : "API 키 미등록";
        Check(summary.Text.StartsWith(info.DisplayName + " · ") && summary.Text.Contains(expected)
            && ((FrameworkElement)summary.Parent).Visibility == Visibility.Visible,
            "Provider registration state was missing, stale or from a different provider");
        Check(Named<TextBlock>(main, "SettingsStatus").Text == "", "Registration status still occupies shared bottom message");
    }
}
