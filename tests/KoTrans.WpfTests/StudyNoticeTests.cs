using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class StudyNoticeTests
{
    internal static void EmptySourceCard()
    {
        var reader = Track(new StudyWindow(new AppSettings(), resolveVoiceDirectory: () => null));
        try {
            var source = Named<Border>(reader, "StudySourcePanel");
            var feedback = Named<TextBlock>(reader, "StudyFeedback");
            Check(source.Visibility == Visibility.Collapsed, "New reader showed an empty source card");
            reader.ShowNotice("원문 언어 또는 읽기 설정이 변경되어 이전 문장 해설을 비웠습니다");
            Layout(reader, 500, 600);
            Check(source.Visibility == Visibility.Collapsed && source.DesiredSize.Height == 0
                && feedback.Visibility == Visibility.Visible, "Notice-only reader reserved space for an empty source card");
            reader.SetSource(" \r\n\t"); reader.ShowError("원문을 가져오지 못했습니다."); reader.FinishBusy();
            Check(source.Visibility == Visibility.Collapsed && feedback.Visibility == Visibility.Visible,
                "Whitespace-only error showed an empty source card or hid the error");
            reader.SetSource("A new sentence.", "en"); reader.SetBusy(true, "synthetic request");
            Check(source.Visibility == Visibility.Visible && Named<TextBlock>(reader, "StudyOriginalText").Text == reader.Input,
                "New source did not restore its card while loading");
            reader.ShowError("해설을 가져오지 못했습니다."); reader.FinishBusy();
            Check(source.Visibility == Visibility.Visible && feedback.Visibility == Visibility.Visible,
                "Explanation failure hid the available source");
            reader.SetSource(""); reader.Clear(); reader.ShowNotice("원문을 다시 가져와 주세요.");
            Check(source.Visibility == Visibility.Collapsed && feedback.Visibility == Visibility.Visible,
                "Clearing the source left its card behind");
            reader.SetSource("Another sentence.", "en"); reader.Render(WpfTests.Example()); reader.FinishBusy();
            Layout(reader, 500, 600);
            Check(source.Visibility == Visibility.Visible && source.DesiredSize.Height > 0
                && feedback.Visibility == Visibility.Collapsed, "New explanation did not restore the source card and replace the notice");
        } finally { Complete(reader.CloseForExitAsync()); }
    }

    internal static void SettingsInvalidation()
    {
        string directory = FeedbackTests.DirectoryPath(); var settings = new AppSettings(); settings.Validate();
        settings.Hotkeys.Explain = "Alt+F7";
        settings.DefaultProfile.Translation.Provider = TranslationProvider.Gemini;
        settings.DefaultProfile.Translation.Model = settings.DefaultProfile.Translation.GeminiModel = "synthetic-gemini";
        settings.DefaultProfile.Translation.OpenAIModel = "synthetic-openai";
        var other = settings.CreateGameProfile("another game");
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => [])); main.ApplyLoadedSettings(settings);
        var reader = main.EnsureStudy();
        var engine = new UnconfiguredOcrService();
        var pipeline = new TranslationPipeline(engine, new MockTranslationService(), _ => throw new Exception("unexpected capture"),
            new("en", "ADV", false), new(), new(), "synthetic");
        try {
            reader.SetSource("Previous English source.", "en"); reader.Render(WpfTests.Example()); reader.SetBusy(true, "pending mock analysis");
            main.ObserveTranslation(pipeline, settings.ActiveProfileId, watch: true); pipeline.Start(false);
            int registrations = 0;
            main.RegisterSettingsHotkeys = _ => ++registrations == 1 ? "단축키 등록 실패 (합성)" : "";
            main.ShowProfileSettingsPage(7);
            Named<TextBox>(main, "ScrollUpHotkey").Text = "Shift+F9";
            Complete(main.SaveWithFeedbackAsync());
            Check(pipeline.IsRunning && reader.Input.Contains("Previous") && registrations == 1
                && main.FeedbackToast.Detail.Contains("단축키 등록 실패"), "Study shortcut interrupted translation or hid registration failure");
            Complete(main.SaveWithFeedbackAsync());
            Check(pipeline.IsRunning && registrations == 2 && !main.FeedbackToast.Detail.Contains("단축키 등록 실패"), "Retry failed to recover shortcut registration");
            main.ShowProfileSettingsPage(2);
            var timeout = Named<TextBox>(main, "Timeout"); string initially = timeout.Text;
            timeout.Text = "15"; timeout.Text = initially;
            Complete(main.SaveWithFeedbackAsync());
            Check(pipeline.IsRunning && reader.Input.Contains("Previous") && registrations == 2, "Reverted form edits interrupted the live pipeline");
            Named<ComboBox>(main, "Provider").SelectedItem = TranslationProvider.OpenAI;
            var save = main.SaveWithFeedbackAsync(); Complete(save);
            Check(!pipeline.IsRunning && main.FeedbackToast.Detail.Contains("다시 시작"), "Changed provider kept old session or hid restart guidance");
            Check(save.Result, "Provider-change fixture did not save");
            Layout(reader, 500, 600);
            var feedback = Named<TextBlock>(reader, "StudyFeedback");
            Check(feedback.Visibility == Visibility.Visible && feedback.Text.Contains("번역 제공자가 변경되어")
                && feedback.Text.Contains("OpenAI · synthetic-openai") && feedback.Text.Contains("Alt+F7"),
                "Provider change left a blank reader without the reason, service/model or actual configured shortcut");
            Check(reader.Input == "" && !Text(reader).Contains("Previous English source.") && !Text(reader).Contains("journey")
                && !reader.IsLoadingAnimationRunning, "Provider change left previous data or spinner behind");
            Check(Named<Border>(reader, "StudySourcePanel").Visibility == Visibility.Collapsed,
                "Settings invalidation left an empty source card behind");
            Check(feedback.Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(0xA5, 0xBC, 0xD1),
                "Normal settings invalidation was incorrectly styled as a service error");
            string providerNotice = feedback.Text;
            reader.FinishBusy(); reader.Close();
            Check(feedback.Visibility == Visibility.Visible && feedback.Text == providerNotice,
                "Stopping/hiding discarded the explanation for an empty reader");
            main.ShowProfileSettingsPage(7); main.ShowProfileSettingsPage(2);
            save = main.SaveCurrentProfileAsync(); Complete(save);
            Check(feedback.Text == providerNotice, "Unchanged navigation/save replaced the invalidation reason");

            Named<TextBox>(main, "Model").Text = "new-openai-model";
            save = main.SaveCurrentProfileAsync(); Complete(save);
            Check(save.Result && feedback.Text.Contains("번역 모델이 변경되어")
                && feedback.Text.Contains("OpenAI · new-openai-model"), "Model-only change used a wrong or stale reason");
            reader.SetBusy(true, "new analysis");
            Check(feedback.Text.Contains("가져오는 중") && !feedback.Text.Contains("비웠습니다"), "Starting a new request retained old guidance");
            reader.SetSource("Next source.", "en"); reader.Render(WpfTests.Example("새로운 해석")); reader.FinishBusy();
            Check(feedback.Visibility == Visibility.Collapsed && Text(reader).Contains("새로운 해석"), "New result did not replace the settings notice");
            Check(Named<Border>(reader, "StudySourcePanel").Visibility == Visibility.Visible,
                "New result after settings invalidation did not restore its source card");
            save = main.SwitchProfileAsync(other); Complete(save);
            Check(save.Result && feedback.Visibility == Visibility.Visible && feedback.Text.Contains("프로필이 변경되어"),
                "Profile switch left an unexplained blank study reader");
            Check(!reader.IsVisible && new WindowInteropHelper(reader).Handle == IntPtr.Zero,
                "Notice created a native window or reopened a hidden reader");
        } finally { Complete(reader.CloseForExitAsync()); Close(main); Complete(pipeline.DisposeAsync().AsTask()); Complete(engine.DisposeAsync().AsTask()); Directory.Delete(directory, true); }
    }
}
