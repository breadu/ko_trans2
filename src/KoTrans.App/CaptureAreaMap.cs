using System.Collections.Generic;

namespace KoTrans.App;

// Geometry only. Never captures a monitor or reads pixels from a game window.
public sealed class CaptureAreaMap : FrameworkElement
{
    private IReadOnlyList<PixelRect> displays = [];
    private PixelRect? area;
    internal IReadOnlyList<Rect> DisplayRects { get; private set; } = [];
    internal Rect? AreaRect { get; private set; }
    public CaptureAreaMap() { IsHitTestVisible = false; }
    internal void SetGeometry(IReadOnlyList<PixelRect> screens, PixelRect? selection)
    {
        displays = screens.Where(s => s.IsValid).ToArray(); area = selection;
        Visibility = displays.Count > 0 && area is not null ? Visibility.Visible : Visibility.Collapsed;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); DisplayRects = []; AreaRect = null;
        if (displays.Count == 0 || area is not { } selected) return;
        double left = displays.Min(s => (double)s.X), top = displays.Min(s => (double)s.Y);
        double width = displays.Max(s => (double)s.X + s.Width) - left;
        double height = displays.Max(s => (double)s.Y + s.Height) - top;
        double scale = Math.Min(Math.Max(0, ActualWidth - 8) / width, Math.Max(0, ActualHeight - 8) / height);
        double ox = (ActualWidth - width * scale) / 2, oy = (ActualHeight - height * scale) / 2;
        Rect Map(PixelRect r) => new(ox + (r.X - left) * scale, oy + (r.Y - top) * scale, r.Width * scale, r.Height * scale);
        DisplayRects = displays.Select(Map).ToArray();
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(117, 143, 165)), 1);
        foreach (var rect in DisplayRects) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 34, 53)), pen, rect);
        var clip = new GeometryGroup(); foreach (var rect in DisplayRects) clip.Children.Add(new RectangleGeometry(rect));
        AreaRect = Map(selected); dc.PushClip(clip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(115, 68, 165, 245)), new Pen(Brushes.LightSkyBlue, 1), AreaRect.Value);
        dc.Pop();
    }
}
