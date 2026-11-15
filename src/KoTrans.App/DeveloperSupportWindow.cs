using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace KoTrans.App;

internal static class DeveloperSupportLinks
{
    // Set only the developer's confirmed HTTPS donation page. Never guess a recipient.
    internal const string SupportPageUrl = "https://link.kakaopay.com/__/nH3zt49";

    internal static Uri? Resolve(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(uri.Host)
        && uri.UserInfo.Length == 0 ? uri : null;
}

internal sealed class DeveloperSupportWindow : Window
{
    private const string OpenFailure = "후원 페이지를 열지 못했습니다. 기본 브라우저 설정을 확인해 주세요.";
    private readonly Uri? supportPage;
    private readonly Func<ProcessStartInfo, bool> openBrowser;
    private readonly TextBlock notice = new()
    {
        Name = "DeveloperSupportNotice", FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(165, 188, 209), VerticalAlignment = VerticalAlignment.Center
    };

    internal DeveloperSupportWindow(string? supportPageUrl = DeveloperSupportLinks.SupportPageUrl,
        Func<ProcessStartInfo, bool>? openBrowser = null)
    {
        supportPage = DeveloperSupportLinks.Resolve(supportPageUrl);
        // ShellExecute can reuse an existing browser without returning a process handle.
        this.openBrowser = openBrowser ?? (info => { using var process = Process.Start(info); return true; });

        Title = "☕ 개발자에게 커피 한 잔";
        Width = 724; MinWidth = 640; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush(12, 26, 43);
        Foreground = Brush(234, 242, 251);
        FontFamily = new FontFamily("맑은 고딕"); FontSize = 13;

        // The original dialog behavior (optional donation, valid HTTPS only, safe dismissal)
        // is unchanged. This is a layout/artwork refresh, not a new payment workflow.
        var panel = new Border
        {
            Background = Brush(16, 35, 57), BorderBrush = Brush(49, 77, 104),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Margin = new Thickness(12), Padding = new Thickness(24, 20, 24, 21)
        };
        var root = new Grid();
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title and quiet accent underline.
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var headingRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headingRow.Children.Add(new TextBlock
        {
            Text = "☕", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 31,
            Foreground = Brush(182, 219, 251), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0)
        });
        headingRow.Children.Add(new TextBlock
        {
            Name = "DeveloperSupportTitle", Text = "개발자에게 커피 한 잔",
            FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center
        });
        heading.Children.Add(headingRow);
        heading.Children.Add(new Border
        {
            Width = 150, Height = 3, CornerRadius = new CornerRadius(2),
            Background = Brush(92, 176, 244), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(50, 8, 0, 0)
        });
        Grid.SetRow(heading, 0); root.Children.Add(heading);

        // Free-program message and exact coffee-cat art extracted from the approved mockup.
        var body = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(258) });
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Paragraph("KO Trans 2는 개인 개발자가 만든 무료 프로그램입니다.", 15));
        copy.Children.Add(Paragraph("게임을 즐기는 데 도움이 되었다면,\n커피 한 잔으로 개발자를 응원해 주세요.", 15, true));
        copy.Children.Add(Paragraph("여러분의 작은 응원이 프로그램을 개발하고\n유지보수하는 데 큰 힘이 됩니다.", 0));
        Grid.SetColumn(copy, 0); body.Children.Add(copy);

        var picture = new Grid
        {
            Width = 258, Height = 247, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        var aura = new RadialGradientBrush
        {
            Center = new Point(0.52, 0.52), GradientOrigin = new Point(0.52, 0.52),
            RadiusX = 0.55, RadiusY = 0.55
        };
        aura.GradientStops.Add(new GradientStop(Color.FromArgb(112, 44, 91, 142), 0));
        aura.GradientStops.Add(new GradientStop(Color.FromArgb(0, 24, 57, 94), 1));
        picture.Children.Add(new Ellipse
        {
            Width = 236, Height = 224, Fill = aura,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        var art = new BitmapImage();
        art.BeginInit();
        art.UriSource = new Uri("pack://application:,,,/KoTrans2;component/Assets/developer-coffee-cat.png", UriKind.Absolute);
        art.CacheOption = BitmapCacheOption.OnLoad;
        art.EndInit();
        art.Freeze();
        var mascot = new Image
        {
            Name = "DeveloperSupportMascot", Source = art,
            Width = 255, Height = 245, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(mascot, BitmapScalingMode.HighQuality);
        System.Windows.Automation.AutomationProperties.SetName(mascot, "커피잔을 든 파란 헤드셋의 KO Trans 2 고양이");
        picture.Children.Add(mascot);
        Grid.SetColumn(picture, 1); body.Children.Add(picture);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var divider = new Border
        {
            Height = 1, Background = Brush(69, 112, 152),
            Margin = new Thickness(0, 8, 0, 14)
        };
        Grid.SetRow(divider, 2); root.Children.Add(divider);

        // Visually distinct, non-coercive note; donation never unlocks features.
        var optionalCard = new Border
        {
            Name = "DeveloperSupportInfoCard", Background = Brush(32, 58, 85),
            BorderBrush = Brush(79, 122, 160), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12)
        };
        var optionalRow = new Grid();
        optionalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(47) });
        optionalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var infoCircle = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
            Background = Brush(42, 96, 143), BorderBrush = Brush(109, 175, 224),
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "i", FontFamily = new FontFamily("Segoe UI"), FontSize = 19,
                FontWeight = FontWeights.Bold, Foreground = Brush(205, 235, 253),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        Grid.SetColumn(infoCircle, 0); optionalRow.Children.Add(infoCircle);
        optionalRow.Children.Add(new TextBlock
        {
            Name = "DeveloperSupportOptional",
            Text = "후원은 선택 사항이며, 후원 여부와 관계없이\n모든 기능을 동일하게 이용할 수 있습니다.\n‘후원 페이지 열기’를 누르면 카카오페이 송금 화면으로 이동합니다.",
            Foreground = Brush(216, 232, 247), FontSize = 13,
            TextWrapping = TextWrapping.Wrap, LineHeight = 21,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(optionalRow.Children[1], 1);
        optionalCard.Child = optionalRow;
        Grid.SetRow(optionalCard, 3); root.Children.Add(optionalCard);

        // Status stays unobtrusive. Buttons and link validation retain previous behavior.
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        notice.Text = supportPage is null ? "후원 페이지 준비 중입니다." : "";
        notice.Visibility = supportPage is null ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(notice, 0); footer.Children.Add(notice);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Name = "CloseDeveloperSupport", Content = "닫기", IsCancel = true,
            Style = (Style)FindResource("DarkDisabledButton"), Height = 40,
            Background = Brush(43, 66, 89), BorderBrush = Brush(92, 126, 158),
            BorderThickness = new Thickness(1), Foreground = Foreground,
            Margin = new Thickness(0, 0, 10, 0)
        };
        close.Click += (_, _) => Close();
        var open = new Button
        {
            Name = "OpenDeveloperSupportPage", Content = "후원 페이지 열기 ↗",
            Style = (Style)FindResource("DeveloperSupportButton"), Height = 40,
            Padding = new Thickness(17, 0, 17, 0), Margin = new Thickness(0),
            IsEnabled = supportPage is not null
        };
        open.Click += (_, _) => OpenPage();
        actions.Children.Add(close); actions.Children.Add(open);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        Grid.SetRow(footer, 4); root.Children.Add(footer);

        panel.Child = root; Content = panel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));

    private static TextBlock Paragraph(string text, double bottom, bool emphasized = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 23, FontSize = 14,
        FontWeight = emphasized ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(0, 0, 0, bottom),
        Foreground = Brush(234, 242, 251)
    };

    private void OpenPage()
    {
        if (supportPage is null) return;
        try
        {
            if (!openBrowser(new ProcessStartInfo(supportPage.AbsoluteUri) { UseShellExecute = true }))
                throw new InvalidOperationException("Browser launch returned no process.");
            Close();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or SecurityException
            or IOException or NotSupportedException or ArgumentException)
        {
            notice.Text = OpenFailure;
            notice.Visibility = Visibility.Visible;
        }
    }
}
