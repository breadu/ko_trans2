using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class ProfileFeedbackTests
{
    internal static void MajorActions()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new SaveFeedbackTests.Writer(store) { Gate = new() };
        var settings = SaveFeedbackTests.Settings(); Complete(store.SaveAsync(settings));
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], persistSettings: writer.Save)); main.ApplyLoadedSettings(settings);
        try {
            var created = main.CreateProfileWithFeedbackAsync();
            Check(main.FeedbackCount == 0 && main.IsUserSavePending, "Profile creation reported success before its save");
            writer.Gate.SetResult(); Complete(created); var game = settings.Profiles.Single(p => p.Id == settings.ActiveProfileId);
            Check(created.Result && main.FeedbackCount == 1 && main.FeedbackToast.Message == "새 프로필을 만들었어요", "Nested create/switch/save produced duplicate or wrong feedback");
            Check(main.FeedbackToast.Detail.Contains("기본 설정 복사 완료")
                && main.FeedbackToast.Detail.Contains("게임 창과 인식 영역은 새로 지정"),
                "Creating a profile must explain the copied defaults and required game-specific capture setup");
            Named<TextBox>(main, "ProfileName").Text = "이름 변경됨";
            var rename = main.RenameProfileWithFeedbackAsync(); Complete(rename);
            Check(rename.Result && main.FeedbackCount == 2 && main.FeedbackToast.Message == "프로필 이름을 변경했어요"
                && game.Name == "이름 변경됨", "Rename completion missing or duplicated");
            var switched = main.SwitchProfileAsync(settings.DefaultProfile); Complete(switched);
            Check(switched.Result && main.FeedbackCount == 3 && main.FeedbackToast.Message == "해당 프로필로 전환했어요", "Switch feedback missing or duplicated");
            Complete(main.SwitchProfileAsync(settings.DefaultProfile)); Check(main.FeedbackCount == 3, "Same-profile internal synchronization emitted a switch toast");
            Complete(main.SwitchProfileAsync(game)); int before = main.FeedbackCount; int writes = writer.Calls;
            var deleted = main.DeleteProfileWithFeedbackAsync(); Complete(deleted);
            Check(deleted.Result && main.FeedbackCount == before + 1 && writer.Calls == writes + 1
                && main.FeedbackToast.Message == "프로필을 삭제했어요" && main.FeedbackToast.Detail == "이름 변경됨",
                "Nested delete/default-switch/save emitted multiple toasts or the wrong profile name");
            var saved = store.LoadAsync(); Complete(saved);
            Check(saved.Result.Profiles.All(p => p.Id != game.Id), "Delete toast preceded persisted deletion");
            int saves = writer.Calls;
            var models = Named<ComboBox>(main, "OcrEngineChoices");
            models.SelectedItem = models.Items.OfType<ComboBoxItem>().Single(b =>
                b.Tag is OcrModelPresetDescriptor { EngineId: "paddle", Id: "default" });
            Check(main.FeedbackToast.Kind == FeedbackKind.Information && main.FeedbackToast.Detail.Contains("저장 필요")
                && writer.Calls == saves, "Model selection falsely claimed persistence or added autosave");
            int notices = main.FeedbackCount;
            models.SelectedItem = models.SelectedItem;
            Check(main.FeedbackCount == notices, "Same OCR choice emitted repeated information");
            main.ShowProfileSettingsPage(2); Click(Button(main, "메인으로"));
            Until(() => Named<Grid>(main, "LauncherView").Visibility == Visibility.Visible, "Saved return to launcher failed");
            Check(main.FeedbackCount == notices && main.FeedbackToast.Visibility == Visibility.Hidden,
                "Return autosave generated success or left an already-saved model marked as unsaved");
        } finally { writer.Fail = false; writer.FailOnCall = null; writer.Gate?.TrySetResult(); Close(main); Directory.Delete(dir, true); }
    }
    internal static void FailedTransitions()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new SaveFeedbackTests.Writer(store);
        var settings = SaveFeedbackTests.Settings(); Complete(store.SaveAsync(settings));
        var game = settings.Profiles.Single(p => p.Id == settings.ActiveProfileId);
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], persistSettings: writer.Save)); main.ApplyLoadedSettings(settings);
        var ocr = new UnconfiguredOcrService();
        var pipe = new TranslationPipeline(ocr, new MockTranslationService(), _ => throw new Exception("unexpected capture"),
            new("en", "ADV", false), new(), new(), "synthetic");
        try {
            main.ObserveTranslation(pipe, game.Id, watch: true); pipe.Start(false);
            writer.Fail = true; var failed = main.SwitchProfileAsync(settings.DefaultProfile); Complete(failed);
            Check(!failed.Result && main.FeedbackCount == 1 && main.FeedbackToast.Kind == FeedbackKind.Error,
                "Failed profile switch showed success");
            var original = store.LoadAsync(); Complete(original);
            Check(original.Result.ActiveProfileId == game.Id, "Failed switch damaged persisted selection");
            var combo = Named<ComboBox>(main, "Profiles");
            Check(ReferenceEquals(combo.SelectedItem, game) && settings.ActiveProfileId == game.Id,
                "Rejected switch replaced the live selection");
            int profileCount = settings.Profiles.Count;
            var created = main.CreateProfileWithFeedbackAsync(); Complete(created);
            Check(!created.Result && settings.Profiles.Count == profileCount && ReferenceEquals(combo.SelectedItem, game),
                "Rejected creation leaked into the live list");
            writer.Fail = false; Complete(main.SwitchProfileAsync(game));
            Named<TextBox>(main, "ProfileName").Text = " "; int writes = writer.Calls;
            var invalid = main.SwitchProfileAsync(settings.DefaultProfile); Complete(invalid);
            Check(!invalid.Result && writer.Calls == writes && settings.ActiveProfileId == game.Id
                && main.FeedbackToast.Kind == FeedbackKind.Error, "Invalid draft switched/saved or showed success");
            Named<TextBox>(main, "ProfileName").Text = "Game";
            writer.FailOnCall = writer.Calls + 1; int before = main.FeedbackCount;
            var deleted = main.DeleteProfileWithFeedbackAsync(); Complete(deleted);
            Check(!deleted.Result && main.FeedbackCount == before + 1 && main.FeedbackToast.Kind == FeedbackKind.Error,
                "Intermediate switch save incorrectly acknowledged a failed final deletion save");
            var saved = store.LoadAsync(); Complete(saved);
            Check(saved.Result.Profiles.Any(p => p.Id == game.Id), "Failed final deletion did not preserve the last successful file");
            Check(settings.Profiles.Contains(game) && ReferenceEquals(combo.SelectedItem, game), "Rejected deletion changed live identity");
            Check(pipe.IsRunning, "Failed profile operation stopped the previous session");
            Complete(main.SaveCurrentProfileAsync()); saved = store.LoadAsync(); Complete(saved);
            Check(saved.Result.ActiveProfileId == game.Id && saved.Result.Profiles.Any(p => p.Id == game.Id),
                "Unrelated save committed the failed deletion");
            writer.Gate = new();
            var pending = main.DeleteProfileWithFeedbackAsync();
            var position = new WindowPlacement { X = 123 };
            var autosave = main.SaveDictionaryPlacementAsync(settings.DefaultProfile, position);
            Check(!autosave.IsCompleted && ReferenceEquals(combo.SelectedItem, game), "Pending deletion exposed an uncommitted profile");
            writer.Gate.SetResult(); Complete(pending); Complete(autosave); saved = store.LoadAsync(); Complete(saved);
            Check(pending.Result && saved.Result.Profiles.All(p => p.Id != game.Id)
                && saved.Result.DefaultProfile.DictionaryPopupWindow?.X == 123, "Queued save lost a placement or resurrected a deleted profile");
        } finally { writer.Gate?.TrySetResult(); writer.Fail = false; writer.FailOnCall = null;
            Close(main); Complete(pipe.DisposeAsync().AsTask()); Complete(ocr.DisposeAsync().AsTask()); Directory.Delete(dir, true); }
    }
    internal static void ModelSaveSnapshot()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new SaveFeedbackTests.Writer(store) { Gate = new() };
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], persistSettings: writer.Save));
        main.ApplyLoadedSettings(SaveFeedbackTests.Settings());
        try {
            var choices = Named<ComboBox>(main, "OcrEngineChoices");
            choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(b =>
                b.Tag is OcrModelPresetDescriptor { EngineId: "paddle", Id: "default" });
            var pending = main.SaveCurrentProfileAsync();
            choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(b =>
                b.Tag is OcrModelPresetDescriptor { EngineId: "paddle", Id: "v5-det-v6-medium-rec" });
            writer.Gate.SetResult(); Complete(pending);
            Check(main.FeedbackToast.Kind == FeedbackKind.Information && main.FeedbackToast.Visibility == Visibility.Visible,
                "Earlier saved model cleared a newer unsaved OCR selection");
            Complete(main.SaveCurrentProfileAsync());
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && main.FeedbackCount == 2,
                "Matching saved model kept stale save-needed advice or emitted an autosave success toast");
        } finally { writer.Gate?.TrySetResult(); Close(main); Directory.Delete(dir, true); }
    }
}
