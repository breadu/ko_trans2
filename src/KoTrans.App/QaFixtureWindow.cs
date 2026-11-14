using System.Windows.Threading;
namespace KoTrans.App;

// Local synthetic source for explicit --test-data --mock-ai desktop checks. No network or real clipboard reads.
internal sealed class QaFixtureWindow : Window
{
    private const string Sentence = "The journey begins with a single step.";
    private readonly TextBlock text = new() { Text = Sentence, FontFamily = new FontFamily("Segoe UI"), FontSize = 28, Foreground = Brushes.White, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
    private readonly Border surface = new() { Background = new SolidColorBrush(Color.FromRgb(27, 39, 55)), Height = 170 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock outcome = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    public QaFixtureWindow(Action<PixelRect> select)
    {
        Title = "KO Trans 2 · 합성 게임 화면 (QA)"; Width = 850; Height = 450; MinWidth = 500;
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        DesktopWindows.Restore(this, new WindowPlacement { X = area.Right - 1070, Y = area.Bottom - 600, Width = 850, Height = 450 });
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "합성 대사 · 개인 화면이나 외부 AI를 사용하지 않는 검증용 창" });
        surface.Child = text; panel.Children.Add(surface); var buttons = new WrapPanel(); panel.Children.Add(buttons);
        void Button(string title, Action action) { var b = new Button { Content = title }; b.Click += (_, _) => action(); buttons.Children.Add(b); }
        Button("이 영역 사용", () => { var point = surface.PointToScreen(new(0, 0)); var dpi = VisualTreeHelper.GetDpi(surface); select(new((int)point.X, (int)point.Y, (int)(surface.ActualWidth * dpi.DpiScaleX), (int)(surface.ActualHeight * dpi.DpiScaleY))); });
        Button("다음 문장", () => { timer.Stop(); text.Text = text.Text == Sentence ? "We will meet again tomorrow." : Sentence; });
        Button("타자기 대사", () => { text.Text = ""; timer.Start(); });
        Button("대사 복사", () => { try { System.Windows.Clipboard.SetText(text.Text); } catch (ExternalException) { } });
        panel.Children.Add(outcome);
        timer.Tick += (_, _) => { if (text.Text.Length >= Sentence.Length) timer.Stop(); else text.Text = Sentence[..(text.Text.Length + 1)]; };
        Closed += (_, _) => timer.Stop();
    }
    public void Report(PipelineUpdate update, bool excluded)
    {
        if (!string.IsNullOrEmpty(update.Translation) || update.State == PipelineState.Error)
            outcome.Text = $"{update.Message}\n원문: {update.Original}\n{update.Translation}\nOCR {update.OcrRequests} / 번역 {update.TranslationRequests} / HTTP {update.HttpAttempts} / 재사용 {update.CacheHits} · 오버레이 캡처 제외: {excluded}\n캡처 {update.CaptureMs:F0} / 검출 {update.Ocr?.DetectionTime.TotalMilliseconds:F0} / 인식 {update.Ocr?.RecognitionTime.TotalMilliseconds:F0} / AI {update.ApiMs:F0} / 전체 {update.TotalMs:F0} ms";
    }
    public void ShowDiagnostics(bool value) => outcome.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
}
