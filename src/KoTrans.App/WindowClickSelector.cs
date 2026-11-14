using System.Collections.Generic;
using KoTrans.Infrastructure;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KoTrans.App;

internal readonly record struct PhysicalClickPoint(int X, int Y);
internal sealed record WindowClickPickResult(WindowTarget? Target, bool Canceled);

// Full-screen, almost transparent windows consume the click so the game does
// not receive a stray attack/confirm action. Escape cancels without changing the profile.
internal sealed class WindowClickOverlay : Window
{
    private PhysicalClickPoint? pointerDown;
    private readonly Action<PhysicalClickPoint> selectedClick;

    internal WindowClickOverlay(System.Drawing.Rectangle physicalBounds,
        Action<PhysicalClickPoint> selected, Action canceled)
    {
        selectedClick = selected;
        Title = "KO Trans 2 · 클릭해서 게임 창 선택";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = physicalBounds.Left;
        Top = physicalBounds.Top;
        Width = Math.Max(1, physicalBounds.Width);
        Height = Math.Max(1, physicalBounds.Height);
        Cursor = Cursors.Cross;

        var surface = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x3C, 0x10, 0x1B, 0x2A)),
            Cursor = Cursors.Cross
        };
        var help = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x2B, 0x3D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x59, 0xAA, 0xF0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 14, 20, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18, 26, 18, 0),
            Cursor = Cursors.Cross
        };
        var labels = new StackPanel { Cursor = Cursors.Cross };
        labels.Children.Add(new TextBlock
        {
            Text = "⌖  대상 게임 창을 클릭하세요",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, FontSize = 17, TextAlignment = TextAlignment.Center
        });
        labels.Children.Add(new TextBlock
        {
            Text = "클릭은 게임에 전달되지 않습니다.  Esc: 취소",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA5, 0xBC, 0xD1)),
            FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            TextAlignment = TextAlignment.Center
        });
        help.Child = labels;
        surface.Children.Add(help);
        Content = surface;

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (DesktopWindows.GetCursorPos(out var point))
                BeginPointerPress(new PhysicalClickPoint(point.X, point.Y));
            else canceled();
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            // Consume the complete down/up sequence so a hidden overlay cannot
            // leave the mouse-up event to a game underneath.
            e.Handled = true;
            FinishPointerPress();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            canceled();
        };
        Closed += (_, _) => canceled();
    }

    internal void BeginPointerPress(PhysicalClickPoint location) => pointerDown = location;
    internal void FinishPointerPress()
    {
        if (pointerDown is not { } click) return;
        pointerDown = null;
        selectedClick(click);
    }
}

internal static class WindowClickSelector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    // A clicked child control belongs to its root top-level window.
    // Only targets from the existing window enumeration may be selected.
    internal static WindowTarget? MatchCandidate(IReadOnlyList<WindowTarget> candidates, IntPtr topLevelHandle)
    {
        var matches = candidates.Where(w => w.Handle == topLevelHandle).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static async Task<WindowClickPickResult> PickAsync(Func<IReadOnlyList<WindowTarget>> candidates,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) throw new InvalidOperationException("선택 가능한 화면이 없습니다.");
        var selected = new TaskCompletionSource<PhysicalClickPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<WindowClickOverlay>();
        try
        {
            foreach (var screen in screens)
            {
                var area = screen.Bounds;
                var overlay = new WindowClickOverlay(area, point => selected.TrySetResult(point),
                    () => selected.TrySetResult(null));
                overlays.Add(overlay);
                overlay.Show();
                // Physical pixels, including negative coordinates on secondary displays.
                // This avoids mixing WPF DIPs with desktop coordinates on mixed-DPI PCs.
                DesktopWindows.MovePhysical(overlay, area.X, area.Y, area.Width, area.Height);
            }
            overlays[^1].Activate();
            PhysicalClickPoint? point = await selected.Task.WaitAsync(token);
            foreach (var overlay in overlays) overlay.Hide();
            // Ensure the overlay HWNDs are hidden before WindowFromPoint looks underneath.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            token.ThrowIfCancellationRequested();
            if (point is null) return new(null, true);
            var hit = WindowFromPoint(new NativePoint { X = point.Value.X, Y = point.Value.Y });
            var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2); // GA_ROOT
            var target = root == IntPtr.Zero ? null : MatchCandidate(candidates(), root);
            return new(target, false);
        }
        finally
        {
            foreach (var overlay in overlays)
            {
                try { overlay.Close(); }
                catch (InvalidOperationException) { /* Already closed during cancellation. */ }
            }
        }
    }
}
