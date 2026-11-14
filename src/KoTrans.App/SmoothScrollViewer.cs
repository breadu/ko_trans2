using System;
using System.Diagnostics;
using System.Windows.Input;

namespace KoTrans.App;

// Shared by the study reader and long settings pages. Scrolling never saves settings or runs OCR/AI.
public sealed class SmoothScrollViewer : ScrollViewer, IDisposable
{
    private const double TextLineHeight = 29;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(140);
    private readonly Func<bool> animationsEnabled;
    private readonly Func<TimeSpan> clock;
    private TimeSpan started;
    private double start, target;
    private bool animating, disposed;
    internal bool IsAnimating => animating;
    internal double TargetOffset => target;

    public SmoothScrollViewer() : this(null, null) { }
    internal SmoothScrollViewer(Func<bool>? animationsEnabled, Func<TimeSpan>? clock = null)
    {
        this.animationsEnabled = animationsEnabled ?? (() => SystemParameters.ClientAreaAnimation);
        long epoch = Stopwatch.GetTimestamp();
        this.clock = clock ?? (() => Stopwatch.GetElapsedTime(epoch));
        CanContentScroll = false;
        PanningMode = PanningMode.VerticalOnly;
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopAnimation(); };
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollWheel(e.Delta, SystemParameters.WheelScrollLines)) e.Handled = true;
        else base.OnMouseWheel(e);
    }
    internal bool ScrollWheel(int delta, int lines)
    {
        if (disposed || delta == 0 || ScrollableHeight <= 0) return false;
        if (lines == 0) return true; // Respect the Windows "do not scroll" preference.
        double distance = lines < 0 ? ViewportHeight : lines * TextLineHeight;
        MoveBy(-delta / (double)Mouse.MouseWheelDeltaForOneLine * distance);
        return true;
    }
    internal void ScrollPage(bool down) => MoveBy((down ? 1 : -1) * ViewportHeight);
    private void MoveBy(double distance)
    {
        if (disposed || ScrollableHeight <= 0 || distance == 0) return;
        bool sameDirection = animating && Math.Sign(distance) == Math.Sign(target - VerticalOffset);
        double next = Math.Clamp((sameDirection ? target : VerticalOffset) + distance, 0, ScrollableHeight);
        // Avoid winding up movement at an edge or losing rapid/reversed wheel input.
        if (animating && next == target) return;
        StopAnimation();
        target = next;
        if (!animationsEnabled() || Math.Abs(target - VerticalOffset) < .1) {
            ScrollToVerticalOffset(target); return;
        }
        start = VerticalOffset; started = clock(); animating = true;
        CompositionTarget.Rendering += Rendering;
    }
    private void Rendering(object? sender, EventArgs e) => AdvanceAnimation();
    internal void AdvanceAnimation()
    {
        if (!animating) return;
        double progress = Math.Clamp((clock() - started).TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
        double eased = 1 - Math.Pow(1 - progress, 3);
        ScrollToVerticalOffset(start + (target - start) * eased);
        if (progress >= 1) StopAnimation();
    }
    internal void StopAnimation()
    {
        if (!animating) return;
        CompositionTarget.Rendering -= Rendering;
        animating = false;
    }
    internal void ResetToTop()
    {
        StopAnimation(); target = 0; ScrollToTop();
    }
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        StopAnimation(); // Thumb dragging/track clicking must take control immediately.
        base.OnPreviewMouseDown(e);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        StopAnimation();
        base.OnPreviewKeyDown(e);
    }
    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        StopAnimation();
        base.OnManipulationStarting(e);
    }
    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) StopAnimation();
        base.OnScrollChanged(e);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; StopAnimation();
    }
}
