using System.Windows.Interop;
using KoTrans.Infrastructure;

namespace KoTrans.App;

public partial class MainWindow
{
    private Size availableWindowSize = new(double.PositiveInfinity, double.PositiveInfinity);
    private DisplayArea? layoutDisplay;
    private bool fittingWindow;
    private bool? narrowLayout;
    private bool minimumUpdateQueued;
    internal WindowPlacement? PendingStartupPlacement { get; private set; }

    private void InitializeResponsiveLayout()
    {
        SourceInitialized += (_, _) => System.Windows.Interop.HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)
            .AddHook(ResizeBorderMessage);
        LocationChanged += (_, _) => FitToCurrentDisplay(false);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => FitToCurrentDisplay(false)));
        StateChanged += (_, _) => FitToCurrentDisplay(false);
        SourceInitialized += (_, _) => RefreshCaptureMap();
    }
    private IntPtr ResizeBorderMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        message == NativeResizeBorder.HitTestMessage ? NativeResizeBorder.Handle(this, handle, wParam, lParam, ref handled) : IntPtr.Zero;
    private void PrepareInitialPlacement(WindowPlacement saved)
    {
        // SourceInitialized occurs before Show paints the window. Never move it in ContentRendered.
        PendingStartupPlacement = new() { X = saved.X, Y = saved.Y, Width = 820, Height = 485,
            MonitorId = saved.MonitorId, MonitorDevice = saved.MonitorDevice,
            MonitorWorkX = saved.MonitorWorkX, MonitorWorkY = saved.MonitorWorkY };
        DesktopWindows.Restore(this, PendingStartupPlacement);
        SourceInitialized += (_, _) => FitToCurrentDisplay(true);
    }
    internal static Size ViewSize(bool settingsView, Size available) => new(
        Math.Min(settingsView ? 1060 : 820, available.Width),
        Math.Min(settingsView ? 740 : 485, available.Height));

    // Also used with synthetic monitor work sizes by the HWND-free WPF tests.
    internal void ApplyAvailableWindowSize(Size available, bool resizeView = true)
    {
        availableWindowSize = available;
        MinWidth = Math.Min(360, available.Width);
        MaxWidth = available.Width; MaxHeight = available.Height;
        var size = resizeView ? ViewSize(profileSettingsOpen, available) : new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
        Width = size.Width;
        UpdateMinimumHeight(Math.Max(1, size.Width - DesktopWindows.ChromeSize(this).Width));
        Height = Math.Max(MinHeight, size.Height);
    }
    private void UpdateMinimumHeight(double clientWidth)
    {
        double minimum = 400;
        if (!profileSettingsOpen) {
            ApplyResponsiveLayout(clientWidth);
            // Measure without reserving scrollbar width. An already visible
            // scrollbar can wrap the same text onto more lines and keep itself visible.
            LauncherScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            double width = Math.Max(1, clientWidth - LauncherView.Margin.Left - LauncherView.Margin.Right);
            double columnWidth = narrowLayout == true ? width : width * 1.7 / 2.7;
            var card = LauncherTranslationCard;
            double contentWidth = Math.Max(1, columnWidth - card.Margin.Left - card.Margin.Right
                - card.Padding.Left - card.Padding.Right - card.BorderThickness.Left - card.BorderThickness.Right);
            // Text changes may be queued for layout when sizing starts. Refresh
            // the summary's measure path before reading the card's desired height.
            for (DependencyObject? element = TranslationSummary; element is UIElement child && element != LauncherContent;
                element = VisualTreeHelper.GetParent(element))
                child.InvalidateMeasure();
            LauncherContent.InvalidateMeasure();
            LauncherContent.Measure(new Size(contentWidth, double.PositiveInfinity));
            MainHeader.Measure(new Size(clientWidth, double.PositiveInfinity));
            MainStatus.Measure(new Size(clientWidth, double.PositiveInfinity));
            LauncherHelpActions.Measure(new Size(columnWidth, double.PositiveInfinity));
            minimum = Math.Max(minimum, Math.Ceiling(MainHeader.DesiredSize.Height + MainStatus.DesiredSize.Height
                + LauncherView.Margin.Top + LauncherView.Margin.Bottom + LauncherContent.DesiredSize.Height
                + card.Margin.Top + card.Margin.Bottom + card.Padding.Top + card.Padding.Bottom
                + card.BorderThickness.Top + card.BorderThickness.Bottom + LauncherHelpActions.DesiredSize.Height
                + DesktopWindows.ChromeSize(this).Height + 2));
            if (minimum > availableWindowSize.Height)
                LauncherScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        MinHeight = Math.Min(minimum, availableWindowSize.Height);
        if (Height < MinHeight) Height = MinHeight;
    }
    private void QueueMinimumHeightUpdate()
    {
        if (profileSettingsOpen || minimumUpdateQueued || MainRoot.ActualWidth <= 0) return;
        minimumUpdateQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
            minimumUpdateQueued = false;
            if (!profileSettingsOpen && !Dispatcher.HasShutdownStarted)
                UpdateMinimumHeight(MainRoot.ActualWidth);
        }));
    }
    private void LauncherContentSizeChanged(object sender, SizeChangedEventArgs e) => QueueMinimumHeightUpdate();

    // Same production path for native display changes and HWND-free synthetic monitors.
    internal bool ApplyDisplayLayout(DisplayArea display, bool resizeView = false)
    {
        bool changed = display != layoutDisplay;
        if (!resizeView && !changed) return false;
        layoutDisplay = display;
        ApplyAvailableWindowSize(new(display.WorkArea.Width / display.DpiScale, display.WorkArea.Height / display.DpiScale),
            resizeView || changed);
        return true;
    }
    private void ResizeForView()
    {
        if (WindowState != WindowState.Normal) return;
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) ApplyAvailableWindowSize(availableWindowSize);
        else FitToCurrentDisplay(true);
    }
    private void FitToCurrentDisplay(bool resizeView)
    {
        if (fittingWindow || WindowState == WindowState.Minimized || new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        var display = DesktopWindows.CurrentDisplay(this);
        fittingWindow = true;
        try {
            if (!ApplyDisplayLayout(display, resizeView) || WindowState != WindowState.Normal) return;
            var bounds = WindowTargets.Bounds(new WindowInteropHelper(this).Handle);
            if (bounds is { } r) {
                int x = (int)Math.Clamp((long)r.X, display.WorkArea.X, (long)display.WorkArea.X + Math.Max(0, display.WorkArea.Width - (int)Math.Ceiling(Width * display.DpiScale)));
                int y = (int)Math.Clamp((long)r.Y, display.WorkArea.Y, (long)display.WorkArea.Y + Math.Max(0, display.WorkArea.Height - (int)Math.Ceiling(Height * display.DpiScale)));
                if (x != r.X || y != r.Y) DesktopWindows.MovePhysical(this, x, y);
            }
        } finally { fittingWindow = false; }
    }
    private void ContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        QueueMinimumHeightUpdate();
    }
    private void ApplyResponsiveLayout(double clientWidth)
    {
        bool narrow = clientWidth < 760;
        if (narrowLayout == narrow) return;
        narrowLayout = narrow;
        Grid.SetRow(HeaderProfile, narrow ? 1 : 0);
        Grid.SetColumn(HeaderProfile, narrow ? 0 : 1);
        Grid.SetColumnSpan(HeaderProfile, narrow ? 3 : 1);
        HeaderProfile.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        LauncherView.ColumnDefinitions[0].Width = new GridLength(narrow ? 1 : 1.7, GridUnitType.Star);
        LauncherView.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        LauncherMascotCard.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        LauncherTranslationCard.Margin = narrow ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 13, 10);
        LauncherHelpActions.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0, 8, 13, 0);

        ProfileSettingsView.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(224);
        Grid.SetColumn(ProfileNavigation, narrow ? 1 : 0);
        Grid.SetRowSpan(ProfileNavigation, narrow ? 1 : 2);
        Grid.SetRow(ProfileContent, narrow ? 1 : 0);
        Grid.SetRowSpan(ProfileContent, narrow ? 1 : 2);
        ProfileNavigation.Padding = narrow ? new Thickness(12, 6, 12, 6) : new Thickness(14, 20, 14, 20);
        ProfileNavigationTitle.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ProfileContent.Margin = narrow ? new Thickness(12, 12, 12, 10) : new Thickness(24, 18, 24, 15);
        // Move the fixed preview frame, not its map child: placeholder and map must
        // occupy exactly the same slot in both wide and narrow layouts.
        Grid.SetRow(CaptureMapFrame, narrow ? 1 : 0); Grid.SetColumn(CaptureMapFrame, narrow ? 0 : 1);
        CaptureMapFrame.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
        var factory = new FrameworkElementFactory(narrow ? typeof(WrapPanel) : typeof(StackPanel));
        ProfilePages.ItemsPanel = new ItemsPanelTemplate(factory);
        ProfilePages.MaxHeight = narrow ? 150 : double.PositiveInfinity;
        foreach (var item in ProfilePages.Items.OfType<ListBoxItem>()) item.Padding = narrow ? new Thickness(9, 7, 9, 7) : new Thickness(13, 12, 13, 12);
    }
}
