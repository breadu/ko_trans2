using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class CharacterDictionaryHelpTests
{
    internal static void Guide()
    {
        var markdown = CharacterDictionaryHelpWindow.LoadGuideMarkdown();
        Check(markdown.StartsWith("# 캐릭터 사전이란?", StringComparison.Ordinal)
            && markdown.Contains("korean_name")
            && markdown.Contains("description") && markdown.Contains("UTF-8")
            && markdown.Contains("DeepL") && markdown.Contains("256 KB"),
            "The character dictionary guide lacks required format or provider information");
        var match = Regex.Match(markdown, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
        Check(match.Success && CharacterDictionaryHelpWindow.ExtractJsonExample(markdown) == match.Groups[1].Value.Trim(),
            "Character dictionary JSON example cannot be extracted for copying");
        Check(!match.Groups[1].Value.Contains("\"info\"", StringComparison.Ordinal)
            && Regex.Matches(match.Groups[1].Value, "\"description\"").Count == 2,
            "New dictionary examples should use only the description field");
        Check(markdown.Length < 1400 && markdown.Contains("AI에게") && markdown.Contains("스포일러")
            && markdown.Contains("최대 300명") && markdown.Contains("설정 저장"),
            "Character dictionary guide must stay brief but practical and safe");
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.DictionaryHelp",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "example.json");
            File.WriteAllText(path, match.Groups[1].Value);
            var examples = CharacterDictionary.Load(path);
            Check(examples.Count == 2 && examples[0].Name == "Alice"
                && examples[0].KoreanName == "앨리스"
                && examples[0].Description.Contains("왕국")
                && examples[1].Name == "佐藤" && examples[1].KoreanName == "사토",
                "The sample JSON is incompatible with the actual character dictionary loader");
            var legacyPath = Path.Combine(directory, "legacy.json");
            File.WriteAllText(legacyPath, """[{"name":"Alice","korean_name":"앨리스","info":"주인공의 동료. 왕국 출신 기사."}]""");
            var preferredPath = Path.Combine(directory, "preferred.json");
            File.WriteAllText(preferredPath, """[{"name":"Alice","korean_name":"앨리스","description":"주인공의 동료. 왕국 출신 기사."}]""");
            Check(TranslationPrompt.Build(CharacterDictionary.Load(legacyPath)) == TranslationPrompt.Build(CharacterDictionary.Load(preferredPath)),
                "Legacy info and preferred description did not produce identical AI reference data");
            var viewer = Track(new CharacterDictionaryHelpWindow());
            try
            {
                Layout(viewer, 640, 430);
                Check(viewer.Background is SolidColorBrush { Color: var col }
                    && col == Color.FromRgb(0x10, 0x1B, 0x2A),
                    "Character dictionary help does not use the dark launcher theme");
                Check(viewer.GuideViewer.Document.Blocks.OfType<Paragraph>().Any(p =>
                    p.Inlines.OfType<Run>().Any(r => r.Text.Contains("Alice"))),
                    "JSON example was not rendered in the help window");
                Check(Button(viewer, "닫기").IsCancel && !viewer.IsVisible,
                    "Help dialog must be dismissible without opening a desktop window in Background tests");
                var exampleHeading = viewer.GuideViewer.Document.Blocks.OfType<Paragraph>()
                    .Single(p => p.Inlines.OfType<Run>().Any(r => r.Text == "JSON 작성 예시"));
                Check(exampleHeading.Inlines.OfType<InlineUIContainer>()
                    .Any(i => ReferenceEquals(i.Child, viewer.CopyExampleButton))
                    && viewer.CopyExampleButton.Content as string == "예시 복사"
                    && viewer.CopyExampleButton.VerticalContentAlignment == VerticalAlignment.Center
                    && viewer.CopyExampleButton.Height == 36
                    && viewer.CopyExampleButton.ContentTemplate is not null,
                    "JSON copy button must use a centered content layout beside the example heading");
                viewer.GuideScroll.ScrollToVerticalOffset(180);
                Layout(viewer, 640, 430);
                var beforeCopy = viewer.GuideScroll.VerticalOffset;
                Check(beforeCopy > 0, "Dictionary guide did not preserve a nonzero scroll position before copying");
                string? copied = null;
                viewer.CopyExample = value => copied = value;
                Click(viewer.CopyExampleButton);
                Layout(viewer, 640, 430);
                Check(copied == match.Groups[1].Value.Trim() && viewer.CopyExampleButton.Content as string == "복사됨"
                    && viewer.CopyToast.Visibility == Visibility.Visible && viewer.CopyToast.Message == "클립보드에 복사되었습니다."
                    && Math.Abs(viewer.GuideScroll.VerticalOffset - beforeCopy) < 1,
                    "Copy button did not copy the complete valid JSON sample");
                viewer.CopyExampleButton.ApplyTemplate();
                var copyLabel = OverlayActivityTests.Visuals<TextBlock>(viewer.CopyExampleButton)
                    .Single(t => t.Text == "복사됨");
                double centerY = copyLabel.TranslatePoint(new Point(0, copyLabel.ActualHeight / 2), viewer.CopyExampleButton).Y;
                Check(copyLabel.VerticalAlignment == VerticalAlignment.Center
                    && copyLabel.ActualHeight > 0
                    && Math.Abs(centerY - viewer.CopyExampleButton.ActualHeight / 2) <= 3,
                    "The copy-success label is not visually centered inside its button");
                viewer.CopyExample = _ => throw new IOException("Synthetic clipboard busy");
                Click(viewer.CopyExampleButton);
                Check(viewer.CopyExampleButton.Content as string == "복사 실패"
                    && viewer.CopyToast.Kind == FeedbackKind.Error
                    && viewer.CopyToast.Message == "클립보드에 복사하지 못했습니다."
                    && Math.Abs(viewer.GuideScroll.VerticalOffset - beforeCopy) < 1,
                    "Clipboard failure must be reported without crashing the help window");
                var scroll = viewer.GuideScroll;
                Check(scroll.ScrollableHeight > 0 && !scroll.CanContentScroll,
                    "Dictionary guide has no wheel-scrollable document viewport");
                scroll.ResetToTop(); Layout(viewer, 640, 430);
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                viewer.GuideViewer.RaiseEvent(wheel);
                Check(wheel.Handled && (SystemParameters.WheelScrollLines == 0 ? scroll.TargetOffset == 0 : scroll.TargetOffset > 0),
                    "Dictionary guide did not use the shared settings wheel behavior");
                if (SystemParameters.ClientAreaAnimation && SystemParameters.WheelScrollLines != 0)
                    Check(scroll.IsAnimating && scroll.VerticalOffset < scroll.TargetOffset,
                        "Dictionary guide wheel skipped the settings scroll animation");
                Until(() => !scroll.IsAnimating, "Dictionary guide wheel animation did not finish");
                Layout(viewer, 640, 430);
                Check(Math.Abs(scroll.VerticalOffset - scroll.TargetOffset) < 1,
                    "Dictionary guide wheel did not reach the selected offset");
            }
            finally { Close(viewer); }
            var game = new Profile { CharacterDictionaryEnabled = false };
            var settings = Track(new TranslationSettingsWindow(game, new HotkeyOptions(), new ApiKeyStore(directory)));
            try
            {
                var help = Named<Button>(settings, "CharacterDictionaryHelpButton");
                var browse = Named<Button>(settings, "CharacterDictionaryBrowseButton");
                var file = Named<TextBox>(settings, "CharacterDictionaryPath");
                var header = Named<DockPanel>(settings, "CharacterDictionaryHeader");
                var fileRow = Named<DockPanel>(settings, "CharacterDictionaryFileRow");
                Check(Equals(help.Content, "ⓘ 사용 안내") && help.IsEnabled
                    && help.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue
                    && help.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue
                    && help.ReadLocalValue(Control.BorderBrushProperty) == DependencyProperty.UnsetValue
                    && DockPanel.GetDock(help) == Dock.Right
                    && header.Children.Contains(help)
                    && header.Children.Contains(Named<CheckBox>(settings, "DictionaryEnabled"))
                    && Equals(browse.Content, "사전 선택…")
                    && DockPanel.GetDock(browse) == Dock.Right
                    && fileRow.Children.Count == 2
                    && ReferenceEquals(fileRow.Children[0], browse)
                    && ReferenceEquals(fileRow.Children[1], file),
                    "Dictionary help must be in the header and picker to the right of the file field");
                foreach (double width in new[] { 790d, 600d })
                {
                    Layout(settings, width, 650);
                    var input = FeedbackTests.Bounds(settings, file);
                    var choose = FeedbackTests.Bounds(settings, browse);
                    Check(choose.Left >= input.Right + 5
                        && Math.Abs(choose.Top + choose.Height / 2 - (input.Top + input.Height / 2)) < 7,
                        "Dictionary picker was not aligned beside the file field at width " + width);
                }
            }
            finally { Close(settings); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
