using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class CaptureAreaDescriptionTests
{
    internal static void Cases()
    {
        var profile = new Profile { CaptureMode = CaptureMode.Region, CaptureAreaConfigured = false };
        Check(CaptureAreaDescription.ForProfile(profile).Contains("아직 지정하지"),
            "Unconfigured area should explain how to select a game text region");
        profile.CaptureAreaConfigured = true;
        profile.CaptureArea = new PixelRect(1240, 96, 620, 185);
        var summary = CaptureAreaDescription.ForProfile(profile);
        Check(summary.Contains("화면(바탕화면) 기준")
            && summary.Contains("왼쪽에서 1,240px") && summary.Contains("위에서 96px")
            && summary.Contains("가로 620px") && summary.Contains("세로 185px"),
            "Screen area uses unexplained raw coordinates");
        profile.CaptureArea = new PixelRect(-1440, -220, 640, 160);
        summary = CaptureAreaDescription.ForProfile(profile);
        Check(summary.Contains("기준점 왼쪽 1,440px") && summary.Contains("기준점 위쪽 220px"),
            "Negative coordinates on an extra monitor were shown as negative distances");
        profile.CaptureMode = CaptureMode.Window;
        Check(CaptureAreaDescription.ForProfile(profile).Contains("대상 게임 창"),
            "Window mode without a selected game did not explain selection");
        profile.GameWindow = new GameWindowIdentity("synthetic", "", "", "Game");
        profile.WindowCaptureRegion = new PixelRect(42, 60, 310, 98);
        summary = CaptureAreaDescription.ForProfile(profile);
        Check(summary.Contains("게임 창 내부 기준") && summary.Contains("왼쪽에서 42px")
            && summary.Contains("위에서 60px") && summary.Contains("가로 310px"),
            "Game-window-relative crop was presented as an absolute screen crop");
        profile.WindowCaptureRegion = null;
        Check(CaptureAreaDescription.ForProfile(profile).Contains("이전에 지정한 화면 기준"),
            "Legacy absolute crop was misrepresented as window-relative");
        profile.CaptureMode = CaptureMode.Clipboard;
        Check(CaptureAreaDescription.ForProfile(profile).Contains("사용하지 않습니다")
            && CaptureAreaDescription.CompactForProfile(profile).Contains("클립보드 입력"),
            "Clipboard mode should not show an irrelevant screen rectangle");

        profile.CaptureMode = CaptureMode.Window;
        profile.WindowCaptureRegion = new PixelRect(42, 60, 310, 98);
        Check(CaptureAreaDescription.CompactForProfile(profile).Contains("게임 창 내부 기준 · X 42 · Y 60")
            && CaptureAreaDescription.CompactForProfile(profile).Contains("310 × 98 px"),
            "Compact game capture summary does not show its relative basis");
        profile.WindowCaptureRegion = null; profile.CaptureAreaConfigured = true;
        Check(CaptureAreaDescription.CompactForProfile(profile).Contains("창 내부 영역 재지정 필요"),
            "Legacy screen-area crop was misrepresented as a confirmed window crop");
        profile.CaptureMode = CaptureMode.Region; profile.CaptureArea = new PixelRect(-1440, -220, 640, 160);
        Check(CaptureAreaDescription.CompactForProfile(profile).Contains("X -1,440 · Y -220")
            && CaptureAreaDescription.CompactForProfile(profile).Contains("640 × 160 px"),
            "Compact display lost signed multi-monitor coordinates");

        // Layout presentation separates basis from coordinates without
        // changing the original full technical description or saved geometry.
        var parts = CaptureAreaDescription.PreviewForProfile(profile);
        Check(parts.Basis == "화면 기준"
            && parts.Position.Contains("X -1,440 · Y -220")
            && parts.Size == "크기  640 × 160 px",
            "Screen-based preview lines lost signed multi-monitor coordinates");
        profile.CaptureMode = CaptureMode.Window;
        profile.WindowCaptureRegion = new PixelRect(42, 60, 310, 98);
        parts = CaptureAreaDescription.PreviewForProfile(profile);
        Check(parts.Basis == "게임 창 내부 기준"
            && parts.Position == "위치  X 42 · Y 60"
            && parts.Size == "크기  310 × 98 px",
            "Window-relative preview lines must keep local coordinates");
        profile.WindowCaptureRegion = null;
        parts = CaptureAreaDescription.PreviewForProfile(profile);
        Check(parts.Basis.Contains("창 내부 영역 재지정 필요")
            && parts.Position.Contains("X -1,440") && parts.Size.Contains("640 × 160"),
            "Legacy screen coordinates must not be misrepresented as a window crop");
        profile.GameWindow = null;
        Check(CaptureAreaDescription.PreviewForProfile(profile) is { Position: "게임 창을 먼저 선택하세요.", Size: "" },
            "Missing game selection must not display stale crop coordinates");
        profile.CaptureMode = CaptureMode.Clipboard;
        Check(CaptureAreaDescription.PreviewForProfile(profile) is { Basis: "클립보드 입력", Position: "캡처 영역 없음", Size: "" },
            "Clipboard mode should not display screen coordinates");
        profile.CaptureMode = CaptureMode.Region; profile.CaptureAreaConfigured = false;
        Check(CaptureAreaDescription.PreviewForProfile(profile) is { Basis: "화면 기준", Position: "대사 영역을 지정하세요.", Size: "" },
            "Unconfigured region needs a simple capture-area instruction");

        var data = new AppSettings(); data.Validate();
        data.DefaultProfile.CaptureMode = CaptureMode.Region;
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KoTrans2.AreaDisplay",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var window = Track(new MainWindow(new SettingsStore(directory),
            new ApiKeyStore(directory), () => [], _ => new PixelRect(0, 0, 1024, 768)));
        try
        {
            window.ApplyLoadedSettings(data);
            window.ShowProfileSettingsPage(1);
            Layout(window, 1060, 740); // Resolve the real WPF bindings before reading control text.
            var label = Named<TextBlock>(window, "SavedAreaSummary");
            var basis = Named<TextBlock>(window, "CaptureAreaBasisText");
            var size = Named<TextBlock>(window, "CaptureAreaSizeText");
            var divider = Named<Border>(window, "CaptureAreaSeparator");
            var heading = Named<TextBlock>(window, "CaptureAreaHeading");
            var card = Named<Border>(window, "GameCaptureCard");
            var primary = Elements<TextBlock>(card).Single(x => x.Text == "게임 캡처");
            Check(divider.Height == 1 && heading.FontSize < primary.FontSize
                && ((StackPanel)label.Parent).VerticalAlignment == VerticalAlignment.Top,
                "Capture-area subsection must have a subtle divider, smaller heading and top-aligned content");
            Check(basis.Text == "화면 기준" && label.Text.Contains("지정하세요") && size.Text == "",
                "Initial capture basis/instruction did not bind");
            data.DefaultProfile.CaptureAreaConfigured = true;
            data.DefaultProfile.CaptureArea = new PixelRect(150, 95, 360, 120);
            window.ApplyLoadedSettings(data);
            Layout(window, 1060, 740);
            Check(basis.Text == "화면 기준"
                && label.Text == "위치  X 150 · Y 95"
                && size.Text == "크기  360 × 120 px"
                && label.ToolTip is string detail && detail.Contains("가로 360px"),
                "Basis, position and size should refresh as independent top-aligned rows");
            var cardTop = heading.TranslatePoint(new Point(0, 0), card).Y;
            var basisTop = basis.TranslatePoint(new Point(0, 0), card).Y;
            var posTop = label.TranslatePoint(new Point(0, 0), card).Y;
            var sizeTop = size.TranslatePoint(new Point(0, 0), card).Y;
            var dividerTop = divider.TranslatePoint(new Point(0, 0), card).Y;
            Check(dividerTop < cardTop && cardTop < basisTop && basisTop < posTop && posTop < sizeTop,
                "Capture-area basis, position and size should flow immediately below the subsection heading");
            Named<ComboBox>(window, "InputMode").SelectedIndex = 2;
            Pump();
            Check(basis.Text == "클립보드 입력" && label.Text == "캡처 영역 없음" && size.Text == "",
                "Clipboard input retained screen-based coordinates");
        }
        finally { Close(window); System.IO.Directory.Delete(directory, true); }
    }
}
