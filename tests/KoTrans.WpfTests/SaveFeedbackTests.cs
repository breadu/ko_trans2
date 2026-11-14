using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class SaveFeedbackTests
{
    internal static void DurableAndRepeated()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new Writer(store) { Gate = new() };
        var now = TimeSpan.Zero; var settings = Settings(); Complete(store.SaveAsync(settings));
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], feedbackClock: () => now, persistSettings: writer.Save));
        main.ApplyLoadedSettings(settings); main.ShowProfileSettingsPage(0); Layout(main, 1060, 740);
        try {
            var name = Named<TextBox>(main, "ProfileName"); name.Text = "새 이름";
            var button = Named<Button>(main, "SaveSettingsButton"); var before = FeedbackTests.Bounds(main, button);
            var pending = main.SaveWithFeedbackAsync();
            Check(writer.Calls == 1 && main.IsUserSavePending && !button.IsEnabled, "Save press did not immediately suppress duplicate input");
            Check(main.FeedbackCount == 0 && Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Collapsed,
                "Success appeared before durable write");
            var disk = store.LoadAsync(); Complete(disk); Check(disk.Result.Profiles.Single(p => p.Id == settings.ActiveProfileId).Name == "Game", "Write was not held by the test gate");
            var duplicate = main.SaveWithFeedbackAsync(); Complete(duplicate);
            Check(!duplicate.Result && writer.Calls == 1, "Duplicate press issued another disk save");
            now = TimeSpan.FromMilliseconds(100); main.AdvanceFeedback();
            Check(Named<System.Windows.Shapes.Path>(main, "SaveSpinner").Visibility == Visibility.Collapsed, "Fast save flashed a loading indicator");
            now = TimeSpan.FromMilliseconds(130); main.AdvanceFeedback(); Layout(main, 1060, 740);
            Check(Named<TextBlock>(main, "SaveCaption").Text == "저장 중…" && FeedbackTests.Bounds(main, button) == before,
                "Slow-save label/spinner missing or changed button geometry");
            writer.Gate.SetResult(); Complete(pending); Layout(main, 1060, 740);
            Check(pending.Result && writer.DurableWrites == 1 && main.FeedbackCount == 1 && main.FeedbackToast.Kind == FeedbackKind.Success,
                "Durable completion failed to produce exactly one success");
            Check(Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Visible && button.IsEnabled && FeedbackTests.Bounds(main, button) == before,
                "Completion check changed geometry or prevented retry");
            now += TimeSpan.FromMilliseconds(1400); main.AdvanceFeedback();
            Check(Named<TextBlock>(main, "SaveCaption").Text == "설정 저장" && Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Collapsed,
                "Saved button did not return to its default state");
            var again = main.SaveWithFeedbackAsync(); Complete(again);
            Check(again.Result && writer.Calls == 2 && main.FeedbackCount == 2, "Repeat save failed or left previous animation state");
            now += TimeSpan.FromMilliseconds(500); main.AdvanceFeedback();
            Check(Named<TextBlock>(main, "SaveCaption").Text == "저장 완료", "An old completion deadline reset the next save early");
            writer.Gate = new(); var switching = main.SwitchProfileAsync(settings.DefaultProfile);
            Check(Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Collapsed,
                "New profile operation retained another profile's saved check while still pending");
            writer.Gate.SetResult(); Complete(switching);
        } finally { writer.Gate?.TrySetResult(); writer.Fail = false; Close(main); Directory.Delete(dir, true); }
    }
    internal static void FailureRetryAndAdditionalEdits()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir); var writer = new Writer(store);
        var now = TimeSpan.Zero; var settings = Settings(); Complete(store.SaveAsync(settings)); byte[] original = File.ReadAllBytes(Path.Combine(dir, "settings.json"));
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], feedbackClock: () => now, persistSettings: writer.Save)); main.ApplyLoadedSettings(settings);
        try {
            writer.Fail = true; var failed = main.SaveWithFeedbackAsync(); Complete(failed);
            Check(!failed.Result && main.FeedbackToast.Kind == FeedbackKind.Error && main.FeedbackToast.Visibility == Visibility.Visible,
                "Disk failure was not error feedback");
            Check(Named<TextBlock>(main, "LastSaveFailure").Visibility == Visibility.Visible
                && Named<TextBlock>(main, "StatusMessageText").Visibility == Visibility.Collapsed,
                "Persistent save failure did not take priority over ordinary status");
            Check(Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Collapsed && Named<Button>(main, "SaveSettingsButton").IsEnabled
                && File.ReadAllBytes(Path.Combine(dir, "settings.json")).SequenceEqual(original), "Failure showed a check, blocked retry or damaged original");
            now += TimeSpan.FromMilliseconds(3200); main.AdvanceFeedback();
            Check(main.FeedbackToast.Visibility == Visibility.Hidden && !main.IsFeedbackTimerRunning
                && ((MainViewModel)main.DataContext).Status == "설정 저장 실패 · 저장 위치와 권한을 확인하세요."
                && !Named<TextBlock>(main, "LastSaveFailure").ToolTip.ToString()!.Contains("Synthetic save denied")
                && File.ReadAllBytes(Path.Combine(dir, "settings.json")).SequenceEqual(original)
                && Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Collapsed,
                "Expired save toast cleared the failure state, changed data or reported success");
            writer.Fail = false; var retry = main.SaveWithFeedbackAsync(); Complete(retry);
            Check(retry.Result && main.FeedbackToast.Kind == FeedbackKind.Success, "Successful retry left old failure visible");
            var failureNotice = Named<TextBlock>(main, "LastSaveFailure");
            Check(failureNotice.Visibility == Visibility.Collapsed
                && Named<TextBlock>(main, "StatusMessageText").Visibility == Visibility.Visible,
                "Retry kept a stale failure notice or hid normal status");
            writer.Gate = new(); var older = main.SaveCurrentProfileAsync(); var held = writer.Gate;
            writer.Gate = null; writer.Fail = true; var newer = main.SaveCurrentProfileAsync(); Complete(newer);
            held.SetResult(); Complete(older);
            ((MainViewModel)main.DataContext).Status = "다음 대기";
            now += TimeSpan.FromSeconds(4); main.AdvanceFeedback();
            Check(!newer.Result && older.Result && failureNotice.Visibility == Visibility.Visible
                && main.FeedbackToast.Visibility == Visibility.Hidden, "Late success or toast expiration hid the newer write failure");
            foreach (var size in new[] { new Size(400,620), new Size(640,1008), new Size(1060,740) }) {
                Layout(main, size.Width, size.Height);
                var bounds = FeedbackTests.Bounds(main, failureNotice);
                Check(bounds.Left >= 0 && bounds.Right <= size.Width + 1 && bounds.Height >= failureNotice.FontSize,
                    "Save failure text fell outside the narrow settings layout");
            }
            writer.Fail = false;
            Complete(main.SaveDictionaryPlacementAsync(settings.DefaultProfile, new WindowPlacement()));
            Check(failureNotice.Visibility == Visibility.Visible
                && Named<TextBlock>(main, "StatusMessageText").Visibility == Visibility.Collapsed,
                "Unrelated placement save erased an unresolved settings failure");
            var name = Named<TextBox>(main, "ProfileName"); string validName = name.Text;
            name.Text = ""; Complete(main.SaveWithFeedbackAsync());
            Complete(main.SaveDictionaryPlacementAsync(settings.DefaultProfile, new WindowPlacement()));
            Check(failureNotice.Visibility == Visibility.Visible, "Saving an unrelated placement acknowledged an invalid form");
            name.Text = validName;
            writer.Gate = new(); var pending = main.SaveWithFeedbackAsync();
            Named<TextBox>(main, "Timeout").Text = "42"; Named<Slider>(main, "FontSlider").Value = 30;
            writer.Gate.SetResult(); Complete(pending);
            Check(pending.Result && main.FeedbackToast.Detail.Contains("추가 변경") && Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Collapsed,
                "Earlier snapshot falsely acknowledged newer draft/memory changes");
            var disk = store.LoadAsync(); Complete(disk);
            Check(disk.Result.Profiles.Single(p => p.Id == settings.ActiveProfileId).Translation.TimeoutSeconds == 30,
                "Delayed fixture did not preserve the request's real snapshot");
            var last = main.SaveWithFeedbackAsync(); Complete(last); disk = store.LoadAsync(); Complete(disk);
            Check(last.Result && disk.Result.Profiles.Single(p => p.Id == settings.ActiveProfileId).Translation.TimeoutSeconds == 42
                && Named<TextBlock>(main, "SaveCheck").Visibility == Visibility.Visible, "Additional edits could not be explicitly saved");
        } finally { writer.Gate?.TrySetResult(); writer.Fail = false; Close(main); Directory.Delete(dir, true); }
    }
    internal static void SilentSave()
    {
        string dir = FeedbackTests.DirectoryPath(); var settings = Settings();
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(settings);
        try {
            Complete(main.SaveCurrentProfileAsync());
            Check(main.FeedbackCount == 0 && main.FeedbackToast.Visibility == Visibility.Hidden,
                "Internal save emitted a manual completion toast");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void SafeDetails()
    {
        const string sensitive = "private path, synthetic credential, source text";
        var errors = new (Exception Error, string Detail)[] {
            (new UnauthorizedAccessException(sensitive), "읽기 전용"),
            (new DirectoryNotFoundException(sensitive), "드라이브"),
            (new IOException(sensitive, unchecked((int)0x80070020)), "다른 프로그램"),
            (new IOException(sensitive, unchecked((int)0x80070021)), "다른 프로그램"),
            (new IOException(sensitive, unchecked((int)0x80070070)), "여유 공간"),
            (new IOException(sensitive, unchecked((int)0x80070013)), "읽기 전용"),
            (new InvalidDataException(sensitive), "입력값"),
            (new InvalidOperationException(sensitive), "설정 변경")
        };
        foreach (var item in errors) {
            string detail = MainWindow.SaveFailureDetail(item.Error);
            Check(detail.Contains(item.Detail) && !detail.Contains(sensitive), "Save cause is not actionable or leaks exception content");
        }
    }
    internal static AppSettings Settings()
    { var settings = new AppSettings(); settings.Validate(); settings.ActiveProfileId = settings.CreateGameProfile("Game").Id; return settings; }
    internal sealed class Writer(SettingsStore store)
    {
        internal TaskCompletionSource? Gate; internal bool Fail; internal int Calls, DurableWrites; internal int? FailOnCall;
        internal async Task Save(AppSettings settings)
        {
            int call = ++Calls;
            bool fail = Fail || call == FailOnCall;
            // SettingsStore serializes before yielding; preserve that contract when delaying this fixture.
            var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            if (Gate is { } gate) await gate.Task;
            if (fail) throw new IOException("Synthetic save denied");
            await store.SaveAsync(snapshot); DurableWrites++;
        }
    }
}
