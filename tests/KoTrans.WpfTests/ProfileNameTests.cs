using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class ProfileNameTests
{
    internal static void RenameAndDefaultGuard()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KoTrans2.ProfileName", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings = new AppSettings(); settings.Validate(); var store = new SettingsStore(dir);
        var window = Track(new MainWindow(store, new ApiKeyStore(dir), () => [])); window.ApplyLoadedSettings(settings);
        try
        {
            window.ShowProfileSettingsPage(0); Layout(window, 1060, 740);
            var name = Named<TextBox>(window, "ProfileName"); var rename = Named<Button>(window, "RenameProfileButton");
            var profiles = Named<ComboBox>(window, "Profiles");
            string SelectedCaption() => string.Join("|", OverlayActivityTests.Visuals<TextBlock>(profiles).Select(t => t.Text));
            Check(name.IsReadOnly && !rename.IsEnabled, "Default profile exposes editable name controls");
            // Also protect programmatic/stale commands, not just disabled visual controls.
            name.Text = "attempted default rename"; Complete(window.SaveCurrentProfileAsync());
            Check(settings.DefaultProfile.Name == "기본 프로필" && name.Text == "기본 프로필", "Settings Save renamed the protected default");
            Click(Button(window, "새로 만들기"));
            Until(() => !window.IsUserSavePending, "Profile creation did not finish");
            var game = settings.Profiles.Single(p => p.Id == settings.ActiveProfileId);
            Layout(window, 1060, 740);
            Check(!name.IsReadOnly && rename.IsEnabled && SelectedCaption().Contains("새 게임 프로필"), "New profile has no rendered/editable name");
            name.Text = "Pink Sox · 日本語"; Click(rename); Layout(window, 1060, 740);
            Until(() => !window.IsUserSavePending, "Rename save did not finish");
            Check(SelectedCaption().Contains("Pink Sox · 日本語") && !SelectedCaption().Contains("새 게임 프로필")
                && profiles.SelectedItem == game, "Displayed ComboBox caption stayed stale after Rename");
            settings.CreateGameProfile("Saved new name");
            Check(settings.CreateGameProfile("Saved new name").Name == "Saved new name (2)",
                "Creating a profile with an existing name did not add a suffix");
            name.Text = "  Saved new name  "; Click(Button(window, "설정 저장")); Layout(window, 1060, 740);
            Until(() => !window.IsUserSavePending, "Profile settings save did not finish");
            Check(name.Text == "Saved new name (3)" && SelectedCaption().Contains(name.Text)
                && !SelectedCaption().Contains("Pink Sox"), "Settings Save did not show the next available name");
            Complete(window.RenameProfileWithFeedbackAsync());
            Check(game.Name == "Saved new name (3)", "Saving the same profile again treated its own name as a duplicate");
            var load = store.LoadAsync(); Complete(load);
            Check(load.Result.Profiles.Single(p => p.Id == game.Id).Name == "Saved new name (3)"
                && load.Result.DefaultProfile.Name == "기본 프로필", "Rename persistence altered the wrong profile");
            string maxName = new string('A', 75) + "😀XYZ";
            settings.CreateGameProfile(maxName);
            Check(settings.CreateGameProfile(maxName).Name == new string('A', 75) + " (2)",
                "Duplicate maximum-length name split a surrogate pair when adding its suffix");
            window.ShowProfileSettingsPage(4);
            Check(!Text(window).Contains("공통 인증 정보는 기본 프로필에서 관리합니다"), "Input trigger editor retains removed credential note");
            var provider = Named<ComboBox>(window, "Provider"); provider.SelectedItem = TranslationProvider.OpenAI;
            Check(!Text(window).Contains("공통 인증 정보는 기본 프로필에서 관리합니다"), "Provider switch restores removed credential note");
            Complete(window.SwitchProfileAsync(settings.DefaultProfile));
            Check(name.IsReadOnly && !rename.IsEnabled, "Returning to default re-enabled rename");
            Complete(window.SwitchProfileAsync(game)); Layout(window, 1060, 740);
            Check(!name.IsReadOnly && rename.IsEnabled && SelectedCaption().Contains("Saved new name"), "Game name not restored after switching");
        }
        finally { Close(window); Directory.Delete(dir, true); }
    }
}
