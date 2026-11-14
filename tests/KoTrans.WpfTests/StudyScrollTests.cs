using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using KoTrans.App;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class StudyScrollTests
{
    internal static void WheelAndPages()
    {
        TimeSpan now = TimeSpan.Zero; bool motion = true;
        using var scroll = new SmoothScrollViewer(() => motion, () => now) {
            Content = new Border { Height = 6000 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var host = Track(new Window { Content = scroll });
        void Frame(int elapsed) { now += TimeSpan.FromMilliseconds(elapsed); scroll.AdvanceAnimation(); Layout(host, 500, 400); }
        try {
            Layout(host, 500, 400);
            Check(scroll.ScrollWheel(-120, 5) && scroll.IsAnimating && scroll.TargetOffset == 145
                && scroll.VerticalOffset == 0, "Wheel did not use body-line height and Windows line count");
            Frame(20); double early = scroll.VerticalOffset;
            Check(early > 0 && early < 145, "Wheel must begin moving within its first frame without jumping a full notch");
            Frame(50); Check(scroll.VerticalOffset > early && Math.Abs(scroll.VerticalOffset - 145 * .875) < 1,
                "Wheel progress did not use elapsed-time easing");
            Frame(70); Check(!scroll.IsAnimating && Math.Abs(scroll.VerticalOffset - 145) < .1, "Scroll did not settle at 140ms");
            scroll.ResetToTop(); Layout(host, 500, 400);
            scroll.ScrollWheel(-120, 3); scroll.ScrollWheel(-120, 3); scroll.ScrollWheel(-120, 3);
            Check(scroll.TargetOffset == 261, "Rapid wheel events lost their accumulated distance");
            Frame(20); double beforeReverse = scroll.VerticalOffset;
            scroll.ScrollWheel(120, 3);
            Check(scroll.TargetOffset < beforeReverse, "Reversing wheel input continued toward the old destination");
            Frame(140); Check(!scroll.IsAnimating && scroll.VerticalOffset < beforeReverse, "Reversed animation overshot or failed to settle");

            motion = false; scroll.ResetToTop(); Layout(host, 500, 400);
            scroll.ScrollWheel(-30, 4); Layout(host, 500, 400);
            Check(!scroll.IsAnimating && scroll.VerticalOffset == 29, "High-resolution wheel delta or reduced-motion preference was ignored");
            double before = scroll.VerticalOffset; scroll.ScrollWheel(-120, 0); Layout(host, 500, 400);
            Check(scroll.VerticalOffset == before, "Disabled Windows wheel scrolling still moved");
            scroll.ScrollWheel(-120, -1); Layout(host, 500, 400);
            Check(Math.Abs(scroll.VerticalOffset - before - scroll.ViewportHeight) < 1, "Windows page-per-notch preference was ignored");
            scroll.ScrollPage(false); Layout(host, 500, 400);
            Check(Math.Abs(scroll.VerticalOffset - before) < 1, "Remote upward page amount changed");
            motion = true; scroll.ScrollPage(true);
            Check(scroll.IsAnimating && Math.Abs(scroll.TargetOffset - before - scroll.ViewportHeight) < 1,
                "Remote page command did not share the reader's smooth scrolling");
            Frame(140);
            scroll.ScrollWheel(-120000, 5); Frame(140);
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "Wheel exceeded or failed to reach the bottom");
            scroll.ScrollWheel(-120, 5); Check(!scroll.IsAnimating, "Wheel at bottom kept an idle rendering callback running");
            scroll.ScrollWheel(120000, 5); Frame(140);
            Check(scroll.VerticalOffset == 0 && !scroll.IsAnimating, "Wheel exceeded the top");
            Check(new WindowInteropHelper(host).Handle == IntPtr.Zero && !host.IsVisible, "Scroll test created a real window");
        } finally { Close(host); }
    }
    internal static void CancellationAndReaderLifecycle()
    {
        TimeSpan now = TimeSpan.Zero;
        using var scroll = new SmoothScrollViewer(() => true, () => now) { Content = new Border { Height = 4000 } };
        var host = Track(new Window { Content = scroll });
        try {
            Layout(host, 500, 400); scroll.ScrollWheel(-120, 3);
            var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            scroll.RaiseEvent(mouse);
            Check(!scroll.IsAnimating, "Scrollbar/direct pointer input did not cancel old wheel movement");
            scroll.ScrollWheel(-120, 3); scroll.ResetToTop(); Layout(host, 500, 400);
            now += TimeSpan.FromSeconds(1); scroll.AdvanceAnimation(); Layout(host, 500, 400);
            Check(!scroll.IsAnimating && scroll.VerticalOffset == 0, "Reset allowed a late old frame to move the new document");
            scroll.ScrollWheel(-120, 3); scroll.Content = new Border { Height = 40 }; Layout(host, 500, 400);
            Check(!scroll.IsAnimating && !scroll.ScrollWheel(-120, 3), "Resized/short content kept obsolete animation bounds");
            scroll.Content = new Border { Height = 4000 }; Layout(host, 500, 400); scroll.ScrollWheel(-120, 3);
            scroll.Dispose(); now += TimeSpan.FromSeconds(1); scroll.AdvanceAnimation();
            Check(!scroll.IsAnimating && !scroll.ScrollWheel(-120, 3), "Disposed reader retained wheel/render callbacks");
        } finally { Close(host); }

        var reader = Track(new StudyWindow(new(), resolveVoiceDirectory: () => null));
        try {
            reader.SetSource("An English sentence.", "en");
            reader.Render(WpfTests.Example(string.Join(" ", Enumerable.Repeat("Long example paragraph.", 200))));
            Layout(reader, 500, 600);
            var actual = (SmoothScrollViewer)reader.Content;
            // Synthetic routed event exercises the actual reader's input handler.
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent };
            actual.RaiseEvent(wheel);
            Check(wheel.Handled, "Reader did not route wheel input to its scroll control");
            reader.Clear(); Check(!actual.IsAnimating, "Clear left the previous result scrolling");
            reader.Render(WpfTests.Example(new string('a', 3000))); Layout(reader, 500, 600);
            actual.ScrollWheel(-120, 3); reader.SetSource("Next source.", "en"); Layout(reader, 500, 600);
            Check(!actual.IsAnimating && actual.VerticalOffset == 0, "New source retained old scrolling or offset");
            reader.Render(WpfTests.Example(new string('b', 3000))); Layout(reader, 500, 600);
            actual.ScrollWheel(-120, 3); reader.Close();
            Check(!actual.IsAnimating && !reader.IsVisible, "Hidden resident reader retained its rendering callback");
            actual.ScrollWheel(-120, 3); Complete(reader.CloseForExitAsync());
            Check(!actual.IsAnimating && !actual.ScrollWheel(-120, 3), "Reader exit did not dispose scrolling");
        } finally { Close(reader); }
    }
}
