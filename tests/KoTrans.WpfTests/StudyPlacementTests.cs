using System.IO;
using System.Windows;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class StudyPlacementTests
{
    private static readonly DisplayArea Primary = new("main", "DISPLAY1", new(0, 0, 2560, 1400), 1, true);
    private static readonly DisplayArea Portrait = new("portrait", "DISPLAY2", new(-1600, -100, 1600, 2500), 2.5);
    private static WindowPlacement Placement(int x, int y, DisplayArea display, double width = 500, double height = 600) => new() {
        X = x, Y = y, Width = width, Height = height, MonitorId = display.Id, MonitorDevice = display.Device,
        MonitorWorkX = display.WorkArea.X, MonitorWorkY = display.WorkArea.Y
    };
    // Synthetic native boundary only; all restore decisions use the production policy.
    private static PlacementResolution Restore(WindowPlacement saved, IReadOnlyList<DisplayArea> displays, out WindowPlacement snapshot)
    {
        var result = OverlayPlacementPolicy.Resolve(saved, displays);
        var monitor = displays.First(d => result.X >= d.WorkArea.X && result.Y >= d.WorkArea.Y
            && result.X < (long)d.WorkArea.X + d.WorkArea.Width && result.Y < (long)d.WorkArea.Y + d.WorkArea.Height);
        snapshot = Placement(result.X, result.Y, monitor, result.Width, result.Height);
        return result;
    }
    internal static void CloseAndProfiles()
    {
        string directory = FeedbackTests.DirectoryPath();
        var data = new AppSettings { StudyWindow = Placement(-1500, 300, Portrait) }; data.Validate();
        var first = data.CreateGameProfile("first"); var second = data.CreateGameProfile("second");
        second.StudyWindow = Placement(400, 220, Primary, 720, 650); data.ActiveProfileId = first.Id;
        var store = new SettingsStore(directory); Task pending = Task.CompletedTask; int saves = 0; bool failSave = false;
        WindowPlacement? snapshot = null;
        var window = Track(new StudyWindow(data, resolveVoiceDirectory: () => null, profile: first,
            capturePlacement: () => snapshot, restorePlacement: saved => {
                var resolved = Restore(saved, [Primary, Portrait], out var native); snapshot = native; return resolved;
            }));
        window.PreferencesChanged += () => { saves++; pending = failSave ? Task.FromException(new IOException("synthetic save failure")) : store.SaveAsync(data); };
        try {
            Check(ReferenceEquals(window.SavedPlacement, data.StudyWindow), "Legacy global study placement was not retained for an old profile");
            window.CommitPlacement(); Check(saves == 0 && first.StudyWindow is null, "Never-shown reader created a synthetic saved position");
            var restored = window.RestoreSavedPlacement();
            Check(restored.X == -1500 && restored.Y == 300 && !restored.Temporary
                && window.Width == 500 && window.Height == 600, "Study monitor or DIP size was not restored");
            snapshot = Placement(-1300, 400, Portrait, 520, 630); window.UserPlacementChanged();
            Check(saves == 0, "Study wrote settings for every drag/resize instead of close");
            window.Close(); Complete(pending);
            Check(first.StudyWindow is { X: -1300, Y: 400, Width: 520, Height: 630, MonitorId: "portrait", MonitorWorkX: -1600 }
                && second.StudyWindow.X == 400 && data.StudyWindow.X == -1500 && saves == 1,
                "Closing a non-default profile did not save its own monitor/size or changed another profile/legacy data");
            var read = store.LoadAsync(); Complete(read);
            var loaded = read.Result.Profiles.Single(p => p.Id == first.Id).StudyWindow!;
            Check(loaded.MonitorDevice == "DISPLAY2" && loaded.MonitorWorkY == -100 && loaded.Height == 630,
                "Study close-save did not persist monitor origin, device and size");
            restored = window.RestoreSavedPlacement();
            Check(restored.X == -1300 && restored.Y == 400 && window.Width == 520 && window.Height == 630,
                "Reusing hidden reader did not restore its latest closed placement");
            window.BindProfile(second);
            window.CommitPlacement();
            Check(ReferenceEquals(window.SavedPlacement, second.StudyWindow) && second.StudyWindow.X == 400 && saves == 1,
                "Hidden profile rebind saved the previous profile's HWND rectangle into the new profile");
            window.RestoreSavedPlacement(); snapshot = Placement(500, 350, Primary, 740, 680);
            window.UserPlacementChanged(); window.Close(); Complete(pending);
            Check(second.StudyWindow is { X: 500, Y: 350, Width: 740, MonitorId: "main" }
                && first.StudyWindow!.X == -1300 && saves == 2, "Resident reader mixed profiles after a second close");
            failSave = true; snapshot = Placement(600, 360, Primary, 740, 680);
            window.UserPlacementChanged(); window.Close();
            try { Complete(pending); throw new Exception("Synthetic save failure ignored"); }
            catch (IOException) { Check(saves == 3, "Close did not attempt the failed save"); }
            failSave = false; window.Close(); Complete(pending);
            read = store.LoadAsync(); Complete(read);
            Check(saves == 4 && read.Result.Profiles.Single(p => p.Id == second.Id).StudyWindow?.X == 600,
                "Unchanged close did not retry a failed placement save");
            var valid = second.StudyWindow; second.StudyWindow = new() { Width = double.NaN };
            try { data.Validate(); throw new Exception("Invalid profile study placement accepted"); }
            catch (InvalidDataException) { Check(true, "Invalid placement rejected"); }
            finally { second.StudyWindow = valid; }
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero && !window.IsVisible,
                "Background placement test created a native/visible study window");
        } finally { Close(window); Directory.Delete(directory, true); }

        directory = FeedbackTests.DirectoryPath();
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => []));
        try {
            main.ApplyLoadedSettings(data); var reader = main.EnsureStudy();
            Check(ReferenceEquals(reader.SavedPlacement, first.StudyWindow), "MainWindow did not select the active study profile");
            Complete(main.SwitchProfileAsync(second));
            Check(ReferenceEquals(main.EnsureStudy(), reader) && ReferenceEquals(reader.SavedPlacement, second.StudyWindow)
                && reader.Width == 740 && reader.Height == 680, "Actual profile-switch path recreated the reader or kept the old size");
            Complete(reader.CloseForExitAsync());
        } finally { Close(main); Directory.Delete(directory, true); }
    }
    internal static void MonitorChanges()
    {
        var data = new AppSettings(); data.Validate();
        var profile = data.DefaultProfile; profile.StudyWindow = Placement(-1500, 300, Portrait);
        IReadOnlyList<DisplayArea> displays = [Primary]; WindowPlacement? snapshot = null; int saves = 0;
        var window = Track(new StudyWindow(data, resolveVoiceDirectory: () => null, profile: profile,
            capturePlacement: () => snapshot, restorePlacement: saved => {
                var resolved = Restore(saved, displays, out var native); snapshot = native; return resolved;
            }));
        window.PreferencesChanged += () => saves++;
        try {
            var fallback = window.RestoreSavedPlacement();
            window.BeginPlacementEdit(); window.EndPlacementEdit(); window.Close();
            Check(fallback.Temporary && fallback.X >= 0 && fallback.Y >= 0 && saves == 0
                && profile.StudyWindow.MonitorId == "portrait", "Unedited missing-monitor fallback destroyed the stored study position");
            displays = [Primary, Portrait]; var restored = window.RestoreSavedPlacement();
            Check(!restored.Temporary && restored.X == -1500 && restored.Y == 300 && window.Width == 500,
                "Reconnected portrait monitor was not used on next display");
            displays = [Primary, Portrait with { Device = "DISPLAY3", WorkArea = new(2560, 0, 1600, 2500), DpiScale = 2 }];
            restored = window.RestoreSavedPlacement();
            Check(restored.X == 2660 && restored.Y == 400 && restored.Width == 500 && restored.Height == 600,
                "Physical monitor identity, changed origin or mixed DPI lost relative study placement");
            displays = [Primary, Portrait with { WorkArea = new(-1600, 0, 1200, 1400) }];
            restored = window.RestoreSavedPlacement();
            Check(restored.Width * 2.5 <= 1200 && restored.Height * 2.5 <= 1400 && restored.Y >= 0,
                "Study restore exceeded a reduced monitor work area");
            displays = [Primary]; window.RestoreSavedPlacement();
            window.BeginPlacementEdit(); snapshot = Placement(200, 180, Primary, 560, 610); window.EndPlacementEdit(); window.Close();
            Check(profile.StudyWindow is { MonitorId: "main", X: 200, Width: 560 } && saves == 1,
                "User-edited temporary fallback was not saved on the selected monitor");
            window.RestoreSavedPlacement(); snapshot = Placement(300, 280, Primary, 620, 650); window.UserPlacementChanged();
            snapshot = null; window.WindowState = WindowState.Minimized; window.Close();
            Check(profile.StudyWindow is { X: 300, Y: 280, Width: 620, Height: 650 } && saves == 2,
                "Minimized close discarded the last normal study rectangle");
            window.WindowState = WindowState.Normal; window.RestoreSavedPlacement();
            snapshot = null; window.WindowState = WindowState.Maximized; window.CommitPlacement();
            Check(profile.StudyWindow.Width == 620 && profile.StudyWindow.Height == 650 && saves == 2,
                "Maximized dimensions replaced normal saved study size");
        } finally { Close(window); }
    }
}
