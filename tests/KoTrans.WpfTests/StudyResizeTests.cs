using System.Windows;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class StudyResizeTests
{
    internal static void Borders()
    {
        var reader = Track(new StudyWindow(new AppSettings(), resolveVoiceDirectory: () => null));
        try {
            Check(reader.WindowStyle == WindowStyle.SingleBorderWindow && reader.ResizeMode == ResizeMode.CanResize
                && !reader.AllowsTransparency, "Reader lost standard Windows frame");
            // Frame dimensions are physical system metrics, not an arbitrary DIP enlargement.
            foreach (var frame in new[] { (8, 8), (16, 16), (12, 18) }) {
                var rect = new Rect(-1600, -500, 600, 800);
                for (int x = 0; x < frame.Item1; x++) {
                    Check(Hit(new(rect.Left + x, rect.Top + 300)) == 10, "Left native frame has a gap");
                    Check(Hit(new(rect.Right - 1 - x, rect.Top + 300)) == 11, "Right native frame has a gap");
                }
                for (int y = 0; y < frame.Item2; y++) {
                    Check(Hit(new(rect.Left + 300, rect.Top + y), 2) == 12, "Top native frame has a gap");
                    Check(Hit(new(rect.Left + 300, rect.Bottom - 1 - y)) == 15, "Bottom native frame has a gap");
                }
                Check(Hit(new(rect.Left + frame.Item1, rect.Top + 300)) == 1
                    && Hit(new(rect.Right - frame.Item1 - 1, rect.Top + 300)) == 1
                    && Hit(new(rect.Left + 300, rect.Bottom - frame.Item2 - 1)) == 1,
                    "Resize fix extended into client area/scrollbar");
                Check(Hit(new(rect.Left + 300, rect.Top + frame.Item2), 2) == 2, "Caption drag area was enlarged into");
                Check(Hit(new(rect.Left + 1, rect.Top + 1)) == 13 && Hit(new(rect.Right - 1, rect.Top + 1)) == 14
                    && Hit(new(rect.Left + 1, rect.Bottom - 1)) == 16 && Hit(new(rect.Right - 1, rect.Bottom - 1)) == 17,
                    "Native frame corner directions wrong");
                foreach (int native in new[] { -1, 0, 3, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 20, 21 })
                    Check(Hit(new(rect.Left + 1, rect.Top + 1), native) == native, "Existing native hit/button was overridden");
                Check(Hit(new(rect.Left - 1, rect.Top + 300)) == 1 && Hit(new(rect.Right, rect.Top + 300)) == 1,
                    "Hit fix escaped the window");
                int Hit(Point p, int native = 1) => NativeResizeBorder.Resolve(native, p, rect, frame.Item1, frame.Item2);
            }
            var packed = new IntPtr(unchecked((int)((uint)(ushort)-1500 | ((uint)(ushort)-500 << 16))));
            Check(NativeResizeBorder.ScreenPoint(packed) == new Point(-1500, -500), "Negative monitor coordinates were decoded unsigned");
            Check(new WindowInteropHelper(reader).Handle == IntPtr.Zero && !reader.IsVisible, "Background test created native/visible reader");
        } finally { Close(reader); }
    }
}
