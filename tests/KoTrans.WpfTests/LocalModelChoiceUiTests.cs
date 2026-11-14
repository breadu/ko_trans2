using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class LocalModelChoiceUiTests
{
    private sealed class Runtime : ILocalModelRuntime
    {
        internal LocalTranslationModel? Selected; internal int Loads;
        public string Backend => "CPU";
        public Task PrepareAsync(string path, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) { Selected = selection; Loads++; return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken token, LocalTranslationModel? selection = null) { if (selection is null || selection == Selected) Selected = null; return Task.CompletedTask; }
        public Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) => throw new Exception("Settings must not translate");
    }
    private sealed class Download(byte[] bytes) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    }
    private sealed class Ocr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) => throw new Exception("No OCR expected");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static void Run()
    {
        const LocalTranslationModel standard = LocalTranslationModel.Standard7B, light = LocalTranslationModel.Lightweight18B;
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.LocalChoiceUi", Guid.NewGuid().ToString("N"));
        byte[] bytes = "GGUF synthetic selection model"u8.ToArray();
        LocalModelSpec Spec(string name) => new("fixture", name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new("https://huggingface.co/fixture/model"));
        using var handler = new Download(bytes); using var http = new HttpClient(handler);
        var std = new LocalModelInstaller(Path.Combine(root, "models"), Spec("standard.gguf"), http, () => long.MaxValue);
        var tiny = new LocalModelInstaller(Path.Combine(std.DirectoryPath, "1.8B"), Spec("light.gguf"), http, () => long.MaxValue);
        var runtime = new Runtime(); var settings = new AppSettings(); settings.Validate();
        var profile = settings.DefaultProfile; profile.Translation.Provider = TranslationProvider.HyMtLocal;
        var store = new SettingsStore(root); var main = Track(new MainWindow(store, new ApiKeyStore(root), () => []));
        main.LocalPreparationFactoryOverride = model => new(model == light ? tiny : std, runtime, model);
        string question = ""; bool confirm = true;
        main.ConfirmLocalOverride = (_, _, text, _) => { question = text; return confirm; };
        main.LocalProgressOverride = _ => { };
        main.ApplyLoadedSettings(settings);
        var window = Track(main.CreateTranslationSettings());
        try {
            Layout(window, 850, 900);
            var choice = Named<ComboBox>(window, "LocalModelChoice"); var action = Named<Button>(window, "LocalModelAction");
            Until(() => action.IsEnabled, "Default local state not queried");
            Check(Equals(choice.SelectedValue, standard) && choice.Items.Count == 2 && profile.Translation.LocalModel == standard, "7B not the default UI choice");
            choice.SelectedValue = light; Until(() => action.IsEnabled, "Light state not queried");
            Check(Named<TextBlock>(window, "LocalModelDescription").Text.Contains("잘못") && Named<TextBlock>(window, "LocalModelState").Text.Contains("1.1GB"), "Light download size/quality warning missing");
            Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => Equals(action.Content, "삭제") && action.IsEnabled, "Light install state stale");
            Check(runtime.Selected == light && handler.Calls == 1 && question.Contains("1.8B") && question.Contains("잘못") && File.Exists(tiny.ModelPath) && !File.Exists(std.ModelPath), "Installed/prepared the wrong model or hid warning");
            Check(window.ApplyChanges(profile, settings.Hotkeys) && profile.Translation.LocalModel == light, "Light selection not saved to profile");
            var active = main.CreateProfilePipeline(new Ocr(), new MockTranslationService(), _ => throw new Exception("No capture expected"));
            main.ObserveTranslation(active, profile.Id, watch: true); active.Start(false);
            choice.SelectedValue = standard; Until(() => action.IsEnabled, "Standard state not queried");
            Check(!active.IsRunning && runtime.Selected is null && runtime.Loads == 1, "Changing model did not stop/release previous translation or loaded automatically");
            Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => Equals(action.Content, "삭제") && action.IsEnabled, "Standard install unavailable");
            Check(File.Exists(tiny.ModelPath) && File.Exists(std.ModelPath) && runtime.Selected == standard && handler.Calls == 2, "Second installation replaced/downloaded another model");
            confirm = false; Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => action.IsEnabled, "Declined deletion state not refreshed");
            Check(File.Exists(std.ModelPath) && File.Exists(tiny.ModelPath), "Declined model deletion removed data");
            confirm = true; Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => Equals(action.Content, "설치") && action.IsEnabled, "Delete did not restore Install");
            Check(!File.Exists(std.ModelPath) && File.Exists(tiny.ModelPath) && question.Contains("다른 모델은 보존"), "7B deletion damaged light model");
            Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => Equals(action.Content, "삭제") && action.IsEnabled, "Reinstall failed");
            choice.SelectedValue = light; Until(() => Equals(action.Content, "삭제") && action.IsEnabled, "Installed light model required redownload");
            Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => Equals(action.Content, "설치") && action.IsEnabled, "Light deletion failed");
            Check(File.Exists(std.ModelPath) && !File.Exists(tiny.ModelPath), "Light deletion damaged 7B");
            Check(window.ApplyChanges(profile, settings.Hotkeys), "Light profile apply failed");
            Complete(store.SaveAsync(settings)); var loaded = store.LoadAsync(); Complete(loaded);
            Check(loaded.Result.DefaultProfile.Translation.LocalModel == light, "Saved light selection did not survive restart");
            using (var dark = new TranslationSettingsEditor(loaded.Result.DefaultProfile, loaded.Result.Hotkeys, new ApiKeyStore(root), darkTheme: true)) {
                var host = Track(new System.Windows.Window { Content = dark });
                try {
                    Layout(host, 750, 1000); var restoredChoice = Named<ComboBox>(host, "LocalModelChoice");
                    Check(Equals(restoredChoice.SelectedValue, light), "Restarted editor did not restore light choice");
                    var caption = (TextBlock)restoredChoice.ItemTemplate.LoadContent(); caption.DataContext = LocalTranslationModels.Get(light);
                    caption.Measure(new System.Windows.Size(600, 100)); caption.Arrange(new System.Windows.Rect(0, 0, 600, 100)); caption.UpdateLayout(); Pump();
                    Check(caption.Text.Contains("1.8B") && Equals(caption.Foreground, System.Windows.Application.Current.FindResource("ComboBoxInk")), "Dark editor model choice has unreadable selected text");
                } finally { Close(host); }
            }
            Complete(active.DisposeAsync().AsTask());
        } finally { Close(window); Close(main); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
