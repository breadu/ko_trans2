using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class DeveloperSupportTests
{
    internal static void LauncherAndDialog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.DeveloperSupport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => []));
        try
        {
            var settings = new AppSettings(); settings.Validate(); main.ApplyLoadedSettings(settings);
            Layout(main, 820, 485);
            var card = Named<Border>(main, "LauncherMascotCard");
            var mascot = Named<Image>(main, "LauncherMascot");
            var button = Named<Button>(main, "DeveloperCoffeeButton");
            Check(card.Visibility == Visibility.Visible && mascot.Source is not null
                && Elements<TextBlock>(card).Any(t => t.Text == "게임 속 대사를, 편하게!")
                && !Elements<TextBlock>(card).Any(t => t.Text == "프로필만 선택하고 바로 번역"),
                "Developer support replaced the mascot or retained the old caption");
            Check(button.ActualWidth > 230 && button.ActualWidth <= card.ActualWidth
                && button.ActualHeight == 39 && Text(main).Contains("마음에 들었다면, 개발자에게 커피 한 잔"),
                "Support button was clipped in the default launcher");
            Check(((FrameworkElement)button.Content).DesiredSize.Width <= button.ActualWidth - button.Padding.Left - button.Padding.Right - 2,
                "Support button caption does not fit inside the filled surface");
            var supportColor = ((SolidColorBrush)button.Background).Color;
            var primaryColor = ((SolidColorBrush)Named<Button>(main, "WatchButton").Background).Color;
            Check(supportColor == Color.FromRgb(0x52, 0x6F, 0x89) && supportColor != primaryColor
                && button.Style!.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsMouseOverProperty)
                && button.Style.Triggers.OfType<Trigger>().Any(t => t.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty),
                "Support button lost its quiet filled theme or hover/pressed states");

            int dialogs = 0;
            DeveloperSupportWindow? shown = null;
            main.PresentDeveloperSupportOverride = dialog =>
            {
                dialogs++; shown = Track(dialog);
                Check(dialog.Owner is null && dialog.WindowStartupLocation == WindowStartupLocation.CenterOwner,
                    "Background dialog created a native owner or lost center-owner placement");
                Click(button); // A reentrant click must not create a second dialog.
            };
            Click(button);
            Check(dialogs == 1 && shown is not null && !shown.IsVisible,
                "Support click opened multiple dialogs or created a native window in Background");
            var open = Named<Button>(shown!, "OpenDeveloperSupportPage");
            Check(open.IsEnabled && Named<TextBlock>(shown!, "DeveloperSupportNotice").Visibility == Visibility.Collapsed
                && DeveloperSupportLinks.Resolve(DeveloperSupportLinks.SupportPageUrl)?.AbsoluteUri == "https://link.kakaopay.com/__/nH3zt49"
                && Text(shown!).Contains("KO Trans 2는 개인 개발자가 만든 무료 프로그램입니다.")
                && Text(shown!).Contains("후원은 선택 사항이며, 후원 여부와 관계없이")
                && Named<TextBlock>(shown!, "DeveloperSupportOptional").Text.Contains("‘후원 페이지 열기’를 누르면 카카오페이 송금 화면으로 이동합니다."),
                "Confirmed KakaoPay support link must enable the button without changing the explanation");
            var withoutUrl = Track(new DeveloperSupportWindow(""));
            Check(!Named<Button>(withoutUrl, "OpenDeveloperSupportPage").IsEnabled
                && Named<TextBlock>(withoutUrl, "DeveloperSupportNotice").Text == "후원 페이지 준비 중입니다.",
                "An unconfigured support dialog must continue to disable its link safely");
            Close(withoutUrl);
            var popupContent = (FrameworkElement)shown!.Content;
            popupContent.Measure(new Size(shown!.Width, double.PositiveInfinity));
            Check(popupContent.DesiredSize.Height >= 390 && popupContent.DesiredSize.Height < 650,
                "Redesigned support dialog has an unexpected height or clipped content");
            var coffeeCat = Named<Image>(shown!, "DeveloperSupportMascot");
            Check(coffeeCat.Source is System.Windows.Media.Imaging.BitmapSource imageSource
                && imageSource.PixelWidth >= 250 && imageSource.PixelHeight >= 230
                && coffeeCat.Width >= 240 && coffeeCat.Height >= 230
                && Named<Border>(shown!, "DeveloperSupportInfoCard").CornerRadius.TopLeft >= 8,
                "Support artwork/resource or polished info card is missing");
            Check(shown.Width >= 700 && shown.Background is SolidColorBrush,
                "Redesigned support dialog lost its two-column dark layout");
            Close(shown!);

            Check(DeveloperSupportLinks.Resolve("http://example.com/support") is null
                && DeveloperSupportLinks.Resolve("javascript:alert(1)") is null
                && DeveloperSupportLinks.Resolve("https://user@example.com/support") is null
                && DeveloperSupportLinks.Resolve("https://example.com/support")?.Scheme == Uri.UriSchemeHttps,
                "Support address validation accepted a non-HTTPS or credential-bearing URL");
            // Use an injected browser launcher: never initiate a real transfer link in background tests.
            ProcessStartInfo? configuredLaunch = null;
            var configured = Track(new DeveloperSupportWindow(openBrowser: info =>
            { configuredLaunch = info; return true; }));
            Click(Named<Button>(configured, "OpenDeveloperSupportPage"));
            Check(configuredLaunch is { UseShellExecute: true }
                && configuredLaunch.FileName == DeveloperSupportLinks.SupportPageUrl,
                "Configured KakaoPay link was not forwarded unchanged to the system browser");

            ProcessStartInfo? attempted = null;
            var failed = Track(new DeveloperSupportWindow("https://example.com/support", info =>
            { attempted = info; throw new System.ComponentModel.Win32Exception(); }));
            Click(Named<Button>(failed, "OpenDeveloperSupportPage"));
            Check(attempted is { UseShellExecute: true, FileName: "https://example.com/support" }
                && Named<TextBlock>(failed, "DeveloperSupportNotice").Text == "후원 페이지를 열지 못했습니다. 기본 브라우저 설정을 확인해 주세요.",
                "Browser failure was not handled safely or did not use the system browser");
            Close(failed);
            bool launched = false, closed = false;
            var successful = new DeveloperSupportWindow("https://example.com/support", info =>
            { launched = info.UseShellExecute && info.FileName == "https://example.com/support"; return true; });
            successful.Closed += (_, _) => closed = true;
            Click(Named<Button>(successful, "OpenDeveloperSupportPage"));
            Check(launched && closed, "Successful browser launch did not close the support dialog");
            Check(Named<Grid>(main, "LauncherView").Visibility == Visibility.Visible,
                "Closing support dialog changed the launcher");
            main.ApplyAvailableWindowSize(new Size(760, 500)); Layout(main, 760, 500);
            Check(card.Visibility == Visibility.Visible
                && ((FrameworkElement)button.Content).DesiredSize.Width <= button.ActualWidth - button.Padding.Left - button.Padding.Right - 2,
                "Support caption is clipped at the narrowest visible mascot-card width");
            main.ApplyAvailableWindowSize(new Size(700, 500)); Layout(main, 700, 500);
            Check(card.Visibility == Visibility.Collapsed, "Narrow launcher failed to hide the mascot card safely");
        }
        finally { Close(main); Directory.Delete(directory, true); }
    }
}
