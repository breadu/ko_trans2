using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;

namespace KoTrans.App;

// The target picker is created in C# rather than XAML. Keep its colors,
// selection state and controls consistent with MainWindow's dark launcher.
internal static class TargetPickerTheme
{
    private static readonly Brush Canvas = new SolidColorBrush(Color.FromRgb(0x10, 0x1B, 0x2A));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x1C, 0x2B, 0x3D));
    private static readonly Brush Input = new SolidColorBrush(Color.FromRgb(0x12, 0x22, 0x35));
    private static readonly Brush Outline = new SolidColorBrush(Color.FromRgb(0x34, 0x4E, 0x65));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFB));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA5, 0xBC, 0xD1));
    private static readonly Brush Hover = new SolidColorBrush(Color.FromRgb(0x26, 0x41, 0x58));
    private static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0x27, 0x59, 0x81));
    private static readonly Brush Action = new SolidColorBrush(Color.FromRgb(0x32, 0x8B, 0xD0));
    private static readonly Brush ActionHover = new SolidColorBrush(Color.FromRgb(0x43, 0x9B, 0xDD));
    private static readonly Brush ActionPressed = new SolidColorBrush(Color.FromRgb(0x24, 0x6D, 0xA4));
    private static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(0x2A, 0x40, 0x56));
    private static readonly Brush SecondaryHover = new SolidColorBrush(Color.FromRgb(0x35, 0x54, 0x6D));
    private static readonly Brush AccentBorder = new SolidColorBrush(Color.FromRgb(0x59, 0xAA, 0xF0));

    internal static void Apply(Window window, ListBox list, TextBlock status, Button select, Button refresh)
    {
        window.Background = Canvas;
        window.Foreground = Ink;
        window.FontFamily = new FontFamily("맑은 고딕");
        window.FontSize = 13;
        window.MinWidth = 540;
        window.MinHeight = 310;

        list.Background = Input;
        list.Foreground = Ink;
        list.BorderThickness = new Thickness(0);
        list.Padding = new Thickness(5);
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        list.ItemContainerStyle = ItemStyle();
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);

        status.Foreground = Muted;
        status.FontSize = 12;
        status.Margin = new Thickness(2, 7, 2, 0);
        status.MinHeight = 20;

        ApplyButtonStyle(select, primary: true);
        ApplyButtonStyle(refresh, primary: false);
        refresh.Margin = new Thickness(0);

        var content = new DockPanel { Margin = new Thickness(20, 18, 20, 17) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 13) };
        heading.Children.Add(new TextBlock
        {
            Text = "캡처할 게임 창 선택", FontSize = 19, FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        heading.Children.Add(new TextBlock
        {
            Text = "목록에서 게임 창을 선택하세요.", FontSize = 12,
            Foreground = Muted, Margin = new Thickness(0, 5, 0, 0)
        });
        DockPanel.SetDock(heading, Dock.Top);
        content.Children.Add(heading);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(select);
        actions.Children.Add(refresh);
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        DockPanel.SetDock(status, Dock.Bottom);
        content.Children.Add(status);

        var listCard = new Border
        {
            Background = Card,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6),
            Child = list
        };
        content.Children.Add(listCard);
        window.Content = content;
    }

    private static Style ItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(TextElement.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11, 7, 11, 7)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        style.Setters.Add(new Setter(Control.TemplateProperty, ChromeTemplate(typeof(ListBoxItem), 6, false)));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Hover));
        style.Triggers.Add(hover);
        // Selection remains legible whether or not the list currently has focus.
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Selected));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        selected.Setters.Add(new Setter(TextElement.ForegroundProperty, Brushes.White));
        style.Triggers.Add(selected);
        return style;
    }

    private static void ApplyButtonStyle(Button button, bool primary)
    {
        var style = new Style(typeof(Button), (Style)button.FindResource("DarkDisabledButton"));
        style.Setters.Add(new Setter(Control.BackgroundProperty, primary ? Action : Secondary));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, primary ? AccentBorder : Outline));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(15, 9, 15, 9)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.TemplateProperty, ChromeTemplate(typeof(Button), 6, true)));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, primary ? ActionHover : SecondaryHover));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, primary ? ActionPressed : Hover));
        style.Triggers.Add(pressed);

        button.Style = style;
        button.MinWidth = 100;
        button.MinHeight = 38;
        button.Margin = new Thickness(0, 0, 9, 0);
        button.Cursor = Cursors.Hand;
    }

    private static ControlTemplate ChromeTemplate(Type target, double radius, bool center)
    {
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        chrome.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        chrome.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        chrome.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        chrome.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            center ? HorizontalAlignment.Center : HorizontalAlignment.Left);
        text.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(ContentPresenter.RecognizesAccessKeyProperty, center);
        chrome.AppendChild(text);
        return new ControlTemplate(target) { VisualTree = chrome };
    }
}
