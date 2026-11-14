using System.IO;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class DictionaryPlacementTests
{
    private static readonly DictionaryPopupBounds Center = new(480, 166, 640, 667);
    internal static void MonitorRestoration()
    {
        DisplayArea primary = new("main", "DISPLAY1", new(0, 0, 2560, 1400), 1, true);
        DisplayArea portrait = new("portrait", "DISPLAY2", new(-1600, -100, 1600, 2500), 2.5);
        var saved = new WindowPlacement { X = -1500, Y = 300, Width = 500, Height = 600,
            MonitorId = "portrait", MonitorDevice = "DISPLAY2", MonitorWorkX = -1600, MonitorWorkY = -100 };
        var restored = DictionaryPopupPlacement.Resolve(saved, [primary, portrait], Center);
        Check(restored.Bounds == new DictionaryPopupBounds(-1500, 300, 1250, 1500) && !restored.Temporary,
            "Dictionary did not restore saved secondary-monitor position and DPI size");
        var movedMonitor = portrait with { Device = "DISPLAY3", WorkArea = new(2560, 0, 1600, 2500) };
        restored = DictionaryPopupPlacement.Resolve(saved, [primary, movedMonitor], Center);
        Check(restored.Bounds.Left == 2660 && restored.Bounds.Top == 400 && !restored.Temporary,
            "Monitor ID / changed monitor origin didn't preserve relative dictionary position");
        var missing = DictionaryPopupPlacement.Resolve(saved, [primary], Center);
        Check(missing.Temporary && missing.Bounds.Left >= 0 && missing.Bounds.Top >= 0,
            "Disconnected monitor didn't produce visible temporary fallback");
        Check(saved.X == -1500 && saved.MonitorId == "portrait", "Resolving missing monitor overwrote saved monitor");
        Check(DictionaryPopupPlacement.Resolve(null, [primary], Center).Bounds == Center, "First dictionary popup lost centered default");
        var legacy = new WindowPlacement { X = -1500, Y = 300, Width = 500, Height = 600, MonitorDevice = "DISPLAY2" };
        Check(!DictionaryPopupPlacement.Resolve(legacy, [primary, portrait], Center).Temporary, "Monitor-device fallback failed");
        var reduced = portrait with { WorkArea = new(-1600, 0, 1000, 1200) };
        restored = DictionaryPopupPlacement.Resolve(saved, [primary, reduced], Center);
        Check(restored.Bounds.Width <= 1000 && restored.Bounds.Height <= 1200 && restored.Bounds.Top >= 0,
            "Saved dictionary popup is offscreen after resolution/work-area change");
    }
    internal static void CloseAndRebind()
    {
        var first = new Profile(); var second = new Profile();
        var saves = new List<(Guid Profile, WindowPlacement Placement)>();
        DictionaryPopupSnapshot? snapshot = Snapshot(-1200, 200, "secondary");
        Action<bool>? notify = null; int attached = 0, detached = 0;
        using var tracker = new DictionaryPopupTracker((p, placement) => {
            saves.Add((p.Id, placement)); p.DictionaryPopupWindow = placement; return Task.CompletedTask;
        }, _ => snapshot, (_, changed) => { attached++; notify = changed; return new Subscription(() => detached++); });
        Complete(tracker.PrepareAsync(first)); tracker.Attach(new(42), false);
        snapshot = Snapshot(-1100, 250, "secondary"); notify!(false);
        Check(tracker.CurrentBounds() == snapshot.Bounds && saves.Count == 0, "Live dictionary move was reset or saved before close");
        tracker.Attach(new(42), false); Check(attached == 1 && detached == 0, "Repeated word click replaced live window observation");
        snapshot = null; notify(true); Complete(tracker.FlushAsync());
        Check(saves.Count == 1 && saves[0].Profile == first.Id && saves[0].Placement.X == -1100,
            "Close didn't save the last normal position for its opening profile");
        Complete(tracker.PrepareAsync(second)); snapshot = Snapshot(500, 400, "primary"); tracker.Attach(new(43), false);
        snapshot = Snapshot(700, 500, "primary"); notify(false);
        // Reusing a still-open popup for another profile must commit its previous owner first.
        Complete(tracker.PrepareAsync(first));
        Check(saves.Count == 2 && saves[1].Profile == second.Id && second.DictionaryPopupWindow?.X == 700
            && first.DictionaryPopupWindow?.X == -1100 && detached == 2, "Reused popup mixed profile placements or leaked hooks");
        snapshot = Snapshot(30, 30, "primary"); tracker.Attach(new(44), true); snapshot = null; notify(true); Complete(tracker.FlushAsync());
        Check(saves.Count == 2 && first.DictionaryPopupWindow?.MonitorId == "secondary", "Unedited fallback destroyed missing-monitor placement");
        snapshot = Snapshot(30, 30, "primary"); tracker.Attach(new(45), true);
        snapshot = Snapshot(200, 300, "primary"); notify(false); snapshot = null; notify(true); Complete(tracker.FlushAsync());
        Check(saves.Count == 3 && first.DictionaryPopupWindow?.MonitorId == "primary", "User-edited fallback wasn't saved");
        tracker.Dispose(); Check(attached == detached, "Dictionary placement observation wasn't released");
    }
    internal static void AsyncSaveAndPersistence()
    {
        ExitSaveDecisions();
        var first = new Profile(); var second = new Profile();
        DictionaryPopupSnapshot? snapshot = Snapshot(120, 240, "primary"); Action<bool>? notify = null;
        var held = new TaskCompletionSource(); int calls = 0; string failure = "";
        using (var tracker = new DictionaryPopupTracker((p, placement) => { calls++; p.DictionaryPopupWindow = placement; return calls == 1 ? held.Task : Task.CompletedTask; },
            _ => snapshot, (_, changed) => { notify = changed; return new Subscription(() => { }); })) {
            tracker.Failed += message => failure = message;
            Complete(tracker.PrepareAsync(first)); tracker.Attach(new(55), false); snapshot = null; notify!(true);
            var switching = tracker.PrepareAsync(second); Check(!switching.IsCompleted, "Profile rebinding didn't await close save");
            held.SetException(new IOException("synthetic save failure")); Complete(switching);
            Check(calls == 2 && failure.Contains("저장 실패") && first.DictionaryPopupWindow?.X == 120,
                "Failed close save was lost or failed to retry before changing owner");
        }
        string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate();
        var game = data.CreateGameProfile("separate dictionary monitor"); data.ActiveProfileId = game.Id;
        var store = new SettingsStore(dir); var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        try {
            Complete(main.SaveDictionaryPlacementAsync(data.DefaultProfile, Snapshot(-1200, 220, "secondary").Placement));
            Complete(main.SaveDictionaryPlacementAsync(game, Snapshot(400, 350, "primary").Placement));
            var task = store.LoadAsync(); Complete(task); var restored = task.Result;
            Check(restored.DefaultProfile.DictionaryPopupWindow?.MonitorId == "secondary" && restored.DefaultProfile.DictionaryPopupWindow?.X == -1200
                && restored.Profiles.Single(p => p.Id == game.Id).DictionaryPopupWindow?.X == 400 && restored.ActiveProfileId == game.Id,
                "Inactive-profile close save didn't persist independently or changed selected profile");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    private static void ExitSaveDecisions()
    {
        foreach (bool placementFails in new[] { true, false })
        foreach (bool settingsFail in new[] { true, false })
        foreach (var choice in Enum.GetValues<ExitSaveChoice>()) {
            string dir = FeedbackTests.DirectoryPath();
            var store = new SettingsStore(dir);
            var writer = new SaveFeedbackTests.Writer(store) { Fail = settingsFail };
            var data = new AppSettings(); data.Validate();
            var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], persistSettings: writer.Save));
            main.ApplyLoadedSettings(data);
            bool rejectPlacement = placementFails;
            using var tracker = new DictionaryPopupTracker((_, _) => rejectPlacement
                ? Task.FromException(new IOException("synthetic placement failure")) : Task.CompletedTask,
                _ => Snapshot(100, 150, "test"), (_, _) => new Subscription(() => { }));
            try {
                Complete(tracker.PrepareAsync(data.DefaultProfile)); tracker.Attach(new(1), false);
                int decisions = 0;
                var result = main.SaveForExitAsync(tracker, _ => {
                    decisions++;
                    if (choice == ExitSaveChoice.Retry) { rejectPlacement = false; writer.Fail = false; }
                    return choice;
                });
                Complete(result);
                bool failed = placementFails || settingsFail;
                Check(result.Result == (!failed || choice != ExitSaveChoice.Cancel)
                    && decisions == (failed ? 1 : 0), "Exit did not honor placement/settings save decision");
                if (failed && choice == ExitSaveChoice.Retry)
                    Check(writer.DurableWrites == 1, "Exit retry skipped the final settings write");
            } finally { writer.Fail = false; Close(main); Directory.Delete(dir, true); }
        }
    }
    private static DictionaryPopupSnapshot Snapshot(int x, int y, string monitor) => new(
        new WindowPlacement { X = x, Y = y, Width = 500, Height = 600, MonitorId = monitor,
            MonitorDevice = monitor, MonitorWorkX = monitor == "secondary" ? -1600 : 0, MonitorWorkY = 0 }, new(x, y, 500, 600));
    private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
