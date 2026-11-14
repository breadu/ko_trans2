namespace KoTrans.App;

internal sealed class LocalConfirmationWindow : Window
{
    internal LocalConfirmationWindow(string title, string message, bool deleting = false)
    {
        Title = title; Width = 450; SizeToContent = SizeToContent.Height;
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(25, 37, 52)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 20) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = new Button { Name = "ConfirmLocalYes", Content = "예", MinWidth = 72, IsDefault = !deleting, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var no = new Button { Name = "ConfirmLocalNo", Content = "아니요", MinWidth = 72, IsCancel = true, IsDefault = deleting, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        yes.Click += (_, _) => DialogResult = true;
        no.Click += (_, _) => DialogResult = false;
        actions.Children.Add(yes); actions.Children.Add(no); panel.Children.Add(actions); Content = panel;
    }
}
