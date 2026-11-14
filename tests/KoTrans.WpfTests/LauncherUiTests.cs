using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class LauncherUiTests
{
    internal static void LayoutAndNavigation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.LauncherUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(directory);
        var settings = new AppSettings(); settings.Validate();
        var original = settings.DefaultProfile;
        original.Translation.Provider = TranslationProvider.Gemini;
        var game = settings.CreateGameProfile("번역 테스트 게임");
        var window = Track(new MainWindow(store, new ApiKeyStore(directory), () => [],
            _ => new PixelRect(0, 0, 1024, 768)));
        try
        {
            window.ApplyLoadedSettings(settings);
            var hint = Named<TextBlock>(window, "LauncherHotkeyHint");
            Check(hint.Text == $"번역 시작·중지: {settings.Hotkeys.ToggleTranslation}  ·  메인 창 표시: {settings.Hotkeys.ShowSettings}",
                "Launcher must identify translation controls and the main-window restore shortcut");
            var helpIcon = Named<Border>(window, "FirstRunWizardHelpIcon");
            Check(helpIcon.Width == 18 && helpIcon.Height == 18
                && helpIcon.CornerRadius.TopLeft == 9 && helpIcon.CornerRadius.TopRight == 9
                && helpIcon.Child is TextBlock { Text: "?" },
                "First-run wizard help icon must be a centered question mark inside a circle");
            var mascot = Named<Image>(window, "LauncherMascot");
            Check(mascot.Source is BitmapSource { PixelWidth: >= 1000, PixelHeight: >= 1000 },
                "Launcher must load high-resolution mascot PNG, not a scaled-up thumbnail");
            // Icon is separate from the large in-app mascot. Read the embedded
            // alpha PNG without opening an HWND, accessing the desktop, or calling APIs.
            var iconUri = new Uri("/KoTrans2;component/Assets/kotrans-app-icon.png", UriKind.Relative);
            var resource = Application.GetResourceStream(iconUri);
            Check(resource is not null, "The selected cat app icon is missing from WPF resources");
            using (var stream = resource!.Stream)
            {
                var decoder = new PngBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var corner = new byte[4];
                rgba.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
                Check(frame.PixelWidth >= 256 && frame.PixelHeight >= 256
                    && corner[3] == 0, "Black matte was not removed from app icon corners");
            }
            var baseWindowStyle = Application.Current.Resources[typeof(Window)] as Style;
            Check(baseWindowStyle?.Setters.OfType<Setter>().Any(s =>
                s.Property == Window.IconProperty && s.Value is ImageSource) == true,
                "Windows must use the same transparent icon in their title bars");
            var launcher = Named<Grid>(window, "LauncherView");
            Check(window.MinHeight >= 400 && launcher.RowDefinitions.Count == 2 &&
                launcher.RowDefinitions[1].Height.IsAuto &&
                Grid.GetRow(Named<LauncherActionPanel>(window, "LauncherHelpActions")) == 1 &&
                Named<WrapPanel>(window, "LauncherPrimaryActions").Children.Contains(Named<Button>(window, "HideSettingsButton")),
                "Hide-settings action can be clipped at the launcher minimum height");
            Check(!Elements<Button>(launcher).Any(b => b.Content is string c
                && (c == "지금 번역" || c == "다시 번역" || c == "번역창 열기")),
                "Obsolete one-shot buttons remain visible in the launcher");
            var profileCombo = Named<ComboBox>(window, "Profiles");
            Check(profileCombo.Style?.Setters.OfType<Setter>().Any(s => s.Property == ComboBox.ForegroundProperty)
                == true, "Profile combo must explicitly define readable selected text");
            Check(Named<Grid>(window, "LauncherView").Visibility == Visibility.Visible &&
                  Named<Grid>(window, "ProfileSettingsView").Visibility == Visibility.Collapsed,
                "Main starts with settings instead of compact translator");
            Check(Named<ListBoxItem>(window, "GlobalSettingsPageItem").Visibility == Visibility.Visible,
                "Default profile did not expose the global settings navigation");
            var navigation = Named<Border>(window, "ProfileNavigation");
            Check(!Elements<TextBlock>(navigation).Any(t => t.Text.Contains("새 프로필은 기본 설정을 복사합니다.")),
                "The profile-creation instruction should not stay visible in every settings page");
            var explanation = Named<TextBlock>(window, "DefaultProfileExplanation");
            var readingMode = Named<ComboBox>(window, "ReadingMode");
            Check(explanation.Visibility == Visibility.Visible,
                "Default-profile-only explanation must be visible for the default profile");
            Check(readingMode.Items.Count == 2
                && Equals(((ComboBoxItem)readingMode.Items[0]).Content, "일반 (대사 중심)")
                && Equals(((ComboBoxItem)readingMode.Items[1]).Content, "노벨 (본문 중심)"),
                "Reading mode choices must explain ADV and NVL without changing their indices");
            window.ShowProfileSettingsPage(0); Layout(window, 1060, 740);
            var reservedHintHeight = explanation.ActualHeight;
            Check(reservedHintHeight > 0, "Default-profile hint must reserve a positive layout height");
            window.ShowProfileSettingsPage(2);
            var providerCombo = Named<ComboBox>(window, "Provider");
            Check(providerCombo.ItemTemplate?.VisualTree != null &&
                providerCombo.ItemTemplate.VisualTree.Type == typeof(TextBlock),
                "Embedded provider selection needs a dark-ink item template");
            Check(Named<Grid>(window, "LauncherView").Visibility == Visibility.Collapsed &&
                  Named<Grid>(window, "ProfileSettingsView").Visibility == Visibility.Visible &&
                  Named<ListBox>(window, "ProfilePages").SelectedIndex == 2,
                "Opening profile settings failed to show translation options");
            var defaultLink = Named<Button>(window, "GoToGlobalCredentials");
            var defaultNavigation = (StackPanel)defaultLink.Parent;
            Check(defaultNavigation.Visibility == Visibility.Visible && Equals(defaultLink.Content, "API 키 관리")
                && defaultNavigation.Children.OfType<TextBlock>().Single().Text.Contains("공통 설정"),
                "Default translation settings did not explain where to manage API keys");
            Click(defaultLink);
            Check(settings.ActiveProfileId == original.Id && Named<ListBox>(window, "ProfilePages").SelectedIndex == 7
                && defaultNavigation.Visibility == Visibility.Collapsed,
                "Default credential shortcut did not open common settings");

            Complete(window.SwitchProfileAsync(game));
            Check(Named<ListBoxItem>(window, "GlobalSettingsPageItem").Visibility == Visibility.Collapsed,
                "Game profile exposed global settings");
            window.ShowProfileSettingsPage(0); Layout(window, 1060, 740);
            Check(explanation.Visibility == Visibility.Hidden
                && Math.Abs(explanation.ActualHeight - reservedHintHeight) <= 0.5,
                "Game profile should hide the default hint while preserving its layout slot");
            Check(readingMode.SelectedIndex == (game.ReadingMode == "NVL" ? 1 : 0),
                "Reading-mode label change must not alter saved ADV/NVL selection");
            window.ShowProfileSettingsPage(2);
            window.ShowProfileSettingsPage(2);
            var link = Named<Button>(window, "GoToGlobalCredentials");
            var gameNavigation = (StackPanel)link.Parent;
            var gameProvider = Named<ComboBox>(window, "Provider");
            var keyedProvider = gameProvider.SelectedItem;
            foreach (var noKeyProvider in new[] { TranslationProvider.Ollama, TranslationProvider.HyMtLocal }) {
                gameProvider.SelectedItem = noKeyProvider;
                Check(gameNavigation.Visibility == Visibility.Collapsed, "Keyless provider shows the shared API shortcut and help");
            }
            gameProvider.SelectedItem = keyedProvider;
            Check(link.IsEnabled && gameNavigation.Visibility == Visibility.Visible
                && Equals(link.Content, "기본 프로필에서 API 키 관리 →"),
                "Game profile did not offer a shortcut to default-profile credentials");
            Click(link);
            Until(() => settings.ActiveProfileId == original.Id &&
                Named<ListBox>(window, "ProfilePages").SelectedIndex == 7,
                "Global credential shortcut did not switch to Default and open common settings");
            Check(Named<ListBoxItem>(window, "GlobalSettingsPageItem").Visibility == Visibility.Visible,
                "Default global settings remained hidden after shortcut");
            var latest = store.LoadAsync(); Complete(latest);
            Check(latest.Result.ActiveProfileId == original.Id,
                "Shortcut failed to persist active Default profile");
            Check(Named<StackPanel>(window, "GlobalKeys").Visibility == Visibility.Visible &&
                  Named<StackPanel>(window, "GlobalHotkeys").Visibility == Visibility.Visible,
                "Global credential and hotkey editors not available on Default");
            Click(Named<Button>(window, "BackToGameProfileButton"));
            Until(() => settings.ActiveProfileId == game.Id &&
                Named<ListBox>(window, "ProfilePages").SelectedIndex == 2,
                "Returning from credentials did not restore the game profile");
            Check(Named<ListBoxItem>(window, "GlobalSettingsPageItem").Visibility == Visibility.Collapsed,
                "Returning to game unexpectedly exposed credentials");

            window.ShowLauncher();
            Check(Named<Grid>(window, "LauncherView").Visibility == Visibility.Visible &&
                  Named<Grid>(window, "ProfileSettingsView").Visibility == Visibility.Collapsed,
                "Return to launcher failed");
            Check(Named<Button>(window, "ProfileSettingsButton").Content is string s && s == "프로필 설정",
                "Main settings button did not reset");
            Check(!window.IsVisible, "Background test showed a native window");
        }
        finally { Close(window); Directory.Delete(directory, true); }
    }
}
