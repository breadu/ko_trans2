using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class ProviderUiTests
{
    internal static void Controls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.Wpf.Provider", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vault = new ApiKeyStore(directory);
        var profile = new Profile { Translation=new() { Provider=TranslationProvider.OpenAI, Model="gpt-6-luna", GeminiModel="gemini-3.5-flash-lite", OllamaModel="synthetic-local" } };
        vault.Save(TranslationProvider.OpenAI,"synthetic-openai-existing");
        var hotkeys = new HotkeyOptions();
        var window = Track(new TranslationSettingsWindow(profile, hotkeys, vault, () => true, () => true));
        try
        {
            var provider = Named<ComboBox>(window, "Provider");
            var credentialSelector = Named<ComboBox>(window, "CredentialProvider");
            credentialSelector.SelectedItem = TranslationProvider.OpenAI;
            var masked = Elements<PasswordBox>(window).Single();
            Check(masked.Password == "********" && masked.MinHeight == 0
                && masked.Padding.Equals(new Thickness(11, 8, 11, 8)),
                "Stored API key must remain masked with normal text-field height/padding");
            Click(Button(window, "키 등록/변경"));
            Check(vault.Read(TranslationProvider.OpenAI) == "synthetic-openai-existing",
                "Masked placeholder must never overwrite protected credentials");
            var shortcut = Named<TextBox>(window, "ShowHotkey");
            Check(shortcut is HotkeyCaptureBox { IsReadOnly: true },
                "Global shortcut still allows free-form typing");
            Check(HotkeyCaptureBox.TryFormatHotkey(Key.F7, ModifierKeys.Control | ModifierKeys.Alt, true, out var captured)
                && captured == "Ctrl+Alt+F7", "Physical key chord formatting was not normalized");
            Check(!HotkeyCaptureBox.TryFormatHotkey(Key.LeftCtrl, ModifierKeys.Control, true, out _)
                && !HotkeyCaptureBox.TryFormatHotkey(Key.F8, ModifierKeys.Control, false, out _)
                && !HotkeyCaptureBox.TryFormatHotkey(Key.Escape, ModifierKeys.None, true, out _)
                && !HotkeyCaptureBox.TryFormatHotkey(Key.Tab, ModifierKeys.None, true, out _),
                "Modifier-only, QA Ctrl, Escape or Tab capture was accepted");
            Check(Named<TextBox>(window, "SaveGameQaHotkey") is HotkeyCaptureBox { AllowControl: false },
                "Pass-through F8 capture must reject Ctrl");
            ((HotkeyCaptureBox)shortcut).CaptureRegisteredHotkey(1 | 4, 0x75); // WM_HOTKEY Alt+Shift+F6
            Check(shortcut.Text == "Alt+Shift+F6",
                "Already-registered shortcut event did not reach the keyboard capture box");
            var qaShortcut = (HotkeyCaptureBox)Named<TextBox>(window, "SaveGameQaHotkey");
            var oldQa = qaShortcut.Text;
            qaShortcut.CaptureRegisteredHotkey(2, 0x77); // Ctrl+F8 remains forbidden
            Check(qaShortcut.Text == oldQa, "WM_HOTKEY path bypassed the Game QA Ctrl restriction");
            var model = Named<TextBox>(window, "Model");
            var plan = Named<ComboBox>(window, "DeepLPlan");
            var help = Named<TextBlock>(window, "ProviderHelp");
            Check(help.Text == "문장 해설 지원"
                && Named<TextBlock>(window, "ProviderCredentialStatus").Text.Contains("API 키 등록됨"),
                "Cloud provider should show short study and credential guidance");
            Check(Elements<TextBlock>(window).Any(t => t.Text == "모델 ID")
                && Elements<TextBlock>(window).Any(t => t.Text == "응답 대기 시간 (초)"),
                "Model and timeout labels should not contain internal persistence details");
            Check(provider.Items.Count == TranslationProviderCatalog.All.Count &&
                  provider.Items.Cast<TranslationProvider>().Contains(TranslationProvider.DeepL),
                  "Provider catalog did not populate settings UI");
            provider.SelectedItem = TranslationProvider.DeepL;
            Check(model.Visibility == Visibility.Collapsed && plan.Visibility == Visibility.Visible,
                  "DeepL must not request a model ID or hide the plan selector");
            Check(help.Text == "번역 전용 서비스입니다. 문장 해설과 캐릭터 사전 기능은 지원하지 않습니다."
                && Elements<TextBlock>(window).Any(t => t.Text == "DeepL API 요금제"),
                "DeepL needs a short capability warning without internal JSON jargon");
            Check(Named<StackPanel>(window, "GlobalKeys").Visibility == Visibility.Visible,
                  "Default profile lost access to global credential management");
            plan.SelectedItem = DeepLApiPlan.Pro;
            Elements<PasswordBox>(window).Single().Password = "synthetic-test-key";
            Click(Button(window, "키 등록/변경"));
            Check(vault.ReadCredential(TranslationProvider.DeepL, "ApiKey") == "synthetic-test-key",
                  "DeepL key registration failed through default profile UI");
            Check(window.ApplyChanges(profile, hotkeys) && profile.Translation.Provider == TranslationProvider.DeepL &&
                  profile.Translation.DeepLPlan == DeepLApiPlan.Pro && profile.Translation.Model == "",
                  "Model-free DeepL settings failed Apply");
            provider.SelectedItem = TranslationProvider.OpenAI;
            Check(model.Visibility == Visibility.Visible && plan.Visibility == Visibility.Collapsed,
                  "Model provider was not restored after DeepL");
            Check(model.Text=="gpt-6-luna" && vault.Read(TranslationProvider.OpenAI)=="synthetic-openai-existing","DeepL selection/credentials changed existing OpenAI model/key");
            provider.SelectedItem=TranslationProvider.Gemini;
            Check(model.Text=="gemini-3.5-flash-lite" && help.Text == "문장 해설 지원",
                "Gemini model or concise capability summary not restored after DeepL/OpenAI");
            provider.SelectedItem=TranslationProvider.Ollama;
            Check(model.Text=="synthetic-local" && Elements<PasswordBox>(window).Single().Visibility==Visibility.Collapsed,
                "Ollama model/no-key UI changed");
            var format = Named<ComboBox>(window, "OllamaFormat");
            var formatHelp = Named<TextBlock>(window, "OllamaFormatHelp");
            var memoryHelp = Named<TextBlock>(window, "OllamaKeepAliveHelp");
            Check(help.Visibility == Visibility.Collapsed
                && formatHelp.Visibility == Visibility.Visible && formatHelp.Text.Contains("모델에 따라")
                && memoryHelp.Visibility == Visibility.Visible && memoryHelp.Text.Contains("재로딩")
                && format.SelectedItem is OllamaRequestFormat.Chat,
                "Ollama Chat needs one short format-specific explanation and memory guidance");
            var formatNames = new OllamaRequestFormatLabelConverter();
            Check((string)formatNames.Convert(OllamaRequestFormat.Chat, typeof(string), null!,
                    System.Globalization.CultureInfo.InvariantCulture) == "Chat (일반 대화형 모델)"
                && (string)formatNames.Convert(OllamaRequestFormat.TranslateGemma, typeof(string), null!,
                    System.Globalization.CultureInfo.InvariantCulture) == "TranslateGemma (번역 전용 모델)"
                && (string)formatNames.Convert(OllamaRequestFormat.HyMt, typeof(string), null!,
                    System.Globalization.CultureInfo.InvariantCulture) == "HY-MT (번역 전용 모델)"
                && format.Items.Cast<OllamaRequestFormat>().Contains(OllamaRequestFormat.HyMt)
                && format.ItemTemplate is not null,
                "Ollama dropdown must describe each request type without changing enum values");
            var formatPresenter = new ContentPresenter {
                Content = OllamaRequestFormat.TranslateGemma,
                ContentTemplate = format.ItemTemplate
            };
            var formatHost = Track(new Window { Content = formatPresenter });
            try
            {
                Layout(formatHost, 380, 35);
                var displayedFormat = OverlayActivityTests.Visuals<TextBlock>(formatPresenter).Single();
                Check(displayedFormat.Text == "TranslateGemma (번역 전용 모델)",
                    "Rendered Ollama dropdown item lost its descriptive label");
            }
            finally { Close(formatHost); }
            format.SelectedItem = OllamaRequestFormat.TranslateGemma;
            Check(formatHelp.Text.Contains("번역 전용")
                && formatHelp.Text.Contains("캐릭터 사전")
                && !formatHelp.Text.Contains("JSON")
                && profile.Translation.Provider == TranslationProvider.DeepL,
                "TranslateGemma must update helper text without auto-saving an unsaved provider edit");
            provider.SelectedItem = TranslationProvider.DeepL;
            Check(formatHelp.Visibility == Visibility.Collapsed && memoryHelp.Visibility == Visibility.Collapsed,
                "Ollama-only descriptions leaked into other providers");
            provider.SelectedItem = TranslationProvider.Ollama;
            Check(format.SelectedItem is OllamaRequestFormat.TranslateGemma
                && formatHelp.Text.Contains("번역 전용"),
                "Ollama model-specific request format was not preserved across provider switching");
            format.SelectedItem = OllamaRequestFormat.HyMt;
            Check(formatHelp.Text.Contains("이전 원문") && formatHelp.Text.Contains("캐릭터 이름")
                && formatHelp.Text.Contains("문장 해설은 지원하지") && profile.Translation.Provider == TranslationProvider.DeepL,
                "HY-MT capability guidance missing or selecting its format auto-saved the profile");
            model.Text = "other-synthetic-model";
            model.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check(format.SelectedItem is OllamaRequestFormat.Chat, "HY-MT request format leaked into another model");
            model.Text = "synthetic-local";
            model.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check(format.SelectedItem is OllamaRequestFormat.HyMt, "Switching back lost the model-specific HY-MT selection");
            Check(window.ApplyChanges(profile, hotkeys)
                && profile.Translation.SettingsForOllama().Format == OllamaRequestFormat.HyMt,
                "HY-MT request format could not be saved from the settings controls");
            var reopened = Track(new TranslationSettingsWindow(profile, hotkeys, vault, () => true, () => true));
            try { Check(Named<ComboBox>(reopened, "OllamaFormat").SelectedItem is OllamaRequestFormat.HyMt,
                "Reopening the settings editor did not restore HY-MT"); }
            finally { Close(reopened); }
            provider.SelectedItem = TranslationProvider.DeepL;
            Check((DeepLApiPlan)plan.SelectedItem == DeepLApiPlan.Pro,
                  "DeepL Free/Pro plan lost during provider switching");
            var other = new Profile { Name = "Game A", Translation = new() { Provider = TranslationProvider.DeepL } };
            var game = Track(new TranslationSettingsWindow(other, hotkeys, vault, () => false, () => true));
            try
            {
                Check(Named<StackPanel>(game, "GlobalKeys").Visibility == Visibility.Collapsed &&
                      Named<StackPanel>(game, "GlobalHotkeys").Visibility == Visibility.Collapsed,
                      "Game profile exposed global credentials or hotkeys");
                Elements<PasswordBox>(game).Single().Password = "do-not-save";
                Click(Button(game, "키 등록/변경"));
                Check(vault.ReadCredential(TranslationProvider.DeepL, "ApiKey") == "synthetic-test-key",
                      "Hidden game profile command changed global credential");
                Check(game.ApplyChanges(other, hotkeys) && other.Translation.Provider == TranslationProvider.DeepL,
                      "Game profile could not apply local DeepL settings");
            }
            finally { Close(game); }
        }
        finally { Close(window); Directory.Delete(directory, true); }
    }
    internal static void CredentialDisplay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.Wpf.Credentials", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var initial in TranslationProviderCatalog.All.Select(p => p.Provider))
            {
                var path = Path.Combine(directory, initial.ToString());
                var vault = new ApiKeyStore(path);
                vault.Save(TranslationProvider.Gemini, "synthetic-gemini");
                vault.Save(TranslationProvider.DeepL, "synthetic-deepl");
                var settings = new AppSettings(); settings.Validate();
                settings.DefaultProfile.Translation.SwitchProvider(initial);
                var window = Track(new MainWindow(new SettingsStore(path), vault, () => [], _ => new PixelRect(0, 0, 1024, 768)));
                try
                {
                    window.ApplyLoadedSettings(settings);
                    window.ShowProfileSettingsPage(7);
                    Layout(window, 1060, 740);
                    var selector = Named<ComboBox>(window, "CredentialProvider");
                    var secret = Elements<PasswordBox>(window).Single();
                    var state = Named<TextBlock>(window, "CredentialStatus");
                    var selected = (TranslationProvider)selector.SelectedItem;
                    Check(secret.Visibility == Visibility.Visible &&
                          secret.Password == (vault.Contains(selected) ? "********" : ""),
                          "Initial global credential input/mask missing: " + initial);
                    selector.SelectedItem = TranslationProvider.Gemini;
                    Check(secret.Password == "********" && state.Text.StartsWith("등록됨"), "Saved Gemini state not shown");
                    secret.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, secret)
                        { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
                    Check(secret.Password == "********", "Focusing a saved credential erased its mask");
                    foreach (var page in new[] { 0, 1, 2, 3, 4, 5 })
                    {
                        window.ShowProfileSettingsPage(page);
                        Check(Named<StackPanel>(window, "GlobalKeys").Visibility == Visibility.Collapsed && secret.Password == "",
                              "Hidden credential input was not cleared");
                        window.ShowProfileSettingsPage(7);
                        Check(ReferenceEquals(secret, Elements<PasswordBox>(window).Single()) && secret.Visibility == Visibility.Visible &&
                              secret.Password == "********", "Returning to common settings lost saved credential state");
                    }
                    selector.SelectedItem = TranslationProvider.OpenAI;
                    Check(secret.Password == "" && state.Text.StartsWith("미등록"), "Unregistered provider inherited another mask");
                    secret.Password = "synthetic-unsaved";
                    selector.SelectedItem = TranslationProvider.DeepL;
                    Check(secret.Password == "********" && state.Text.StartsWith("등록됨"), "Saved DeepL mask not restored");
                    selector.SelectedItem = TranslationProvider.OpenAI;
                    Check(secret.Password == "" && !vault.Contains(TranslationProvider.OpenAI), "Unsaved input became a registered key");
                    secret.Password = "synthetic-openai";
                    window.ShowProfileSettingsPage(7);
                    Check(secret.Password == "synthetic-openai" && state.Text.Contains("아직 저장되지"), "Page refresh discarded active key edit");
                    Click(Button(window, "키 등록/변경"));
                    Check(secret.Password == "********" && state.Text.StartsWith("등록됨") && vault.Read(TranslationProvider.OpenAI) == "synthetic-openai",
                          "Registration did not show protected saved state");
                    Click(Button(window, "키 등록/변경"));
                    Check(vault.Read(TranslationProvider.OpenAI) == "synthetic-openai", "Fixed mask overwrote a saved key");
                    secret.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, secret, "s"))
                        { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                    Check(secret.Password == "", "Typing did not remove the stored placeholder before editing");
                    secret.Password = "synthetic-replacement";
                    Click(Button(window, "키 등록/변경"));
                    Check(secret.Password == "********" && vault.Read(TranslationProvider.OpenAI) == "synthetic-replacement", "Key replacement failed");
                    secret.RaiseEvent(new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "synthetic-paste"), false, DataFormats.UnicodeText)
                        { RoutedEvent = DataObject.PastingEvent });
                    Check(secret.Password == "", "Paste did not remove the stored placeholder");
                    secret.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, secret, null)
                        { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                    Check(secret.Password == "********", "Leaving an empty edit lost the saved mask");
                    Click(Button(window, "키 삭제"));
                    Check(secret.Password == "" && state.Text.StartsWith("미등록") && !vault.Contains(TranslationProvider.OpenAI), "Deleted key still appeared registered");
                    selector.SelectedItem = TranslationProvider.Gemini;
                    selector.SelectedItem = TranslationProvider.OpenAI;
                    Check(secret.Password == "" && settings.DefaultProfile.Translation.Provider == initial,
                          "Credential selection altered translation provider or resurrected deleted key");
                }
                finally { Close(window); }
            }
        }
        finally { Directory.Delete(directory, true); }
    }

}
