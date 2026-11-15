using KoTrans.Infrastructure;
namespace KoTrans.App;

internal sealed class LocalPreparationWindow : Window
{
    private readonly TextBlock stage = new() { Text = "로컬 AI 번역 준비 중", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ProgressBar progress = new() { Height = 12, Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly TextBlock bytes = new() { Margin = new Thickness(0, 8, 0, 10) };
    private readonly Button retry = new() { Content = "다시 시도", Visibility = Visibility.Collapsed };
    private readonly Button cancel = new() { Content = "취소" };
    private TaskCompletionSource<bool>? choice;
    private bool finished;
    internal LocalPreparationWindow(Action cancelAction)
    {
        Title = "로컬 AI 번역 준비"; Width = 440; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(25, 37, 52)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(stage); panel.Children.Add(progress); panel.Children.Add(bytes);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { retry, cancel })
        {
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(0, 0, button == retry ? 10 : 8, 8);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); Content = panel;
        retry.Click += (_, _) => { retry.Visibility = Visibility.Collapsed; cancel.Content = "취소"; choice?.TrySetResult(true); };
        cancel.Click += (_, _) => { choice?.TrySetResult(false); cancelAction(); };
        Closed += (_, _) => { choice?.TrySetResult(false); if (!finished) cancelAction(); };
    }
    internal void Report(LocalPreparationProgress value)
    {
        stage.Text = value.Stage;
        progress.IsIndeterminate = value.Total == 0;
        progress.Value = value.Percent;
        bytes.Text = value.Total > 0 ? $"{value.Percent:F0}% · {value.Received / 1e9:F1}GB / {value.Total / 1e9:F1}GB" : "";
    }
    internal Task<bool> RetryAsync(string message)
    {
        stage.Text = message; progress.IsIndeterminate = false; retry.Visibility = Visibility.Visible; cancel.Content = "닫기";
        choice = new(TaskCreationOptions.RunContinuationsAsynchronously); return choice.Task;
    }
    internal void Finish() { finished = true; Close(); }
}
