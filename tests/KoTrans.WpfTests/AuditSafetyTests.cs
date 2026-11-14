using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

// Invoked from the existing LaunchFeedbackTests entry: no new suite or native UI.
internal static class AuditSafetyTests
{
    internal static void Run()
    {
        PreflightContracts();
        StartupRejectsIncompleteInput();
        DormantQaHotkey();
        SaveFailureKeepsLauncher();
        Complete(SettingsSizePolicyAsync());
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        bool rejected = false;
        try { action(); } catch (T) { rejected = true; }
        Check(rejected, message);
    }

    private static void PreflightContracts()
    {
        var p = new Profile { CaptureMode = CaptureMode.Region, CaptureAreaConfigured = false };
        Reject<CaptureUnavailableException>(() => RecognitionPreflight.ValidateCapture(p, CaptureMode.Region),
            "An unconfigured screen rectangle reached capture");
        Reject<CaptureUnavailableException>(() => RecognitionPreflight.ValidateStart(p, true),
            "Automatic start accepted the placeholder rectangle");
        RecognitionPreflight.ValidateStart(p, false); // File-image diagnostics have no desktop area.
        RecognitionPreflight.ValidateCapture(p, CaptureMode.Window); // Whole-window capture remains valid.
        RecognitionPreflight.ValidateCapture(p, CaptureMode.Clipboard);
        p.CaptureAreaConfigured = true;
        RecognitionPreflight.ValidateStart(p, true); // Explicitly selected default-sized rectangle is valid.
        p.AutomaticWatch = false;
        p.Triggers = new() { Enabled = true, Keys = "", MouseButton = "None", GamepadButtons = "" };
        Reject<TranslationException>(() => RecognitionPreflight.ValidateStart(p, true), "Empty input triggers started an idle session");
        p.Triggers.Keys = "Enter"; RecognitionPreflight.ValidateStart(p, true);
        p.Triggers.Keys = ""; p.Triggers.MouseButton = "Left"; RecognitionPreflight.ValidateStart(p, true);
        p.Triggers.MouseButton = "None"; p.Triggers.GamepadButtons = "Joy1"; RecognitionPreflight.ValidateStart(p, true);
        p.Triggers.Enabled = false;
        Reject<TranslationException>(() => RecognitionPreflight.ValidateStart(p, true), "Disabled monitoring was reported as started");
        p.CaptureMode = CaptureMode.Clipboard; p.CaptureAreaConfigured = false;
        RecognitionPreflight.ValidateStart(p, true); // Clipboard must not require screen/triggers.
        p.CaptureMode = CaptureMode.Region; p.CaptureAreaConfigured = true; p.Triggers.Enabled = true;
        p.Triggers.Keys = "NotARealKey";
        Reject<TranslationException>(() => RecognitionPreflight.ValidateStart(p, true), "Malformed saved trigger bypassed preflight");
        Check(true, "Preflight allowed valid screen, window, clipboard, manual and single-input configurations");
    }

    private static void StartupRejectsIncompleteInput()
    {
        string dir = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate();
        settings.DefaultProfile.CaptureMode = CaptureMode.Region;
        settings.DefaultProfile.Translation = new() { Provider = TranslationProvider.OpenAI, Model = "synthetic-only" };
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => []));
        main.ApplyLoadedSettings(settings);
        try {
            int windows = Application.Current.Windows.Count;
            var missing = main.StartTranslationAsync(true); Complete(missing);
            Check(!missing.Result && main.FeedbackToast.Detail.Contains("인식 영역이 지정되지")
                && !main.IsInTray && Application.Current.Windows.Count == windows,
                "Unconfigured area was not rejected before models, credentials and overlay creation");
            var p = settings.DefaultProfile;
            p.CaptureAreaConfigured = true; p.AutomaticWatch = false;
            p.Triggers = new() { Keys = "", MouseButton = "None", GamepadButtons = "" };
            main.ApplyLoadedSettings(settings);
            var empty = main.StartTranslationAsync(true); Complete(empty);
            Check(!empty.Result && main.FeedbackToast.Detail.Contains("입력 트리거를 하나 이상")
                && !main.IsInTray && Application.Current.Windows.Count == windows,
                "Empty triggers reached OCR credentials or created an overlay");
            var dictionary = Named<CheckBox>(main, "WordDictionaryOption");
            Check(!dictionary.IsEnabled && ToolTipService.GetShowOnDisabled(dictionary)
                && dictionary.ToolTip is string hint && hint.Contains("게임에 클릭 전달"),
                "Disabled dictionary control does not explain its dependency");
        } finally { Close(main); Directory.Delete(dir, true); }
    }

    private static void DormantQaHotkey()
    {
        string dir = FeedbackTests.DirectoryPath();
        var profile = new Profile(); var hotkeys = new HotkeyOptions();
        using var editor = new TranslationSettingsEditor(profile, hotkeys, new ApiKeyStore(dir));
        try {
            var explain = Elements<HotkeyCaptureBox>(editor).Single(x => x.Name == "ExplainHotkey");
            var toggle = Elements<HotkeyCaptureBox>(editor).Single(x => x.Name == "ToggleHotkey");
            explain.Text = "F8";
            Check(editor.ApplyChanges(profile, hotkeys) && hotkeys.Explain == "F8" && hotkeys.SaveGameQa == "F8",
                "Dormant QA preference still reserves F8 from a real command");
            var registered = DesktopInput.RegisteredHotkeys(hotkeys);
            Check(registered.Length == 5 && registered.Count(x => x == "F8") == 1,
                "QA key was registered or the real F8 command disappeared");
            toggle.Text = "F8";
            Check(!editor.ApplyChanges(profile, hotkeys) && hotkeys.ToggleTranslation == "Shift+F1",
                "Real command collisions were allowed or partially applied");
            toggle.Text = "Shift+F1";
            Elements<HotkeyCaptureBox>(editor).Single(x => x.Name == "ScrollUpHotkey").Text = "Control+A";
            Elements<HotkeyCaptureBox>(editor).Single(x => x.Name == "ScrollDownHotkey").Text = "Ctrl+A";
            Check(!editor.ApplyChanges(profile, hotkeys), "Normalized active-hotkey aliases bypassed duplicate validation");
        } finally { Directory.Delete(dir, true); }
    }

    private static void SaveFailureKeepsLauncher()
    {
        string dir = FeedbackTests.DirectoryPath(); var store = new SettingsStore(dir);
        var writer = new SaveFeedbackTests.Writer(store); var settings = new AppSettings(); settings.Validate();
        settings.DefaultProfile.CaptureMode = CaptureMode.Clipboard; settings.DefaultProfile.ShowTranslation = false;
        var main = Track(new MainWindow(store, new ApiKeyStore(dir), () => [], persistSettings: writer.Save));
        main.ApplyLoadedSettings(settings); main.AttachTray(new FakeTray());
        var ocr = new UnconfiguredOcrService();
        var pipeline = new TranslationPipeline(ocr, null,
            _ => throw new InvalidOperationException("This fixture must never capture the desktop."),
            settings.DefaultProfile.OcrOptions(), new(), new TranslationSession(), "", translationEnabled: false);
        try {
            main.ObserveTranslation(pipeline, settings.ActiveProfileId, watch: true);
            main.StartProfileMonitoring(pipeline, true, _ => { }); // No native clipboard listener.
            writer.Fail = true; var failed = main.SaveCurrentProfileAsync(); Complete(failed);
            Check(!failed.Result && main.HasSettingsSaveFailure, "Failed save was not recorded");
            Complete(main.StartWithMainHiddenAsync(() => Task.FromResult(true)));
            Check(!main.IsInTray && pipeline.IsRunning, "Partial startup success hid its save failure or stopped valid work");
            Check(main.HideToTray(), "Explicit user hiding should remain available");
            Complete(main.StartWithMainHiddenAsync(() => Task.FromResult(true)));
            Check(!main.IsInTray, "A tray-origin partial start did not restore the error window");
            writer.Fail = false; var saved = main.SaveCurrentProfileAsync(); Complete(saved);
            Check(saved.Result && !main.HasSettingsSaveFailure, "Successful retry did not clear the save failure");
            Complete(main.StartWithMainHiddenAsync(() => Task.FromResult(true)));
            Check(main.IsInTray, "Successful startup no longer follows normal tray policy");
        } finally {
            writer.Fail = false;
            Complete(pipeline.DisposeAsync().AsTask()); Complete(ocr.DisposeAsync().AsTask());
            main.DisposeTray(); Close(main); Directory.Delete(dir, true);
        }
    }

    private static async Task SettingsSizePolicyAsync()
    {
        string dir = FeedbackTests.DirectoryPath(); string path = Path.Combine(dir, "settings.json");
        try {
            var store = new SettingsStore(dir); var good = new AppSettings(); good.Validate();
            await store.SaveAsync(good); good.DefaultProfile.Name = "retained"; await store.SaveAsync(good);
            var original = await File.ReadAllBytesAsync(path); var backup = await File.ReadAllBytesAsync(path + ".bak");
            var models = ImmutableDictionary<string, string>.Empty;
            for (int i = 0; i < 32; i++) models = models.Add("slot-" + i, new string('x', 200));
            var huge = new AppSettings { Profiles = Enumerable.Range(0, 200).Select(i => new Profile {
                Name = "synthetic-" + i, Translation = new TranslationOptions { AdditionalModels = models }
            }).ToList() };
            huge.Validate();
            Check(JsonSerializer.SerializeToUtf8Bytes(huge, new JsonSerializerOptions { WriteIndented = true }).Length > SettingsStore.MaxFileBytes,
                "Oversize fixture did not exceed the shared load/save limit");
            bool rejected = false;
            try { await store.SaveAsync(huge); } catch (InvalidDataException) { rejected = true; }
            var retainedFile = await File.ReadAllBytesAsync(path);
            var retainedBackup = await File.ReadAllBytesAsync(path + ".bak");
            Check(rejected && original.SequenceEqual(retainedFile)
                && backup.SequenceEqual(retainedBackup)
                && !Directory.EnumerateFiles(dir, "settings.*.tmp").Any(),
                "Oversize save changed the file/backup or created a temporary file");
            Check((await new SettingsStore(dir).LoadAsync()).DefaultProfile.Name == "retained", "Rejected save left unreadable persisted settings");
            good.DefaultProfile.Name = "retry"; await store.SaveAsync(good);
            Check((await store.LoadAsync()).DefaultProfile.Name == "retry", "Oversize rejection stranded the save gate");
        } finally { Directory.Delete(dir, true); }
    }

    private sealed class FakeTray : IApplicationTray
    {
        public bool IsAvailable => true;
        public event Action? OpenRequested { add { } remove { } }
        public event Action? ExitRequested { add { } remove { } }
        public void Dispose() { }
    }
}
