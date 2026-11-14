using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class VersionStatusTests
{
    internal static void LayoutAndFormatting()
    {
        Check(AppVersionInfo.FormatStatus("1.0.0+abc123", false) == "Ver 1.0",
            "Stable zero-patch version was not shortened");
        Check(AppVersionInfo.FormatStatus("1.0.0", true) == "Ver 1.0-dev",
            "DEV-RUN label was not distinguished from public release");
        Check(AppVersionInfo.FormatStatus("1.0.1", false) == "Ver 1.0.1"
            && AppVersionInfo.FormatStatus("1.1.0", false) == "Ver 1.1"
            && AppVersionInfo.FormatStatus("2.0.0", false) == "Ver 2.0"
            && AppVersionInfo.FormatStatus("1.0.0-beta.1+sha", false) == "Ver 1.0-beta.1",
            "Semantic version changes were hidden or formatted incorrectly");
        Check(AppVersionInfo.FormatTooltip("1.0.0+abcdef", true).Contains("Version 1.0.0")
            && AppVersionInfo.FormatTooltip("1.0.0+abcdef", true).Contains("DEV-RUN")
            && AppVersionInfo.FormatTooltip("1.0.0+abcdef", true).Contains("Build: abcdef"),
            "Detailed version tooltip lost its prerelease/build context");

        var directory = Path.Combine(Path.GetTempPath(), "KoTrans2.VersionStatus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => []));
        try
        {
            var settings = new AppSettings(); settings.Validate(); main.ApplyLoadedSettings(settings);
            var layout = Named<Grid>(main, "MainStatusLayout");
            var version = Named<TextBlock>(main, "AppVersionText");
            var status = Named<TextBlock>(main, "StatusMessageText");
            var execution = Named<TextBlock>(main, "OcrExecutionStatusText");
            var failure = Named<TextBlock>(main, "LastSaveFailure");
            Check(layout.ColumnDefinitions.Count == 2
                && layout.ColumnDefinitions[0].Width.IsStar
                && layout.ColumnDefinitions[1].Width.IsAuto
                && Math.Abs(version.MaxWidth - 110) < 0.1
                && Grid.GetColumn(version) == 1,
                "Version column must auto-size and cap its width independently of status text");
            Check(version.Text == AppVersionInfo.StatusLabel(global::KoTrans.App.App.IsDev)
                && Equals(version.ToolTip, AppVersionInfo.StatusTooltip(global::KoTrans.App.App.IsDev))
                && version.TextAlignment == TextAlignment.Right,
                "Status label/tooltip were not sourced from the application assembly");
            foreach (var label in new[] { status, failure })
                Check(label.TextTrimming == TextTrimming.CharacterEllipsis
                    && label.TextWrapping == TextWrapping.NoWrap,
                    "Status messages must remain single-line and trim rather than cover the version");
            Check(((MainViewModel)main.DataContext).OcrExecution == "OCR: 대기"
                && ((Grid)failure.Parent).Children.Count == 2
                && !ReferenceEquals(execution.Parent, failure.Parent),
                "OCR execution should begin idle and appear outside the single-line status bar");

            var model = (MainViewModel)main.DataContext;
            string longMessage = "번역 상태: " + new string('가', 400);
            string longExecution = "OCR: " + new string('나', 400);
            string longFailure = "저장 실패: " + new string('다', 400);
            model.Status = longMessage;
            model.OcrExecution = longExecution;
            model.OcrExecutionDetail = longExecution;
            failure.Text = longFailure; failure.Visibility = Visibility.Visible;
            foreach (double width in new[] { 1060d, 820d, 520d, 360d })
            {
                Layout(main, width, 485);
                double reserved = layout.ColumnDefinitions[1].ActualWidth;
                var hotkeyHint = Named<TextBlock>(main, "LauncherHotkeyHint");
                Check(execution.ActualWidth > 0 && hotkeyHint.ActualWidth > 0
                    && hotkeyHint.ActualWidth <= Named<Grid>(main, "LauncherCardBody").ActualWidth + 0.5,
                    "OCR execution and hotkeys did not fit or wrap inside the launcher card");
                Check(reserved > 0 && reserved <= 110.5
                    && Math.Abs(reserved - version.ActualWidth) < 1
                    && layout.ColumnDefinitions[0].ActualWidth > 100
                    && status.ActualWidth < layout.ColumnDefinitions[0].ActualWidth
                    && failure.ActualWidth < layout.ColumnDefinitions[0].ActualWidth,
                    "Long status/error text stole space from the compact auto-sized version column");
                Check(layout.ActualHeight <= status.FontSize * 2 + 10, "Status bar grew beyond one line");
                // Rect.IntersectsWith includes a shared edge: measured row 1 ends at 300.03 and row 2 starts at 300.03.
                // Reject every positive-area overlap, including subpixel overlaps; touching edges are not overlapping content.
                var overlap = Rect.Intersect(FeedbackTests.Bounds(main, execution), FeedbackTests.Bounds(main, hotkeyHint));
                Check(overlap.IsEmpty || overlap.Width == 0 || overlap.Height == 0,
                    $"OCR and hotkey labels overlap at width {width}: {overlap}");
                Check(execution.ActualWidth <= Named<Grid>(main, "LauncherCardBody").ActualWidth,
                    "Long OCR status exceeds its card instead of showing ellipsis");
                Check(status.Text == longMessage && execution.Text == longExecution
                    && failure.Text == longFailure,
                    "Truncation must be visual only, preserving the complete diagnostic strings");
            }
            string currentLabel = version.Text;
            version.Text = "Ver 12345678901234567890-beta.9999";
            Layout(main, 360, 485);
            Check(layout.ColumnDefinitions[1].ActualWidth <= 110.5
                && version.ActualWidth <= 110.5
                && layout.ColumnDefinitions[0].ActualWidth > 100,
                "Long version label must be ellipsized without stealing status space");
            version.Text = currentLabel;
            Layout(main, 360, 485);
            Check(layout.ColumnDefinitions[1].ActualWidth < 110
                && layout.ColumnDefinitions[1].ActualWidth >= version.ActualWidth - 1,
                "Short version must use its measured width, not a fixed reservation");
            Check(Equals(status.ToolTip, longMessage)
                && Equals(execution.ToolTip, ((MainViewModel)main.DataContext).OcrExecutionDetail)
                && Equals(failure.ToolTip, longFailure),
                "Full status/error text should remain readable in tooltips");
        }
        finally { Close(main); Directory.Delete(directory, true); }
    }
}
