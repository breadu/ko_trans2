namespace KoTrans.Core;

public sealed record GameWindowIdentity(string ProcessName, string ExecutablePath, string WindowClass, string Title)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProcessName) || ProcessName.Length > 260 || ExecutablePath is null || ExecutablePath.Length > 2048
            || WindowClass is null || WindowClass.Length > 512 || string.IsNullOrWhiteSpace(Title) || Title.Length > 512)
            throw new InvalidDataException("게임 창 식별 정보를 확인하세요.");
    }
    public bool Matches(GameWindowIdentity other) => string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase)
        && (ExecutablePath.Length == 0 || string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        && (WindowClass.Length == 0 || WindowClass == other.WindowClass) && Title == other.Title;
    // A persisted PID/HWND cannot identify a restarted game. Its executable path
    // and top-level class can; the title is a display/legacy hint, often a scene.
    public bool MatchesStableIdentity(GameWindowIdentity other) => ExecutablePath.Length > 0 && WindowClass.Length > 0
        && string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && WindowClass == other.WindowClass;
}

public sealed record DisplayArea(string Id, string Device, PixelRect WorkArea, double DpiScale, bool Primary = false);
public sealed record PlacementResolution(int X, int Y, double Width, double Height, bool Temporary);
public static class OverlayPlacementPolicy
{
    public static PlacementResolution Resolve(WindowPlacement saved, IReadOnlyList<DisplayArea> displays)
    {
        if (displays.Count == 0) throw new InvalidOperationException("사용 가능한 모니터가 없습니다.");
        DisplayArea? screen = null;
        if (saved.MonitorId.Length > 0) {
            var matches = displays.Where(d => d.Id == saved.MonitorId).ToArray();
            if (matches.Length == 1) screen = matches[0];
        } else if (saved.MonitorDevice.Length > 0) {
            var matches = displays.Where(d => d.Device == saved.MonitorDevice).ToArray();
            if (matches.Length == 1) screen = matches[0];
        } else screen = displays.FirstOrDefault(d => Contains(d.WorkArea, saved.X, saved.Y));
        bool temporary = screen is null;
        screen ??= displays.FirstOrDefault(d => d.Primary) ?? displays[0];
        var area = screen.WorkArea; double dpi = screen.DpiScale;
        if (!double.IsFinite(dpi) || dpi <= 0) throw new InvalidDataException("모니터 DPI가 올바르지 않습니다.");
        long x = temporary ? area.X + 30 : saved.MonitorWorkX.HasValue ? (long)area.X + saved.X - saved.MonitorWorkX.Value : saved.X;
        long y = temporary ? area.Y + 30 : saved.MonitorWorkY.HasValue ? (long)area.Y + saved.Y - saved.MonitorWorkY.Value : saved.Y;
        double width = Math.Min(saved.Width, area.Width / dpi), height = Math.Min(saved.Height, area.Height / dpi);
        int pxWidth = (int)Math.Ceiling(width * dpi), pxHeight = (int)Math.Ceiling(height * dpi);
        return new((int)Math.Clamp(x, area.X, (long)area.X + Math.Max(0, area.Width - pxWidth)),
            (int)Math.Clamp(y, area.Y, (long)area.Y + Math.Max(0, area.Height - pxHeight)), width, height, temporary);
    }
    private static bool Contains(PixelRect area, int x, int y) => x >= area.X && y >= area.Y && (long)x < (long)area.X + area.Width && (long)y < (long)area.Y + area.Height;
}
