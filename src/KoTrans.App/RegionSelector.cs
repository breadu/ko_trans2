using System.Collections.Generic;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace KoTrans.App;

internal sealed class RegionSelector : Window
{
    private readonly Canvas canvas = new() { Background=new SolidColorBrush(Color.FromArgb(90,0,0,0)), ClipToBounds=true };
    private readonly Rectangle selection = new() { Name="SelectionOutline",Stroke=Brushes.Aqua,StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(25,0,200,255)),IsHitTestVisible=false };
    private readonly Border toolbar;
    private readonly StackPanel toolbarHost=new();
    private readonly Grid header=new() {Name="SelectionHeader"};
    private readonly Grid footer=new() {Name="SelectionFooter"};
    private readonly StackPanel info=new() {Name="SelectionInfo",Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
    private readonly WrapPanel actions=new() {HorizontalAlignment=HorizontalAlignment.Right};
    private readonly TextBlock hint=new() {Text="드래그로 이동 · 가장자리 드래그로 크기 조절",Foreground=new SolidColorBrush(Color.FromRgb(166,189,210)),FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock dimensions=new() {Name="SelectionSize",Foreground=new SolidColorBrush(Color.FromRgb(194,211,227)),FontSize=13};
    private readonly TextBlock coordinates=new() {Name="SelectionPosition",Foreground=new SolidColorBrush(Color.FromRgb(194,211,227)),FontSize=13};
    private readonly TextBlock status=new() {Name="SelectionStatus",TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(255,179,152)),FontSize=12,Visibility=Visibility.Collapsed,Margin=new(4,5,0,0)};
    private readonly Button confirm=new() {Name="ConfirmRegion",Content="확정",IsEnabled=false},cancel=new() {Content="취소 (Esc)"},redraw=new() {Content="새로 그리기"};
    private readonly RegionSelectionState draft;
    private readonly PixelRect screen;
    private readonly Action<PixelRect?> completed;
    internal RegionSelectionState Draft=>draft;
    internal RegionSelector(PixelRect screen, RegionSelectionState draft, Action<PixelRect?> completed)
    {
        this.screen=screen;this.draft=draft;this.completed=completed;
        Title="KO Trans 2 · 영역 편집";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.Manual;
        Cursor=Cursors.Cross;Content=canvas;
        confirm.Style=ToolbarButtonStyle(true);redraw.Style=ToolbarButtonStyle(false);cancel.Style=ToolbarButtonStyle(false);
        confirm.MinWidth=62;redraw.MinWidth=86;cancel.MinWidth=90;
        foreach(var button in new[]{confirm,redraw,cancel}) {button.Height=36;button.Margin=new(8,0,0,0);button.Cursor=Cursors.Hand;}
        canvas.Children.Add(selection);
        header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        header.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        header.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});header.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        var title=new TextBlock {Text="▣  영역 편집",FontSize=16,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,17,0)};
        header.Children.Add(title);
        info.Children.Add(new Border {Width=1,Height=16,Background=new SolidColorBrush(Color.FromRgb(65,84,105)),Margin=new(0,0,14,0)});
        info.Children.Add(dimensions);
        info.Children.Add(new Border {Width=1,Height=16,Background=new SolidColorBrush(Color.FromRgb(65,84,105)),Margin=new(14,0,14,0)});
        info.Children.Add(coordinates);Grid.SetColumn(info,1);header.Children.Add(info);
        var badge=new Border {Background=new SolidColorBrush(Color.FromRgb(29,53,77)),CornerRadius=new CornerRadius(10),Padding=new(8,4,8,4),Margin=new(12,0,0,0)};
        var badgeContent=new StackPanel {Orientation=Orientation.Horizontal};
        badgeContent.Children.Add(new TextBlock {Text="●",Foreground=new SolidColorBrush(Color.FromRgb(89,170,233)),FontSize=10,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,5,0)});
        badgeContent.Children.Add(new TextBlock {Text="편집 중",Foreground=new SolidColorBrush(Color.FromRgb(176,204,226)),FontSize=11,VerticalAlignment=VerticalAlignment.Center});
        badge.Child=badgeContent;Grid.SetColumn(badge,2);header.Children.Add(badge);
        footer.Margin=new(0,10,0,0);footer.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
        footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        footer.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});footer.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        footer.Children.Add(hint);
        actions.Children.Add(confirm);actions.Children.Add(redraw);actions.Children.Add(cancel);Grid.SetColumn(actions,1);footer.Children.Add(actions);
        var content=new StackPanel();content.Children.Add(header);content.Children.Add(footer);
        toolbar=new Border {Name="SelectionToolbar",Child=content,Background=new SolidColorBrush(Color.FromArgb(235,24,34,46)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(61,84,106)),BorderThickness=new(1),CornerRadius=new CornerRadius(13),Padding=new Thickness(14,11,14,11),
            Effect=new System.Windows.Media.Effects.DropShadowEffect {BlurRadius=10,ShadowDepth=2,Opacity=.22,Color=Colors.Black}};
        toolbarHost.Children.Add(toolbar);toolbarHost.Children.Add(status);
        canvas.Children.Add(toolbarHost);Canvas.SetLeft(toolbarHost,12);Canvas.SetTop(toolbarHost,12);
        confirm.Click+=(_,_)=> {if(draft.CanConfirm(screen))completed(draft.Area);};
        cancel.Click+=(_,_)=>completed(null);redraw.Click+=(_,_)=>{draft.Clear();if(canvas.IsMouseCaptured)canvas.ReleaseMouseCapture();};
        KeyDown+=(_,e)=> {if(e.Key==Key.Escape){e.Handled=true;completed(null);}};
        canvas.MouseLeftButtonDown+=(_,e)=> {
            if(toolbarHost.IsMouseOver || !ReadPointer(out var p))return;
            BeginEdit(p);
            if(!canvas.CaptureMouse()){draft.CancelGesture();status.Text="마우스 드래그를 시작할 수 없습니다. 다시 시도하거나 취소하세요.";}
            e.Handled=true;
        };
        canvas.MouseMove+=(_,_)=> {
            if(!draft.Dragging&&toolbarHost.IsMouseOver){Cursor=Cursors.Arrow;return;}
            if(!ReadPointer(out var p))return;
            if(draft.Dragging)draft.Move(p);
            var dpi=VisualTreeHelper.GetDpi(this);Cursor=CursorFor(draft.Dragging?draft.Operation:draft.HitTest(p,8*dpi.DpiScaleX,8*dpi.DpiScaleY));
        };
        canvas.MouseLeftButtonUp+=(_,e)=> {
            if(!draft.Dragging){if(canvas.IsMouseCaptured)canvas.ReleaseMouseCapture();return;}
            if(ReadPointer(out var p))draft.Move(p);
            draft.End();canvas.ReleaseMouseCapture();e.Handled=true;
        };
        canvas.LostMouseCapture+=(_,_)=>draft.End();
        SourceInitialized+=(_,_)=>DesktopWindows.MovePhysical(this,screen.X,screen.Y,screen.Width,screen.Height);
        Loaded+=(_,_)=>Refresh();DpiChanged+=(_,_)=>Refresh();draft.Changed+=Refresh;
        Closed+=(_,_)=>draft.Changed-=Refresh;
        Refresh();
    }
    internal void BeginEdit(Point physical, DpiScale? scale=null)
    {
        var dpi=scale??VisualTreeHelper.GetDpi(this);draft.Begin(physical,screen,8*dpi.DpiScaleX,8*dpi.DpiScaleY);
    }
    private void Refresh()=>UpdateVisual(VisualTreeHelper.GetDpi(this));
    internal void UpdateVisual(DpiScale dpi)
    {
        double width=Math.Max(1,Math.Min(600,screen.Width/dpi.DpiScaleX-24));
        toolbar.Width=width;toolbarHost.Width=width;
        bool narrow=width<580;
        Grid.SetRow(info,narrow?1:0);Grid.SetColumn(info,narrow?0:1);Grid.SetColumnSpan(info,narrow?3:1);
        info.Margin=narrow?new Thickness(0,7,0,0):new Thickness(0);
        Grid.SetRow(actions,narrow?1:0);Grid.SetColumn(actions,narrow?0:1);Grid.SetColumnSpan(actions,narrow?2:1);
        actions.Margin=narrow?new Thickness(0,7,0,0):new Thickness(0);
        actions.MaxWidth=Math.Max(1,width-30);
        confirm.IsEnabled=draft.CanConfirm(screen);
        selection.Visibility=draft.Area is {IsValid:true}?Visibility.Visible:Visibility.Collapsed;
        if(draft.Area is not {} r){dimensions.Text="영역 미지정";coordinates.Text="";status.Text="빈 곳을 드래그해 영역을 만드세요.";status.Visibility=Visibility.Visible;return;}
        double l=(r.X-screen.X)/dpi.DpiScaleX,t=(r.Y-screen.Y)/dpi.DpiScaleY,w=r.Width/dpi.DpiScaleX,h=r.Height/dpi.DpiScaleY;
        Canvas.SetLeft(selection,l);Canvas.SetTop(selection,t);selection.Width=Math.Max(0,w);selection.Height=Math.Max(0,h);
        dimensions.Text=$"{r.Width} × {r.Height} px";coordinates.Text=$"X {r.X} · Y {r.Y}";
        status.Text=r.Width<4 || r.Height<4?"영역을 4 × 4 px 이상으로 지정하세요."
            :!r.IsValid?"선택 영역이 너무 큽니다. 크기를 줄이세요."
            :!RegionSelectionState.Contains(screen,r) && r.X<screen.X+screen.Width && r.Y<screen.Y+screen.Height
                && (long)r.X+r.Width>screen.X && (long)r.Y+r.Height>screen.Y
                ?"영역이 모니터 경계를 벗어났습니다.":"";
        status.Visibility=string.IsNullOrEmpty(status.Text)?Visibility.Collapsed:Visibility.Visible;
    }
    private static Style ToolbarButtonStyle(bool primary)
    {
        var border=new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty,new Binding(nameof(Background)) {RelativeSource=RelativeSource.TemplatedParent});
        border.SetBinding(Border.BorderBrushProperty,new Binding(nameof(BorderBrush)) {RelativeSource=RelativeSource.TemplatedParent});
        border.SetBinding(Border.BorderThicknessProperty,new Binding(nameof(BorderThickness)) {RelativeSource=RelativeSource.TemplatedParent});
        border.SetBinding(Border.PaddingProperty,new Binding(nameof(Padding)) {RelativeSource=RelativeSource.TemplatedParent});
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));
        var caption=new FrameworkElementFactory(typeof(ContentPresenter));
        caption.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        caption.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        border.AppendChild(caption);
        var style=new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(Button)) {VisualTree=border}));
        style.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(primary?Color.FromRgb(50,139,208):Color.FromRgb(42,64,86))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(primary?Color.FromRgb(89,170,240):Color.FromRgb(72,99,122))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White));
        style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(13,7,13,7)));
        var hover=new Trigger {Property=IsMouseOverProperty,Value=true};
        hover.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(primary?Color.FromRgb(68,156,222):Color.FromRgb(55,81,105))));
        style.Triggers.Add(hover);
        var pressed=new Trigger {Property=Button.IsPressedProperty,Value=true};
        pressed.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(primary?Color.FromRgb(35,114,181):Color.FromRgb(34,54,74))));
        style.Triggers.Add(pressed);
        var disabled=new Trigger {Property=IsEnabledProperty,Value=false};
        disabled.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(29,42,55))));
        disabled.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(52,68,83))));
        disabled.Setters.Add(new Setter(Control.ForegroundProperty,new SolidColorBrush(Color.FromRgb(123,139,157))));
        disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty,Cursors.Arrow));
        style.Triggers.Add(disabled);
        return style;
    }
    private static bool ReadPointer(out Point p)
    {
        bool ok=DesktopWindows.GetCursorPos(out var native);p=new(native.X,native.Y);return ok;
    }
    internal static Cursor CursorFor(RegionEdit hit)=>hit switch {
        RegionEdit.Left or RegionEdit.Right=>Cursors.SizeWE,
        RegionEdit.Top or RegionEdit.Bottom=>Cursors.SizeNS,
        RegionEdit.Left|RegionEdit.Top or RegionEdit.Right|RegionEdit.Bottom=>Cursors.SizeNWSE,
        RegionEdit.Right|RegionEdit.Top or RegionEdit.Left|RegionEdit.Bottom=>Cursors.SizeNESW,
        RegionEdit.Move=>Cursors.SizeAll,_=>Cursors.Cross
    };
    public static Task<PixelRect?> PickAsync(PixelRect? initial=null)
    {
        var completion=new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows=new List<RegionSelector>();var draft=new RegionSelectionState(initial);
        void Finish(PixelRect? area) {
            if(!completion.TrySetResult(area))return;
            foreach(var window in windows)window.Close();
        }
        foreach(var screen in Forms.Screen.AllScreens) {
            var b=screen.Bounds;windows.Add(new(new(b.X,b.Y,b.Width,b.Height),draft,Finish));
        }
        if(windows.Count==0)throw new InvalidOperationException("영역을 지정할 모니터가 없습니다.");
        foreach(var window in windows)window.Closed+=(_,_)=>Finish(null);
        try {foreach(var window in windows){if(completion.Task.IsCompleted)break;window.Show();}}
        catch {Finish(null);throw;}
        return completion.Task;
    }
}
