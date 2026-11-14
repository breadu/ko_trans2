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

internal static class ProfileUsabilityTests
{
    internal static void CaptureModesAndMap()
    {
        string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate(); var p = data.DefaultProfile;
        p.GameWindow = new("fake-game", "", "class", "Saved game"); p.CaptureAreaConfigured = true;
        p.CaptureArea = new(-1400, 200, 500, 250); p.WindowCaptureRegion = new(100, 80, 500, 250);
        var fake = new WindowTarget(new IntPtr(42), 42, 42, "Saved game", "fake-game", "", "class");
        var available = new List<WindowTarget> { fake };
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => available, _ => new(0, 0, 1280, 720)));
        main.ApplyLoadedSettings(data); main.SetCaptureMapDisplays(() => [new(-1600, 0, 1600, 2560), new(0, 0, 2560, 1440)]);
        try {
            var combo = Named<ComboBox>(main, "Profiles");
            var item = new ComboBoxItem { Style = combo.ItemContainerStyle, Content = "Test profile", FontSize = combo.FontSize, FontFamily = combo.FontFamily };
            item.Measure(new Size(300, 100)); item.Arrange(new Rect(item.DesiredSize)); item.UpdateLayout();
            var row = (Border)VisualTreeHelper.GetChild(item, 0);
            Check(row.Background is SolidColorBrush { Color: var color } && color == Colors.White
                && row.Padding == new Thickness(7, 4, 7, 4)
                && Math.Abs(item.DesiredSize.Height - row.Child.DesiredSize.Height - 10) < .1,
                "Profile dropdown lost its original row spacing or plain white background");
            var captureMode = Named<ComboBox>(main, "InputMode");
            var captureCard = Named<Border>(main, "GameCaptureCard");
            Check(Elements<TextBlock>(captureCard).Any(t => t.Text == "캡처 대상")
                && System.Windows.Automation.AutomationProperties.GetName(captureMode) == "캡처 대상"
                && captureMode.Items.Count == 3
                && Equals(((ComboBoxItem)captureMode.Items[0]).Content, "게임 창")
                && Equals(((ComboBoxItem)captureMode.Items[1]).Content, "화면 영역")
                && Equals(((ComboBoxItem)captureMode.Items[2]).Content, "클립보드")
                && p.CaptureMode == CaptureMode.Window
                && Named<TextBlock>(main, "CaptureModeHelp").Text.Contains("창을 옮겨도"),
                "Capture mode should use user-facing capture target/game window labels");
            var previewFrame = Named<Border>(main, "CaptureMapFrame");
            var placeholder = Named<StackPanel>(main, "CaptureMapEmptyState");
            var emptyReason = Named<TextBlock>(main, "CaptureMapEmptyReason");
            foreach (var mode in new[] { CaptureMode.Region, CaptureMode.Clipboard, CaptureMode.Window, CaptureMode.Region, CaptureMode.Window }) {
                captureMode.SelectedIndex = mode switch { CaptureMode.Window => 0, CaptureMode.Region => 1, _ => 2 }; main.ShowProfileSettingsPage(1); Layout(main, 1060, 740);
                Check(Named<Button>(main, "PickWindowButton").IsEnabled == (mode == CaptureMode.Window)
                    && Named<Button>(main, "PickWindowDirectButton").IsEnabled == (mode == CaptureMode.Window)
                    && Named<Button>(main, "SelectAreaButton").IsEnabled == (mode != CaptureMode.Clipboard), "Capture controls do not follow selected mode");
                Check(p.GameWindow == fake.Identity && p.WindowCaptureRegion == new PixelRect(100, 80, 500, 250), "Mode switch discarded saved target/crop");
                var label = Named<TextBlock>(main, "TargetLabel").Text;
                Check(mode switch { CaptureMode.Region => label.Contains("바탕화면") && !label.Contains("연결 대기"),
                    CaptureMode.Clipboard => label.Contains("후킹") && !label.Contains("대상 게임"),
                    _ => label.Contains("대상 게임") }, "Capture mode has misleading target description");
                var map = Named<CaptureAreaMap>(main, "AreaMap");
                Check(previewFrame.Visibility == Visibility.Visible && previewFrame.ActualWidth == 180
                    && previewFrame.ActualHeight == 110, "Fixed capture preview frame disappeared or changed dimensions");
                Check(map.Visibility == (mode == CaptureMode.Clipboard ? Visibility.Collapsed : Visibility.Visible), "Capture map visibility incorrect");
                Check(placeholder.Visibility == (map.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible),
                    "Missing geometry should switch to an empty state inside the same frame");
                if (mode == CaptureMode.Clipboard)
                    Check(emptyReason.Text == "클립보드 입력", "Clipboard placeholder reason missing");
                if (map.Visibility == Visibility.Visible) {
                    new RenderTargetBitmap(180, 110, 96, 96, PixelFormats.Pbgra32).Render(map);
                    Check(map.DisplayRects.Count == (mode == CaptureMode.Window ? 1 : 2) && map.AreaRect is { Width: > 0, Height: > 0 }, "Capture map has no scaled monitor/selection geometry");
                    if (mode == CaptureMode.Region) Check(map.DisplayRects[0].X < map.DisplayRects[1].X && map.AreaRect!.Value.Left < map.DisplayRects[1].Left,
                        "Negative desktop coordinates or multiple monitors mapped incorrectly");
                }
            }
            // A saved window crop remains intact even when the game is no longer running.
            available.Clear(); main.ReconnectTarget(); Layout(main, 1060, 740);
            var disconnected = Named<TextBlock>(main, "TargetLabel").Text;
            Check(disconnected.Contains("대상 게임:") && disconnected.Contains("연결 대기")
                && disconnected.Contains("게임을 실행하세요")
                && disconnected.Contains("대상 창을 다시 선택하세요")
                && !disconnected.Contains("권한"),
                "Disconnected game should have a clear, actionable message instead of technical jargon");
            var mapAfterDisconnect = Named<CaptureAreaMap>(main, "AreaMap");
            Check(mapAfterDisconnect.Visibility == Visibility.Collapsed
                && placeholder.Visibility == Visibility.Visible
                && emptyReason.Text == "게임 창 연결 대기"
                && previewFrame.ActualHeight == 110
                && p.WindowCaptureRegion == new PixelRect(100, 80, 500, 250),
                "Game disconnect changed the capture card height or discarded saved crop");
            available.Add(fake); main.ReconnectTarget(); Layout(main, 1060, 740);
            Check(mapAfterDisconnect.Visibility == Visibility.Visible
                && placeholder.Visibility == Visibility.Collapsed
                && previewFrame.ActualHeight == 110,
                "Game reconnect did not restore preview in the same frame");

            // A configured screen region can have no monitor geometry temporarily.
            captureMode.SelectedIndex = 1;
            main.SetCaptureMapDisplays(() => []); Layout(main, 1060, 740);
            Check(mapAfterDisconnect.Visibility == Visibility.Collapsed
                && placeholder.Visibility == Visibility.Visible
                && emptyReason.Text == "모니터 정보 확인 대기"
                && previewFrame.ActualHeight == 110,
                "Missing monitor layout should not collapse the preview frame");
            main.SetCaptureMapDisplays(() => [new(-1600, 0, 1600, 2560), new(0, 0, 2560, 1440)]);
            Check(mapAfterDisconnect.Visibility == Visibility.Visible
                && placeholder.Visibility == Visibility.Collapsed,
                "Screen-area preview failed to recover after monitor geometry returned");
            main.ApplyAvailableWindowSize(new(400, 1008)); Layout(main, 384, 900);
            Check(Grid.GetRow(previewFrame) == 1, "Narrow capture preview frame did not move below description");
            Check(FeedbackTests.Bounds(main, previewFrame).Right <= 384
                && previewFrame.ActualHeight == 110, "Narrow capture preview frame overflowed content");
        } finally { Close(main); Directory.Delete(dir, true); }
    }
    internal static void SubtitleOptions()
    {
        string dir = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate(); var p = data.DefaultProfile;
        var main = Track(new MainWindow(new SettingsStore(dir), new ApiKeyStore(dir), () => [])); main.ApplyLoadedSettings(data);
        var overlay = Track(main.CreateOverlayWindow()); Track(overlay.Chrome);
        try {
            main.ShowProfileSettingsPage(3); Layout(main, 1060, 740);
            Check(Equals(Named<CheckBox>(main, "ShowOriginal").Content, "원문 표시") && Named<CheckBox>(main, "ShowKorean").IsChecked == true, "Subtitle defaults/labels incorrect");
            overlay.SetResult("visible original", "visible translation", "");
            Named<TextBox>(main, "OcrText").Text = "visible original";
            Named<CheckBox>(main, "ShowKorean").IsChecked = false;
            Check(!p.ShowTranslation && p.ShowOriginal && Text(main).Contains("번역 API 사용 안 함"), "OCR-only selection did not update profile/status");
            Check(Elements<TextBlock>(overlay).Single(t => t.Text == "visible original").Visibility == Visibility.Visible,
                "Turning translation off hid original text");
            Named<CheckBox>(main, "ShowOriginal").IsChecked = false;
            Check(Named<CheckBox>(main, "ShowOriginal").IsChecked == true && p.ShowOriginal && main.FeedbackToast.Kind == FeedbackKind.Error,
                "Both subtitle channels could be disabled");
            int displayed = 0; main.ToggleOverlayDisplay((w, show) => { Check(w == overlay && show, "Display command did not reuse the existing overlay"); displayed++; });
            Check(displayed == 1 && !overlay.IsVisible && Equals(Named<Button>(overlay.Chrome, "OverlayToggleTranslation").Content, "시작"),
                "Showing overlay started translation or required a native window in the command test");
            Complete(main.SaveCurrentProfileAsync());
            var restored = new SettingsStore(dir).LoadAsync(); Complete(restored);
            Check(!restored.Result.DefaultProfile.ShowTranslation && restored.Result.DefaultProfile.ShowOriginal, "Visibility preferences were not saved");
            Named<CheckBox>(main, "ShowKorean").IsChecked = true; Named<CheckBox>(main, "ShowOriginal").IsChecked = false;
            Check(p.ShowTranslation && !p.ShowOriginal, "Translation-only option was rejected");
        } finally { Close(overlay); Close(overlay.Chrome); Close(main); Directory.Delete(dir, true); }
    }
    internal static void ModelPicker()
    {
        string dir = FeedbackTests.DirectoryPath(); var p = new Profile(); var settings = new AppSettings(); settings.Validate();
        p.Translation.Provider = TranslationProvider.Gemini;
        var editor = new TranslationSettingsEditor(p, settings.Hotkeys, new ApiKeyStore(dir));
        var host = Track(new Window { Content = editor });
        var dialog = Track(editor.CreateModelListWindow(_ => Task.FromResult<IReadOnlyList<ProviderModel>>([new("chosen-a"), new("chosen-b")])));
        try {
            Check(Named<Button>(host, "ModelListButton").Visibility == Visibility.Visible, "Gemini model button missing");
            Complete(dialog.ReloadAsync()); Layout(dialog, 540, 500);
            var list = Named<ListBox>(dialog, "AvailableModels"); list.SelectedIndex = 1;
            var copy = Named<TextBox>(dialog, "SelectedModelId");
            Check(list.Background is SolidColorBrush { Color.R: 28, Color.G: 43, Color.B: 61 }
                && copy.Background is SolidColorBrush { Color.R: 20, Color.G: 35, Color.B: 51 }
                && list.ItemContainerStyle is not null && Named<Button>(dialog, "OfficialPrices").Visibility == Visibility.Visible,
                "Shared model picker lost its dark controls or cloud pricing button");
            Check(copy.IsReadOnly && copy.Text == "chosen-b" && copy.IsEnabled, "Selected model ID cannot be selected/copied");
            Click(Named<Button>(dialog, "ApplyModel"));
            Check(Named<TextBox>(host, "Model").Text == "chosen-b" && editor.HasChanges && p.Translation.Model == "", "Selected model did not populate draft or auto-saved profile");
            Named<TextBox>(dialog, "ModelSearch").Text = "chosen-a";
            Check(list.Items.Count == 1 && !Named<Button>(dialog, "ApplyModel").IsEnabled, "Model filter retained a stale selection");
            Named<ComboBox>(host, "Provider").SelectedItem = TranslationProvider.Ollama;
            Check(Named<Button>(host, "ModelListButton").Visibility == Visibility.Visible,
                "Ollama should use the shared model-list button");
            list.SelectedIndex = 0; Click(Named<Button>(dialog, "ApplyModel"));
            Check(Named<TextBox>(host, "Model").Text != "chosen-a", "Old provider list replaced a newer provider's model");
            var ollamaDialog = Track(editor.CreateModelListWindow(_ => Task.FromResult<IReadOnlyList<ProviderModel>>([new("local:1")])));
            try {
                Complete(ollamaDialog.ReloadAsync()); Layout(ollamaDialog, 540, 500);
                Check(ollamaDialog.GetType() == dialog.GetType()
                    && Named<TextBox>(ollamaDialog, "ModelSearch").Parent is DockPanel searchRow && searchRow.Visibility == Visibility.Collapsed
                    && Named<Button>(ollamaDialog, "OfficialPrices").Visibility == Visibility.Collapsed,
                    "Ollama must reuse the picker without search or pricing controls");
                Named<ListBox>(ollamaDialog, "AvailableModels").SelectedIndex = 0;
                Click(Named<Button>(ollamaDialog, "ApplyModel"));
                Check(Named<TextBox>(host, "Model").Text == "local:1" && p.Translation.Model == "",
                    "Ollama model selection did not update only the unsaved draft");
            } finally { Close(ollamaDialog); }
            var empty = Track(new ModelListWindow(TranslationProvider.Ollama,
                _ => Task.FromResult<IReadOnlyList<ProviderModel>>([])));
            try {
                Complete(empty.ReloadAsync()); Layout(empty, 540, 500);
                Check(Named<TextBlock>(empty, "ModelListStatus").Text == "Ollama 연결됨 · 설치된 모델 없음"
                    && !Named<Button>(empty, "ApplyModel").IsEnabled,
                    "Empty installed-model list must differ from a connection failure");
            } finally { Close(empty); }
            var failed = Track(new ModelListWindow(TranslationProvider.Ollama,
                _ => Task.FromException<IReadOnlyList<ProviderModel>>(new TranslationException("서버에 연결할 수 없습니다."))));
            try {
                Complete(failed.ReloadAsync()); Layout(failed, 540, 500);
                Check(Named<TextBlock>(failed, "ModelListStatus").Text.Contains("연결할 수 없습니다")
                    && !Named<Button>(failed, "ApplyModel").IsEnabled,
                    "Failed Ollama lookup must retain a clear error and no auto-selection");
            } finally { Close(failed); }
            var gate = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(); bool canceled = false;
            var late = Track(new ModelListWindow(TranslationProvider.OpenAI, token => { token.Register(() => canceled = true); return gate.Task; }));
            var loading = late.ReloadAsync(); Close(late); gate.SetResult([new("late-model")]); Complete(loading);
            Check(canceled && Named<ListBox>(late, "AvailableModels").Items.Count == 0, "Closed model picker did not cancel/discard a late response");
        } finally { Close(dialog); Close(host); Directory.Delete(dir, true); }
    }
}
