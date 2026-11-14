using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace KoTrans.App;

// A RAM-only original-window image. Coordinates stay in source pixels regardless of
// the external scaler's capture API, crop, ratio, output display or frame generation.
internal sealed class SourceRegionSelector : Window
{
    private readonly RegionSelectionState draft;
    private readonly PixelRect bounds;
    private readonly Canvas canvas;
    private readonly Rectangle outline=new() {Stroke=Brushes.Aqua,StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(25,0,200,255)),IsHitTestVisible=false};
    private readonly TextBlock status=new() {Foreground=Brushes.White,Margin=new(8)};
    private readonly Button confirm=new() {Content="확정",Name="ConfirmSourceRegion"};
    internal RegionSelectionState Draft=>draft;
    internal SourceRegionSelector(ImageFrame frame,PixelRect? initial,Action<PixelRect?> complete)
    {
        bounds=new(0,0,frame.Width,frame.Height);draft=new(initial);
        Title="KO Trans 2 · 원본 게임 영역 지정";Width=900;Height=750;MinWidth=340;MinHeight=300;Topmost=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=new SolidColorBrush(Color.FromRgb(15,27,40));
        var root=new DockPanel {Margin=new(12)};Content=root;
        var heading=new TextBlock {Text="원본 게임 화면에서 영역 지정\n대사 영역을 드래그한 뒤 확정을 누르세요. 확대 배율과 관계없이 같은 영역을 사용합니다.",Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,8)};
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);footer.Children.Add(status);
        var actions=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(actions);
        var redraw=new Button {Content="새로 그리기"};var cancel=new Button {Content="취소 (Esc)"};
        foreach(var button in new[] {confirm,redraw,cancel}) {button.Style=(Style)FindResource("DarkDisabledButton");button.Margin=new(4);button.MinWidth=90;actions.Children.Add(button);}
        var bitmap=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgr32,null,frame.Pixels.ToArray(),frame.Stride);bitmap.Freeze();
        canvas=new Canvas {Width=frame.Width,Height=frame.Height,Background=Brushes.Black,ClipToBounds=true,Cursor=Cursors.Cross};
        canvas.Children.Add(new Image {Source=bitmap,Width=frame.Width,Height=frame.Height,IsHitTestVisible=false});canvas.Children.Add(outline);
        root.Children.Add(new Viewbox {Stretch=Stretch.Uniform,Child=canvas});
        confirm.Click+=(_,_)=>{if(draft.CanConfirm(bounds))complete(draft.Area);};cancel.Click+=(_,_)=>complete(null);redraw.Click+=(_,_)=>draft.Clear();
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;complete(null);}};
        canvas.MouseLeftButtonDown+=(_,e)=>{Begin(e.GetPosition(canvas));canvas.CaptureMouse();e.Handled=true;};
        canvas.MouseMove+=(_,e)=>{if(draft.Dragging)draft.Move(e.GetPosition(canvas));};
        canvas.MouseLeftButtonUp+=(_,e)=>{if(draft.Dragging){draft.Move(e.GetPosition(canvas));draft.End();}canvas.ReleaseMouseCapture();e.Handled=true;};
        canvas.LostMouseCapture+=(_,_)=>draft.End();draft.Changed+=Refresh;Closed+=(_,_)=>draft.Changed-=Refresh;Refresh();
    }
    internal void Begin(Point sourcePixel)=>draft.Begin(sourcePixel,bounds,8,8);
    private void Refresh()
    {
        confirm.IsEnabled=draft.CanConfirm(bounds);outline.Visibility=draft.Area is null?Visibility.Collapsed:Visibility.Visible;
        if(draft.Area is not {} area) {status.Text="게임 화면 안의 대사 영역을 선택하세요.";return;}
        Canvas.SetLeft(outline,area.X);Canvas.SetTop(outline,area.Y);outline.Width=Math.Max(0,area.Width);outline.Height=Math.Max(0,area.Height);
        status.Text=$"원본 영역 {area.Width} × {area.Height} px"+(confirm.IsEnabled?"":" · 게임 화면 안에 4 × 4 px 이상으로 지정하세요.");
    }
    internal static Task<PixelRect?> PickAsync(ImageFrame frame,PixelRect? initial)
    {
        var completion=new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);SourceRegionSelector? window=null;
        void Finish(PixelRect? area) {if(completion.TrySetResult(area))window?.Close();}
        window=new(frame,initial,Finish);window.Width=Math.Min(window.Width,Math.Max(340,SystemParameters.WorkArea.Width-40));window.Height=Math.Min(window.Height,Math.Max(300,SystemParameters.WorkArea.Height-40));
        window.Closed+=(_,_)=>completion.TrySetResult(null);window.Show();return completion.Task;
    }
}
