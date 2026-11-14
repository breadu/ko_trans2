using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class LocalModelManagementUiTests
{
    private sealed class Runtime : ILocalModelRuntime
    {
        public int Loads, Unloads;
        public string Backend => "CPU";
        public Task PrepareAsync(string model, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) { Loads++; return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken token, LocalTranslationModel? selection = null) { Unloads++; return Task.CompletedTask; }
        public Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) => throw new Exception("Settings must not start translation");
    }
    private sealed class Download(byte[] data) : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource? Wait;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; if (Wait is not null) await Wait.Task.WaitAsync(token); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; }
    }
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.LocalManage", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        byte[] data = "GGUF-small-model-for-background"u8.ToArray();
        var spec = new LocalModelSpec("fixed", "test.gguf", data.Length, Convert.ToHexString(SHA256.HashData(data)), new("https://huggingface.co/fixture/test.gguf"));
        using var download = new Download(data); using var http = new HttpClient(download);
        var installer = new LocalModelInstaller(Path.Combine(root, "models"), spec, http, () => long.MaxValue); var runtime = new Runtime();
        var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile; profile.CaptureMode = CaptureMode.Clipboard;
        profile.Translation.Provider = TranslationProvider.Gemini;
        var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => []));
        main.PresentStartingOverlayOverride = _ => { };
        main.LocalPreparationOverride = new(installer, runtime);
        bool confirm = true; int installQuestions = 0, deleteQuestions = 0, automaticStarts = 0;
        main.ConfirmLocalOverride = (_, _, _, deleting) => { if (deleting) deleteQuestions++; else installQuestions++; return confirm; };
        main.LocalProgressOverride = _ => { };
        main.StartPreparedLocalOverride = () => { automaticStarts++; return Task.FromResult(true); };
        main.ApplyLoadedSettings(settings);
        var window = Track(main.CreateTranslationSettings());
        var confirmation = Track(new LocalConfirmationWindow("설치 확인", "설치 안내"));
        var deletion = Track(new LocalConfirmationWindow("삭제 확인", "삭제 안내", true));
        var progress = Track(new LocalPreparationWindow(() => { }));
        var overlay = Track(new TranslationWindow(profile)); Track(overlay.Chrome);
        try
        {
            foreach (var prompt in new[] { confirmation, deletion }) {
                Layout(prompt, 450, 260);
                var yes = Named<Button>(prompt, "ConfirmLocalYes"); var no = Named<Button>(prompt, "ConfirmLocalNo");
                Check(yes.ActualHeight > 0 && Math.Abs(yes.ActualHeight - no.ActualHeight) < .1,
                    "Confirmation Yes/No buttons have unequal rendered heights");
                Check(Math.Abs(yes.TranslatePoint(new Point(), (UIElement)prompt.Content).Y - no.TranslatePoint(new Point(), (UIElement)prompt.Content).Y) < .1,
                    "Confirmation Yes/No buttons are vertically misaligned");
            }
            CheckPreparationActions(progress);
            Check(confirmation.Topmost && deletion.Topmost && progress.Topmost, "Local dialogs can hide behind a game");
            Check(Named<Button>(deletion, "ConfirmLocalNo").IsDefault, "Delete confirmation does not default to No");
            var provider = Named<ComboBox>(window, "Provider"); var action = Named<Button>(window, "LocalModelAction");
            Check(Named<StackPanel>(window, "LocalModelPanel").Visibility == Visibility.Collapsed, "Cloud provider shows local controls");
            provider.SelectedItem = TranslationProvider.HyMtLocal;
            Until(() => action.IsEnabled, "Local install state not available");
            Check(Equals(action.Content, "설치") && Text(window).Contains("모델 관리"), "Local model management action missing");
            Check(Named<TextBlock>(window, "ProviderHelp").Text ==
                  "인터넷 연결 없이 이 PC에서 번역합니다. 문장 해설은 지원하지 않습니다.",
                  "Local provider explanation should not duplicate model-size and installation details");
            var card = Named<Border>(window, "LocalModelCard");
            Check(card.Visibility == Visibility.Visible && card.BorderThickness.Left > 0 && card.BorderBrush is not null && card.Background is not null
                && card.Padding.Left >= 12 && Elements<Button>(card).Contains(action), "Local model controls lack a distinct padded card");
            Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => Equals(action.Content, "삭제") && action.IsEnabled, "Install did not turn into Delete");
            Check(download.Calls == 1 && runtime.Loads == 1 && installQuestions == 1 && automaticStarts == 0, "Settings install did not reuse preparation or started translation");
            Check(profile.Translation.Provider == TranslationProvider.Gemini, "Installing an unsaved local selection overwrote the profile");
            confirm = false; Click(action); Complete(window.Editor.LocalModelActionTask);
            Until(() => action.IsEnabled, "Declined removal did not restore action");
            Check(File.Exists(installer.ModelPath) && runtime.Unloads == 0 && Equals(action.Content, "삭제"), "Declined delete unloaded/removed the model");
            confirm = true; Click(action); Complete(window.Editor.LocalModelActionTask);
            Until(() => Equals(action.Content, "설치") && action.IsEnabled, "Removal did not turn into Install");
            Check(runtime.Unloads == 1 && !Directory.Exists(installer.DirectoryPath) && deleteQuestions == 2, "Confirmed removal did not unload/remove all managed model data");
            Click(action); Complete(window.Editor.LocalModelActionTask); Until(() => Equals(action.Content, "삭제") && action.IsEnabled, "Reinstall failed");
            Check(download.Calls == 2 && runtime.Loads == 2, "Reinstall did not prepare the model again");
            Check(window.ApplyChanges(profile, settings.Hotkeys), "Local profile Apply failed");
            main.ApplyLoadedSettings(settings);
            main.ShowProfileSettingsPage(2);
            var darkCard = Named<Border>(main, "LocalModelCard");
            Check(darkCard.Visibility == Visibility.Visible && darkCard.Background is System.Windows.Media.SolidColorBrush background
                && card.Background is System.Windows.Media.SolidColorBrush lightBackground && background.Color != lightBackground.Color, "Model card does not follow the dark settings theme");
            main.ConfirmLocalOverride = (_, _, _, _) => throw new Exception("Installed model asked consent");
            main.LocalProgressOverride = _ => throw new Exception("Installed model opened progress");
            var start = main.StartTranslationAsync(true); Complete(start); Pump();
            Check(start.Result && automaticStarts == 1, "Installed model did not start silently");
            foreach (var service in new[] { TranslationProvider.Gemini, TranslationProvider.OpenAI, TranslationProvider.Ollama, TranslationProvider.DeepL }) {
                overlay.SetService(service); Check(Named<Button>(overlay.Chrome, "OverlayService").MinWidth == 64, "Other service width changed");
            }
            overlay.SetService(TranslationProvider.HyMtLocal); Layout(overlay, 430, 180); Layout(overlay.Chrome, 430, 180);
            var badge = Named<Button>(overlay.Chrome, "OverlayService");
            Check(badge.MinWidth == 112 && badge.ActualWidth >= 112, "Local badge still hugs its text");
            overlay.SetService(TranslationProvider.Gemini);
            Check(badge.MinWidth == 64, "Width did not restore after local provider");
            provider.SelectedItem = TranslationProvider.Gemini;
            Check(Named<StackPanel>(window, "LocalModelPanel").Visibility == Visibility.Collapsed && card.Visibility == Visibility.Collapsed,
                "Local card stayed visible after provider switch");
            main.ConfirmLocalOverride = (_, _, _, _) => true;
            var removal = main.ManageLocalModelAsync(true, profile.Id, main); Complete(removal);
            Check(removal.Result, "Cleanup before pending installation test failed");
            var pendingWindow = Track(main.CreateTranslationSettings());
            try {
                var pendingAction = Named<Button>(pendingWindow, "LocalModelAction");
                Until(() => pendingAction.IsEnabled, "Pending installation action unavailable");
                download.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Click(pendingAction); var pending = pendingWindow.Editor.LocalModelActionTask;
                Until(() => download.Calls == 3, "Pending installation did not reach download");
                Check(!pendingAction.IsEnabled && !Named<ComboBox>(pendingWindow, "Provider").IsEnabled, "Installation allows duplicate clicks/provider edits");
                Close(pendingWindow); Complete(pending);
                Check(pending.IsCompletedSuccessfully && !File.Exists(installer.ModelPath), "Closing settings did not cancel installation cleanly");
            } finally { download.Wait?.TrySetResult(); Close(pendingWindow); }
        }
        finally { Close(overlay); Close(overlay.Chrome); Close(progress); Close(confirmation); Close(deletion); Close(window); Close(main); Directory.Delete(root, true); }
    }

    private static void CheckPreparationActions(LocalPreparationWindow progress)
    {
        Layout(progress, 426, 220);
        var dismiss = Button(progress, "취소");
        var originalSize = dismiss.RenderSize;
        var choice = progress.RetryAsync("테스트 모델을 준비하지 못했습니다.");
        Layout(progress, 426, 220);
        var retry = Button(progress, "다시 시도");
        var footer = (UIElement)retry.Parent;
        Check(dismiss.Content as string == "닫기" && originalSize.Width > 0 && originalSize.Height > 0
            && dismiss.RenderSize == originalSize && retry.ActualHeight == dismiss.ActualHeight
            && retry.ActualWidth > dismiss.ActualWidth
            && retry.TranslatePoint(new Point(), footer).Y == dismiss.TranslatePoint(new Point(), footer).Y,
            "Local preparation buttons must fit their labels with matching heights and vertical alignment");
        var caption = new System.Windows.Media.FormattedText((string)retry.Content,
            System.Globalization.CultureInfo.GetCultureInfo("ko-KR"), retry.FlowDirection,
            new System.Windows.Media.Typeface(retry.FontFamily, retry.FontStyle, retry.FontWeight, retry.FontStretch),
            retry.FontSize, System.Windows.Media.Brushes.White, 1);
        Check(caption.WidthIncludingTrailingWhitespace + retry.Padding.Left + retry.Padding.Right <= retry.ActualWidth,
            "The retry button clips its label");
        Click(retry);
        Check(choice.IsCompletedSuccessfully && choice.Result && retry.Visibility == Visibility.Collapsed && Equals(dismiss.Content, "취소"),
            "Retry did not return the preparation window to its running state");
    }
}
