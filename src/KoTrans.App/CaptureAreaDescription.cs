namespace KoTrans.App;

// UI-only formatting: OCR coordinates and stored Profile/Pixels remain unchanged.
internal static class CaptureAreaDescription
{
    internal static string ForProfile(Profile profile)
    {
        if (profile.CaptureMode == CaptureMode.Clipboard)
            return "클립보드 입력 모드 · 화면의 인식 영역을 사용하지 않습니다.";

        if (profile.CaptureMode == CaptureMode.Window)
        {
            if (profile.GameWindow is null)
                return "아직 대상 게임 창을 선택하지 않았습니다.\n위의 ‘대상 창 선택’ 또는 ‘대상 창 직접 클릭’을 이용하세요.";
            if (profile.WindowCaptureRegion is { } relative)
                return "게임 창 내부 기준\n" + Position(relative) + "\n" + Size(relative);
            if (profile.CaptureAreaConfigured)
                return "이전에 지정한 화면 기준 영역 (게임 창 안의 상대 영역은 아직 미지정)\n"
                    + Position(profile.CaptureArea) + "\n" + Size(profile.CaptureArea);
            return "게임 창의 인식 영역을 아직 지정하지 않았습니다.\n‘영역 지정’을 눌러 대사 부분을 드래그하세요.";
        }

        if (!profile.CaptureAreaConfigured)
            return "인식 영역을 아직 지정하지 않았습니다.\n‘영역 지정’을 눌러 대사 부분을 드래그하세요.";
        return "화면(바탕화면) 기준\n" + Position(profile.CaptureArea) + "\n" + Size(profile.CaptureArea);
    }

    // Compact, user-facing summary. Keep ForProfile() unchanged for full
    // coordinate explanations and existing regression/diagnostic consumers.
    internal static string CompactForProfile(Profile profile)
    {
        if (profile.CaptureMode == CaptureMode.Clipboard)
            return "클립보드 입력 · 캡처 영역 없음";

        if (profile.CaptureMode == CaptureMode.Window)
        {
            if (profile.GameWindow is null)
                return "게임 창을 먼저 선택하세요.";
            if (profile.WindowCaptureRegion is { } region)
                return Coordinates("게임 창 내부 기준", region);
            if (profile.CaptureAreaConfigured)
                return Coordinates("이전 화면 기준 · 창 내부 영역 재지정 필요", profile.CaptureArea);
            return "게임 창의 대사 영역을 지정하세요.";
        }

        return profile.CaptureAreaConfigured
            ? Coordinates("화면 기준", profile.CaptureArea)
            : "대사 영역을 지정하세요.";
    }

    // These three lines are shown at the top-left of the capture-area section,
    // independently from the fixed-size diagram on the right. No capture
    // coordinates or stored profile fields are changed here.
    internal sealed record PreviewLines(string Basis, string Position, string Size);

    internal static PreviewLines PreviewForProfile(Profile profile)
    {
        if (profile.CaptureMode == CaptureMode.Clipboard)
            return new("클립보드 입력", "캡처 영역 없음", "");

        if (profile.CaptureMode == CaptureMode.Window)
        {
            if (profile.GameWindow is null)
                return new("게임 창 내부 기준", "게임 창을 먼저 선택하세요.", "");
            if (profile.WindowCaptureRegion is { } region)
                return PreviewCoordinates("게임 창 내부 기준", region);
            if (profile.CaptureAreaConfigured)
                return PreviewCoordinates("이전 화면 기준 · 창 내부 영역 재지정 필요", profile.CaptureArea);
            return new("게임 창 내부 기준", "대사 영역을 지정하세요.", "");
        }

        return profile.CaptureAreaConfigured
            ? PreviewCoordinates("화면 기준", profile.CaptureArea)
            : new("화면 기준", "대사 영역을 지정하세요.", "");
    }

    private static PreviewLines PreviewCoordinates(string basis, PixelRect area) =>
        new(basis, $"위치  X {area.X:N0} · Y {area.Y:N0}",
            $"크기  {area.Width:N0} × {area.Height:N0} px");

    private static string Coordinates(string basis, PixelRect area) =>
        $"{basis} · X {area.X:N0} · Y {area.Y:N0}  |  {area.Width:N0} × {area.Height:N0} px";

    private static string Position(PixelRect area)
    {
        static string Horizontal(int x) => x < 0
            ? $"기준점 왼쪽 {-(long)x:N0}px" : $"왼쪽에서 {x:N0}px";
        static string Vertical(int y) => y < 0
            ? $"기준점 위쪽 {-(long)y:N0}px" : $"위에서 {y:N0}px";
        return $"시작 위치: {Horizontal(area.X)} · {Vertical(area.Y)}";
    }

    private static string Size(PixelRect area) =>
        $"선택한 크기: 가로 {area.Width:N0}px × 세로 {area.Height:N0}px";
}
