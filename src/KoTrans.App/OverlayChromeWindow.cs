using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using KoTrans.Infrastructure;
namespace KoTrans.App;

// A narrow native input region above the transparent text window. The rest is a real
// hole, so another process receives body clicks even though WPF renders visible text below.
internal sealed class OverlayChromeWindow : Window
{
    private readonly TranslationWindow overlay;
    private readonly Grid root = new();
    private readonly StackPanel commands = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 9, 0) };
    private readonly Thumb move;
    private readonly Button service = new() { Name = "OverlayService", FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(153, 193, 162)),
        Background = new SolidColorBrush(Color.FromRgb(35, 57, 48)), BorderBrush = new SolidColorBrush(Color.FromRgb(78, 112, 94)),
        BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 10, 0),
        MinWidth = 64, MaxWidth = 160, Height = 26, Margin = new Thickness(44, 7, 220, 0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Cursor = Cursors.Hand, Focusable = false };
    private readonly Button displayMode = new() { Name = "OverlayDisplayMode", FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(200, 164, 164)),
        Background = new SolidColorBrush(Color.FromRgb(62, 43, 47)), BorderBrush = new SolidColorBrush(Color.FromRgb(112, 78, 83)),
        BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 10, 0), Width = 88, Height = 26,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Cursor = Cursors.Hand, Focusable = false };
    private bool running, starting, changingDisplay;
    private readonly CheckBox pass = MakeSwitch("OverlayPassThrough", "클릭 통과");
    private readonly Button area = new() { Name = "OverlaySelectArea", Content = "영역", Width = 46, Height = 26,
        FontSize = 11, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(59, 79, 93)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(103, 128, 143)), BorderThickness = new Thickness(1),
        Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
    private readonly Button explain = new() { Name = "OverlayExplain", Content = "해설", ToolTip = "현재 문장 해설 보기 (Shift+F2)", Width = 46, Height = 26,
        FontSize = 11, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(59, 79, 93)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(103, 128, 143)), BorderThickness = new Thickness(1),
        Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
    private readonly Button toggle = new() { Name = "OverlayToggleTranslation", Content = "시작", Width = 46, Height = 26,
        FontSize = 11, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(59, 79, 93)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(103, 128, 143)), BorderThickness = new Thickness(1),
        Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 5, 0), Focusable = false };
    private bool applying, syncing, closing, inputFailed, compactCommands;
    private Button? closeButton;
    private readonly CheckBox dictionary = MakeSwitch("OverlayWordDictionary", "사전");
    internal bool InputAvailable => !inputFailed;
    internal bool CaptureExcluded { get; private set; }
    internal bool IsMoveHandleVisible => move.Visibility == Visibility.Visible;
    internal double ServiceGroupWidth => Math.Max(service.MinWidth, service.ActualWidth) + 8 + displayMode.Width;
    internal OverlayChromeWindow(TranslationWindow overlay)
    {
        this.overlay = overlay;
        Title = "KO Trans 2 · 자막 조작"; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); Content = root;
        move = MakeThumb("OverlayMoveHandle", Cursors.SizeAll, Brushes.SlateGray);
        move.Width = 28; move.Height = 28; move.HorizontalAlignment = HorizontalAlignment.Left; move.VerticalAlignment = VerticalAlignment.Top; move.Margin = new Thickness(9, 7, 0, 0);
        move.ToolTip = "누른 채 드래그하여 자막 이동";
        var visual = new FrameworkElementFactory(typeof(TextBlock)); visual.SetValue(TextBlock.TextProperty, "⠿"); visual.SetValue(TextBlock.ForegroundProperty, Brushes.White); visual.SetValue(TextBlock.FontSizeProperty, 22d);
        visual.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); visual.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var moveSurface = new FrameworkElementFactory(typeof(Border)); moveSurface.SetValue(Border.BackgroundProperty, Brushes.SlateGray); moveSurface.SetValue(Border.CornerRadiusProperty,new CornerRadius(4)); moveSurface.AppendChild(visual);
        move.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = moveSurface };
        move.DragDelta += (_, e) => { if (overlay.ClickThrough) overlay.MoveOverlay(e.HorizontalChange, e.VerticalChange); };
        move.DragCompleted += (_, _) => overlay.CommitPlacement(); root.Children.Add(move);
        var close = new Button { Name = "CloseOverlay", Content = "×", ToolTip = "자막 닫기 · 번역 중지", Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0), FontSize = 22, Foreground = Brushes.White, Background = Brushes.Transparent, Focusable = false };
        closeButton = close;
        close.Style = (Style)FindResource("DarkDisabledButton");
        var capsule = new FrameworkElementFactory(typeof(Border)); capsule.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        capsule.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        capsule.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        capsule.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var caption = new FrameworkElementFactory(typeof(ContentPresenter));
        caption.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        capsule.AppendChild(caption);
        var toggleTemplate = new ControlTemplate(typeof(Button)) { VisualTree = capsule };
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .5)); toggleTemplate.Triggers.Add(disabled);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, .7)); toggleTemplate.Triggers.Add(pressed);
        toggle.Template = toggleTemplate;
        area.Template = toggleTemplate;
        explain.Template = toggleTemplate;
        service.Template = toggleTemplate;
        displayMode.Template = toggleTemplate;
        var serviceStyle = new Style(typeof(Button));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, .85)); serviceStyle.Triggers.Add(hover);
        service.Style = serviceStyle;
        displayMode.Style = serviceStyle;
        service.Click += (_, _) => overlay.RequestShowMain();
        service.SizeChanged += (_, _) => { overlay.UpdateHeaderLayout(); PlaceDisplayMode(); UpdateInputRegion(); };
        root.Children.Add(service);
        displayMode.Click += (_, _) => overlay.RequestDisplayModeChange();
        root.Children.Add(displayMode);
        toggle.Click += (_, _) => overlay.RequestTranslationToggle();
        area.Click += (_, _) => overlay.RequestCaptureArea();
        explain.Click += (_, _) => overlay.RequestExplanation();
        close.Click += (_, _) => overlay.Close(); commands.Children.Add(pass); commands.Children.Add(dictionary); commands.Children.Add(area); commands.Children.Add(explain); commands.Children.Add(toggle); commands.Children.Add(close); root.Children.Add(commands);
        dictionary.Checked += (_, _) => { if (!applying) overlay.ChangeWordDictionary(true); };
        dictionary.Unchecked += (_, _) => { if (!applying) overlay.ChangeWordDictionary(false); };
        pass.Checked += (_, _) => { if (!applying) overlay.ChangeClickThrough(true); };
        pass.Unchecked += (_, _) => { if (!applying) overlay.ChangeClickThrough(false); };
        // Invisible edge strips: no resize-grip squares or separate edit mode.
        foreach (var mode in new[] { RegionEdit.Left, RegionEdit.Right, RegionEdit.Top, RegionEdit.Bottom,
            RegionEdit.Left | RegionEdit.Top, RegionEdit.Right | RegionEdit.Top, RegionEdit.Left | RegionEdit.Bottom, RegionEdit.Right | RegionEdit.Bottom }) {
            var thumb = MakeThumb("Resize" + mode.ToString().Replace(", ", ""), RegionSelector.CursorFor(mode), Brushes.Transparent);
            bool x = mode.HasFlag(RegionEdit.Left) || mode.HasFlag(RegionEdit.Right), y = mode.HasFlag(RegionEdit.Top) || mode.HasFlag(RegionEdit.Bottom);
            thumb.HorizontalAlignment = mode.HasFlag(RegionEdit.Left) ? HorizontalAlignment.Left : mode.HasFlag(RegionEdit.Right) ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            thumb.VerticalAlignment = mode.HasFlag(RegionEdit.Top) ? VerticalAlignment.Top : mode.HasFlag(RegionEdit.Bottom) ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            if (x) thumb.Width = y ? 10 : 6; if (y) thumb.Height = x ? 10 : 6;
            thumb.DragDelta += (_, e) => overlay.ResizeOverlay(mode, e.HorizontalChange, e.VerticalChange);
            thumb.DragCompleted += (_, _) => overlay.CommitPlacement(); root.Children.Add(thumb);
        }
        SourceInitialized += (_, _) => {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(hwnd, -20, new IntPtr(GetWindowLongPtr(hwnd, -20).ToInt64() | 0x08000000));
            HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) => {
                if (m == 0x21) { handled = true; return new IntPtr(3); } return IntPtr.Zero;
            });
            CaptureExcluded = CaptureExclusionPolicy.TryExclude(hwnd, subtitleWindow: true); UpdateInputRegion();
        };
        Loaded += (_, _) => { if (inputFailed) { Hide(); return; } SyncBounds(); UpdateInputRegion(); };
        SizeChanged += (_, _) => UpdateInputRegion(); DpiChanged += (_, _) => { SyncBounds(); UpdateInputRegion(); };
        commands.SizeChanged += (_, _) => { overlay.ReserveCommandSpace(commands.ActualWidth); UpdateInputRegion(); };
        Closing += (_, _) => { closing = true; };
    }
    private static Thumb MakeThumb(string name, Cursor cursor, Brush brush)
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, brush);
        return new Thumb { Name = name, Cursor = cursor, Focusable = false, Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border } };
    }
    private static CheckBox MakeSwitch(string name, string label)
    {
        var knob = new FrameworkElementFactory(typeof(Border));
        knob.Name = "Knob"; knob.SetValue(FrameworkElement.WidthProperty, 12d); knob.SetValue(FrameworkElement.HeightProperty, 12d);
        knob.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); knob.SetValue(Border.BackgroundProperty, Brushes.White);
        knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        knob.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "Track"; track.SetValue(FrameworkElement.WidthProperty, 32d); track.SetValue(FrameworkElement.HeightProperty, 18d);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        track.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(72, 83, 97)));
        track.AppendChild(knob);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, label); text.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 0, 0));
        var content = new FrameworkElementFactory(typeof(StackPanel));
        content.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        content.AppendChild(track); content.AppendChild(text);
        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = content };
        var on = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(46, 139, 239)), "Track"));
        on.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "Knob"));
        template.Triggers.Add(on);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .45)); template.Triggers.Add(disabled);
        return new CheckBox { Name = name, Content = label, Template = template, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0),
            Cursor = Cursors.Hand, Focusable = false };
    }
    internal void SetAutomaticTranslation(bool running, bool starting)
    {
        this.running = running; this.starting = starting;
        toggle.Content = running ? "중단" : "시작";
        RefreshActionToolTips();
        toggle.IsEnabled = !starting;
        displayMode.IsEnabled = !starting && !changingDisplay;
    }
    private void RefreshActionToolTips()
    {
        string action = overlay.TranslationEnabled ? "번역" : "원문 인식";
        toggle.ToolTip = action + (running ? " 중지" : " 시작");
        if (closeButton is not null) closeButton.ToolTip = $"자막 닫기 · {action} 중지";
    }
    internal void SetAreaAvailable(bool available) => area.IsEnabled = available;
    internal void SetExplanationAvailable(bool available)
    {
        explain.IsEnabled = available;
        explain.ToolTip = available ? "현재 문장 해설 보기 (Shift+F2)" : "현재 번역 서비스는 문장 해설을 지원하지 않습니다.";
    }
    internal void SetDisplayMode(bool original, bool translated, bool changing)
    {
        changingDisplay = changing;
        displayMode.Content = original && translated ? "원문+번역" : original ? "원문" : "번역";
        System.Windows.Automation.AutomationProperties.SetName(displayMode, $"{displayMode.Content} · 자막 표시 전환");
        displayMode.IsEnabled = !starting && !changingDisplay;
        RefreshActionToolTips();
    }
    private void PlaceDisplayMode()
    {
        double width = service.ActualWidth > 0 ? service.ActualWidth : service.MinWidth;
        displayMode.Margin = new Thickness(service.Margin.Left + width + 8, service.Margin.Top, 0, 0);
    }
    internal void SetService(string label, bool local = false)
    {
        service.MinWidth = local ? 112 : 64;
        service.Content = label;
        System.Windows.Automation.AutomationProperties.SetName(service, label + " · 메인창 열기");
    }
    internal void SetServicePlacement(bool clickThrough, bool stacked, double commandWidth)
    {
        service.Margin = new Thickness(clickThrough ? 44 : 20, stacked ? 38 : 7, stacked ? 20 : commandWidth + 18, 0);
        PlaceDisplayMode();
        UpdateInputRegion();
    }
    internal void SetCompactCommands(bool compact)
    {
        if (compactCommands == compact) return;
        compactCommands = compact;
        commands.Margin = compact ? new Thickness(0, 7, 2, 0) : new Thickness(0, 7, 9, 0);
        pass.Margin = dictionary.Margin = compact ? new Thickness(1, 0, 0, 0) : new Thickness(4, 0, 4, 0);
        area.Margin = explain.Margin = compact ? new Thickness(1, 0, 0, 0) : new Thickness(4, 0, 0, 0);
        toggle.Margin = compact ? new Thickness(1, 0, 2, 0) : new Thickness(4, 0, 5, 0);
    }
    internal void Apply(bool clickThrough)
    {
        applying = true; pass.IsChecked = clickThrough; applying = false;
        move.Visibility = clickThrough ? Visibility.Visible : Visibility.Collapsed;
        UpdateInputRegion();
    }
    internal void ApplyDictionary(bool value, bool enabled)
    {
        applying = true;
        dictionary.IsChecked = value; dictionary.IsEnabled = enabled;
        applying = false;
    }
    internal void SyncBounds()
    {
        if (syncing || closing) return; syncing = true;
        try {
            Width = overlay.Width; Height = overlay.Height;
            var hwnd = new WindowInteropHelper(this).Handle;
            var owner = new WindowInteropHelper(overlay).Handle;
            if (hwnd != IntPtr.Zero && owner != IntPtr.Zero && WindowTargets.Bounds(owner) is { } bounds)
                DesktopWindows.MovePhysical(this, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            else { if (double.IsFinite(overlay.Left)) Left = overlay.Left; if (double.IsFinite(overlay.Top)) Top = overlay.Top; }
        } finally { syncing = false; }
    }
    // Also used by Background tests: body holes, edge reachability, control bounds, DPI conversion.
    internal Rect[] InputRegions(double width, double height)
    {
        double commandWidth = commands.ActualWidth > 0 ? commands.ActualWidth : 190;
        var regions = new System.Collections.Generic.List<Rect> {
            new(0,0,width,6), new(0,height-6,width,6), new(0,0,6,height), new(width-6,0,6,height),
            new(0,0,10,10), new(width-10,0,10,10), new(0,height-10,10,10), new(width-10,height-10,10,10),
            new(Math.Max(0,width-9-commandWidth),7,Math.Min(width,commandWidth),28)
        };
        if (IsMoveHandleVisible) regions.Add(new(9,7,28,28));
        // The label lives in this native input window, not the click-through text surface.
        double serviceWidth = service.ActualWidth > 0 ? service.ActualWidth : service.MinWidth;
        serviceWidth = Math.Max(0, Math.Min(serviceWidth, width - service.Margin.Left - service.Margin.Right));
        if (serviceWidth > 0) regions.Add(new(service.Margin.Left, service.Margin.Top, serviceWidth, service.Height));
        regions.Add(new(displayMode.Margin.Left, displayMode.Margin.Top, displayMode.Width, displayMode.Height));
        return regions.ToArray();
    }
    private void UpdateInputRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || closing || inputFailed) return;
        double width = ActualWidth > 0 ? ActualWidth : Width, height = ActualHeight > 0 ? ActualHeight : Height;
        if (width < 10 || height < 10) return;
        var dpi = VisualTreeHelper.GetDpi(this); IntPtr combined = CreateRectRgn(0,0,0,0);
        try {
            if (combined == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            foreach (var r in InputRegions(width,height)) {
                var part = CreateRectRgn((int)Math.Floor(r.Left*dpi.DpiScaleX),(int)Math.Floor(r.Top*dpi.DpiScaleY),
                    (int)Math.Ceiling(r.Right*dpi.DpiScaleX),(int)Math.Ceiling(r.Bottom*dpi.DpiScaleY));
                if (part == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try { if (CombineRgn(combined,combined,part,2)==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
                finally { DeleteObject(part); }
            }
            if (SetWindowRgn(hwnd,combined,true)==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            combined=IntPtr.Zero; // Windows now owns this region.
        } catch (System.ComponentModel.Win32Exception ex) {
            // Never leave a failed full-size input surface covering the game.
            inputFailed = true; Hide(); overlay.ReportInputFailure(ex.NativeErrorCode);
        } finally { if(combined!=IntPtr.Zero)DeleteObject(combined); }
    }
    [DllImport("gdi32.dll",SetLastError=true)]private static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern int CombineRgn(IntPtr destination,IntPtr a,IntPtr b,int mode);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll",SetLastError=true)]private static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
}
