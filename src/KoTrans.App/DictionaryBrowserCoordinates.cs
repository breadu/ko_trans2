// Adapted independently from breadu/MovieMalhae 88dfc708; word dictionaries only.
using System.Collections.Generic;
namespace KoTrans.App;

public readonly record struct DictionaryDisplay(DictionaryPopupBounds Bounds, DictionaryPopupBounds Work, uint Dpi);

// Chromium's command-line rectangles use a connected DIP desktop, unlike Win32's
// physical-pixel desktop. In particular, dividing a secondary monitor's global
// origin by its own scale puts it on the wrong monitor in a mixed-DPI layout.
public static class DictionaryBrowserCoordinates
{
    public static DictionaryPopupBounds Convert(DictionaryPopupBounds pixels, IReadOnlyList<DictionaryDisplay> displays)
    {
        if (displays.Count == 0 || displays.Any(d => d.Dpi < 96 || d.Bounds.Width <= 0 || d.Bounds.Height <= 0)) return pixels;
        var primary = Enumerable.Range(0, displays.Count).FirstOrDefault(i => displays[i].Bounds.Left == 0 && displays[i].Bounds.Top == 0, -1);
        var target = Enumerable.Range(0, displays.Count).FirstOrDefault(i => Contains(displays[i].Bounds,
            pixels.Left + pixels.Width / 2, pixels.Top + pixels.Height / 2), -1);
        if (primary < 0 || target < 0) return pixels; // Display topology may be changing; native placement remains authoritative.

        var dip = displays.Select(d => Enclosing(d.Bounds, d.Dpi / 96d)).ToArray();
        var pending = Enumerable.Range(0, displays.Count).Where(i => i != primary).ToList();
        var parents = new Stack<int>(); parents.Push(primary);
        while (parents.TryPop(out var parent))
        {
            var children = pending.Where(i => Touches(displays[parent].Bounds, displays[i].Bounds)).ToArray();
            foreach (var child in children)
            {
                dip[child] = PlaceChild(displays[parent], dip[parent], displays[child], dip[child]);
                pending.Remove(child); parents.Push(child);
            }
        }
        // A disconnected virtual desktop cannot be inferred reliably. Keep the
        // existing Win32 fallback rather than inventing a browser monitor origin.
        if (pending.Count != 0) return pixels;
        var monitor = displays[target]; var scale = monitor.Dpi / 96d;
        return new(dip[target].Left + Round((pixels.Left - monitor.Bounds.Left) / scale),
            dip[target].Top + Round((pixels.Top - monitor.Bounds.Top) / scale),
            Math.Max(1, Round(pixels.Width / scale)), Math.Max(1, Round(pixels.Height / scale)));
    }

    private static DictionaryPopupBounds PlaceChild(DictionaryDisplay parent, DictionaryPopupBounds parentDip,
        DictionaryDisplay child, DictionaryPopupBounds childDip)
    {
        var p = parent.Bounds; var c = child.Bounds;
        var verticalEdge = Math.Max(p.Left, c.Left) == Math.Min(Right(p), Right(c));
        var horizontalEdge = Math.Max(p.Top, c.Top) == Math.Min(Bottom(p), Bottom(c));
        // At a shared corner, follow the browser's bottom / left / top precedence.
        var side = verticalEdge && horizontalEdge
            ? Bottom(p) == Math.Max(p.Top, c.Top) ? Side.Bottom : p.Left == Math.Max(p.Left, c.Left) ? Side.Left : Side.Top
            : verticalEdge ? c.Left < p.Left ? Side.Left : Side.Right : c.Top < p.Top ? Side.Top : Side.Bottom;
        var horizontal = side is Side.Top or Side.Bottom;
        var begin = horizontal ? c.Left - p.Left : c.Top - p.Top;
        var length = horizontal ? p.Width : p.Height;
        var end = begin + (horizontal ? c.Width : c.Height);
        var parentLength = horizontal ? parentDip.Width : parentDip.Height;
        var childLength = horizontal ? childDip.Width : childDip.Height;
        int offset;
        if (end == length && begin != 0) offset = parentLength - childLength;
        else if (begin >= 0 && begin <= length) offset = (int)Math.Floor(begin * 96d / parent.Dpi);
        else if (end >= 0 && end <= length) offset = parentLength - childLength - (int)Math.Floor((length - end) * 96d / parent.Dpi);
        else offset = (int)Math.Floor(begin * 96d / child.Dpi);
        offset = Math.Clamp(offset, -childLength, parentLength);
        return side switch
        {
            Side.Left => childDip with { Left = parentDip.Left - childDip.Width, Top = parentDip.Top + offset },
            Side.Right => childDip with { Left = Right(parentDip), Top = parentDip.Top + offset },
            Side.Top => childDip with { Left = parentDip.Left + offset, Top = parentDip.Top - childDip.Height },
            _ => childDip with { Left = parentDip.Left + offset, Top = Bottom(parentDip) }
        };
    }

    private enum Side { Left, Right, Top, Bottom }
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static int Right(DictionaryPopupBounds b) => b.Left + b.Width;
    private static int Bottom(DictionaryPopupBounds b) => b.Top + b.Height;
    private static bool Contains(DictionaryPopupBounds b, int x, int y) => x >= b.Left && x < Right(b) && y >= b.Top && y < Bottom(b);
    private static bool Touches(DictionaryPopupBounds a, DictionaryPopupBounds b) =>
        (Math.Max(a.Left, b.Left) == Math.Min(Right(a), Right(b)) && a.Top <= Bottom(b) && b.Top <= Bottom(a)) ||
        (Math.Max(a.Top, b.Top) == Math.Min(Bottom(a), Bottom(b)) && a.Left <= Right(b) && b.Left <= Right(a));
    private static DictionaryPopupBounds Enclosing(DictionaryPopupBounds b, double scale)
    {
        var x = (int)Math.Floor(b.Left / scale); var y = (int)Math.Floor(b.Top / scale);
        return new(x, y, (int)Math.Ceiling(Right(b) / scale) - x, (int)Math.Ceiling(Bottom(b) / scale) - y);
    }
}
