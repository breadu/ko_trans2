using System.Windows.Media.Animation;

namespace KoTrans.App;

internal enum FeedbackKind { Success, Information, Error }

// A passive overlay control. Canvas hosts keep it out of normal layout measurement.
internal sealed class OperationToast : Border
{
    private readonly TextBlock icon = new() { FontSize = 18, Width = 23, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock message = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detail = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(171, 193, 211)), TextTrimming = TextTrimming.CharacterEllipsis };
    internal string Message => message.Text;
    internal string Detail => detail.Text;
    internal FeedbackKind Kind { get; private set; }

    internal OperationToast()
    {
        Height = 44; MaxWidth = 520; Padding = new Thickness(9, 3, 9, 3);
        CornerRadius = new CornerRadius(6); BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromRgb(28, 46, 62));
        BorderBrush = new SolidColorBrush(Color.FromRgb(52, 78, 98));
        Visibility = Visibility.Hidden; IsHitTestVisible = false; Focusable = false;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, 1);
        text.Children.Add(message); text.Children.Add(detail); grid.Children.Add(text); Child = grid;
    }
    internal void Present(FeedbackKind kind, string caption, string description)
    {
        Kind = kind; message.Text = caption; detail.Text = description;
        icon.Text = kind switch { FeedbackKind.Success => "✓", FeedbackKind.Information => "i", _ => "!" };
        icon.Foreground = new SolidColorBrush(kind switch {
            FeedbackKind.Success => Color.FromRgb(123, 203, 180), FeedbackKind.Information => Color.FromRgb(121, 189, 250),
            _ => Color.FromRgb(231, 151, 151) });
        message.Foreground = new SolidColorBrush(Color.FromRgb(235, 244, 252));
        System.Windows.Automation.AutomationProperties.SetName(this, caption + " " + description);
        BeginAnimation(OpacityProperty, null); Opacity = 1; Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }
    internal void FadeOut() => BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
    internal void Dismiss()
    {
        BeginAnimation(OpacityProperty, null); Opacity = 1; Visibility = Visibility.Hidden;
    }
}
