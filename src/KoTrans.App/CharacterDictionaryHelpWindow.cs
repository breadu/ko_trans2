using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Threading;

namespace KoTrans.App;

// Offline, read-only Markdown. All content becomes native WPF text.
// No HTML engine, script, network, or access to the user's dictionary file.
internal sealed class CharacterDictionaryHelpWindow : Window
{
    internal const string ResourceUri = "/KoTrans2;component/Docs/character-dictionary-guide.md";
    private static readonly Brush Canvas = Paint(0x10, 0x1B, 0x2A);
    private static readonly Brush Card = Paint(0x1C, 0x2B, 0x3D);
    private static readonly Brush Input = Paint(0x12, 0x22, 0x35);
    private static readonly Brush Ink = Paint(0xEA, 0xF2, 0xFB);
    private static readonly Brush Muted = Paint(0xA5, 0xBC, 0xD1);
    private static readonly Brush Accent = Paint(0x79, 0xBD, 0xFA);
    internal FlowDocumentScrollViewer GuideViewer { get; }
    internal SmoothScrollViewer GuideScroll { get; }
    internal Button CopyExampleButton { get; }
    internal string ExampleJson { get; }
    private readonly OperationToast copyToast = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 32, 0) };
    private readonly DispatcherTimer copyToastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool suppressCopyScroll;
    internal OperationToast CopyToast => copyToast;
    // Injectable so headless UI tests never read or overwrite the real clipboard.
    internal Action<string> CopyExample { get; set; } = System.Windows.Clipboard.SetText;

    internal CharacterDictionaryHelpWindow()
    {
        Title = "KO Trans 2 · 캐릭터 사전이란?";
        Width = 790; Height = 650; MinWidth = 550; MinHeight = 430;
        Background = Canvas; Foreground = Ink;
        FontFamily = new FontFamily("맑은 고딕"); FontSize = 13;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = "캐릭터 사전 사용 안내", Foreground = Ink,
            FontSize = 22, FontWeight = FontWeights.Bold
        });
        header.Children.Add(new TextBlock
        {
            Text = "용도 · JSON 예시 · 작성 규칙",
            Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0)
        });
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var close = new Button
        {
            Name = "CloseDictionaryGuide", Content = "닫기", IsCancel = true,
            Style = (Style)FindResource("DarkDisabledButton"),
            MinWidth = 95, MinHeight = 37, HorizontalAlignment = HorizontalAlignment.Right,
            Background = Paint(0x32, 0x8B, 0xD0), Foreground = Brushes.White,
            BorderBrush = Paint(0x59, 0xAA, 0xF0), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);

        string markdown = LoadGuideMarkdown();
        ExampleJson = ExtractJsonExample(markdown);
        CopyExampleButton = new Button
        {
            Name = "CopyDictionaryJsonExample", Content = "예시 복사",
            Style = (Style)FindResource("DarkDisabledButton"),
            FontSize = 12, Width = 90, Height = 36, Margin = new Thickness(14, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            ContentTemplate = CenteredCopyContentTemplate(),
            Background = Paint(0x2A, 0x40, 0x56), Foreground = Ink,
            BorderBrush = Paint(0x48, 0x63, 0x7A), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "아래 JSON 예시를 클립보드에 복사합니다."
        };
        CopyExampleButton.PreviewMouseLeftButtonDown += (_, _) => suppressCopyScroll = true;
        CopyExampleButton.RequestBringIntoView += (_, e) => { if (suppressCopyScroll) e.Handled = true; };
        CopyExampleButton.Click += (_, _) =>
        {
            suppressCopyScroll = true;
            try
            {
                CopyExample(ExampleJson);
                CopyExampleButton.Content = "복사됨";
                CopyExampleButton.ToolTip = "JSON 예시를 클립보드에 복사했습니다.";
                ShowCopyToast(FeedbackKind.Success, "클립보드에 복사되었습니다.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                CopyExampleButton.Content = "복사 실패";
                CopyExampleButton.ToolTip = "클립보드에 접근할 수 없습니다. 다시 시도하세요.";
                ShowCopyToast(FeedbackKind.Error, "클립보드에 복사하지 못했습니다.");
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => suppressCopyScroll = false);
        };
        copyToastTimer.Tick += (_, _) => { copyToastTimer.Stop(); copyToast.Dismiss(); };

        GuideViewer = new FlowDocumentScrollViewer
        {
            Name = "CharacterDictionaryGuideViewer",
            Document = RenderMarkdown(markdown, CopyExampleButton),
            Background = Card,
            IsToolBarVisible = false, IsSelectionEnabled = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        GuideScroll = new SmoothScrollViewer
        {
            Name = "CharacterDictionaryGuideScroll", Content = GuideViewer,
            Background = Card, BorderBrush = Paint(0x34, 0x4E, 0x65),
            BorderThickness = new Thickness(1), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        GuideViewer.PreviewMouseWheel += (_, e) =>
        {
            if (GuideScroll.ScrollWheel(e.Delta, SystemParameters.WheelScrollLines)) e.Handled = true;
        };
        Closed += (_, _) => { copyToastTimer.Stop(); copyToast.Dismiss(); GuideScroll.Dispose(); };
        layout.Children.Add(GuideScroll);
        var root = new Grid();
        root.Children.Add(layout);
        root.Children.Add(copyToast);
        Panel.SetZIndex(copyToast, 1);
        Content = root;
    }

    // A regular Button string inside a FlowDocument paragraph can inherit inline
    // text metrics and appear too high even with VerticalContentAlignment=Center.
    // Give the label its own fixed-height WPF layout, centered independently.
    private static DataTemplate CenteredCopyContentTemplate()
    {
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("."));
        label.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(TextBlock.LineHeightProperty, 17d);
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.HeightProperty, 30d);
        grid.AppendChild(label);
        return new DataTemplate { VisualTree = grid };
    }

    private void ShowCopyToast(FeedbackKind kind, string message)
    {
        copyToast.Present(kind, message, "JSON 작성 예시");
        copyToastTimer.Stop(); copyToastTimer.Start();
    }

    internal static string LoadGuideMarkdown()
    {
        var stream = Application.GetResourceStream(new Uri(ResourceUri, UriKind.Relative))
            ?? throw new IOException("캐릭터 사전 도움말 파일이 없습니다.");
        using var input = stream.Stream;
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static string ExtractJsonExample(string markdown)
    {
        var match = Regex.Match(markdown, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
        if (!match.Success) throw new InvalidDataException("복사할 JSON 예시를 찾을 수 없습니다.");
        return match.Groups[1].Value.Trim();
    }

    internal static FlowDocument RenderMarkdown(string markdown, Button? exampleCopy = null)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("맑은 고딕"), Foreground = Ink,
            FontSize = 13, PagePadding = new Thickness(19, 16, 19, 18)
        };
        var code = new System.Collections.Generic.List<string>();
        bool inCode = false;
        foreach (var original in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = original.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode) AppendCode(document, code);
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.Add(line); continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var content = line.Trim();
            int level = 0;
            while (level < content.Length && content[level] == '#') level++;
            bool heading = level is > 0 and <= 3 && level < content.Length && content[level] == ' ';
            if (heading) content = content[(level + 1)..];
            else if (content.StartsWith("- ", StringComparison.Ordinal)) content = "• " + content[2..];
            var p = new Paragraph
            {
                Margin = heading ? new Thickness(0, 14, 0, 9) : new Thickness(0, 0, 0, 7),
                FontSize = heading ? level == 1 ? 19 : level == 2 ? 16 : 14 : 13,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = heading ? Accent : Ink, LineHeight = heading ? 27 : 22
            };
            AppendInlines(p, content);
            if (heading && content == "JSON 작성 예시" && exampleCopy is not null)
                p.Inlines.Add(new InlineUIContainer(exampleCopy)
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
            document.Blocks.Add(p);
        }
        if (inCode && code.Count > 0) AppendCode(document, code);
        return document;
    }

    private static void AppendInlines(Paragraph paragraph, string content)
    {
        foreach (var part in Regex.Split(content, @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (part.Length == 0) continue;
            if (part.Length >= 4 && part.StartsWith("**", StringComparison.Ordinal)
                && part.EndsWith("**", StringComparison.Ordinal))
                paragraph.Inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.Length >= 2 && part[0] == '`' && part[^1] == '`')
                paragraph.Inlines.Add(new Run(part[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"), Foreground = Accent
                });
            else paragraph.Inlines.Add(new Run(part));
        }
    }

    private static void AppendCode(FlowDocument document, System.Collections.Generic.List<string> code)
    {
        document.Blocks.Add(new Paragraph(new Run(string.Join("\n", code)))
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            Foreground = Ink, Background = Input,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 7, 0, 15)
        });
        code.Clear();
    }

    private static Brush Paint(byte r, byte g, byte b) =>
        new SolidColorBrush(Color.FromRgb(r, g, b));
}
