using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rectangle = System.Windows.Shapes.Rectangle;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class RegionEditorTests
{
    internal static void GeometryAndControls()
    {
        var screen=new PixelRect(0,0,1024,768);var model=new RegionSelectionState();int completed=0;PixelRect? accepted=null;
        var window=Track(new RegionSelector(screen,model,r=>{completed++;accepted=r;}));
        try {
            Check(!Button(window,"확정").IsEnabled && model.Area is null,"New area started configured");
            Check(Elements<Rectangle>(window).Count()==1,"Region editor still displays small resize-grip squares");
            window.BeginEdit(new(600,500));model.Move(new(200,150));
            Check(!Button(window,"확정").IsEnabled,"Confirm enabled while dragging");model.End();Layout(window,1024,768);
            Check(model.Area==new PixelRect(200,150,400,350) && completed==0 && Button(window,"확정").IsEnabled,"Pointer release auto-confirmed or lost new draft");
            var toolbar=Named<Border>(window,"SelectionToolbar");
            var header=Named<Grid>(window,"SelectionHeader");var footer=Named<Grid>(window,"SelectionFooter");
            var size=Named<TextBlock>(window,"SelectionSize");var position=Named<TextBlock>(window,"SelectionPosition");
            Check(size.Text=="400 × 350 px" && position.Text=="X 200 · Y 150"
                && Named<TextBlock>(window,"SelectionStatus").Visibility==Visibility.Collapsed,
                "Valid selection did not show separate physical-pixel size/position or kept the warning visible");
            Check(toolbar.ActualHeight<115 && toolbar.ActualWidth<=600 && header.ActualHeight>0 && footer.ActualHeight>0
                && footer.TranslatePoint(new Point(),toolbar).Y>=header.TranslatePoint(new Point(),toolbar).Y+header.ActualHeight,
                "Normal desktop region toolbar is not a compact two-row layout");
            var hint=Elements<TextBlock>(window).Single(t=>t.Text=="드래그로 이동 · 가장자리 드래그로 크기 조절");
            var fullHint=new TextBlock {Text=hint.Text,FontSize=hint.FontSize,FontFamily=hint.FontFamily};
            fullHint.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            Check(hint.ActualWidth>=fullHint.DesiredSize.Width-1
                && Grid.GetRow(footer.Children.OfType<WrapPanel>().Single())==0,
                "Compact toolbar clipped the instruction or wrapped its buttons on a normal desktop");
            var buttonBounds=new[]{Button(window,"확정"),Button(window,"새로 그리기"),Button(window,"취소 (Esc)")}
                .Select(b=>b.TranslatePoint(new Point(),footer)).ToArray();
            Check(buttonBounds[0].Y==buttonBounds[1].Y && buttonBounds[1].Y==buttonBounds[2].Y
                && Button(window,"확정").ActualHeight==36 && Button(window,"새로 그리기").ActualHeight==36
                && Button(window,"취소 (Esc)").ActualHeight==36,
                "Toolbar buttons lost their single-row alignment or matching heights");
            window.BeginEdit(new(400,300));model.Move(new(450,340));model.End();
            Check(model.Area==new PixelRect(250,190,400,350) && completed==0,"Draft interior did not move independently of confirmation");
            Check(size.Text=="400 × 350 px" && position.Text=="X 250 · Y 190","Dragging did not update physical-pixel coordinates");
            Click(Button(window,"확정"));Check(completed==1 && accepted==model.Area,"Confirm returned wrong draft");
            Click(Button(window,"새로 그리기"));Check(model.Area is null && !Button(window,"확정").IsEnabled,"Redraw did not clear draft");
            window.BeginEdit(new(700,500));model.Move(new(701,501));model.End();
            Check(model.Area is null && completed==1,"Tiny drag became a confirmed area");
            Click(Button(window,"취소 (Esc)"));Check(completed==2 && accepted is null,"Cancel did not return cancellation");
        } finally {Close(window);}
        var original=new PixelRect(100,100,300,200);
        (Point Point,Point Delta,RegionEdit Mode,Cursor Cursor)[] edits=[
            (new(100,200),new(-30,0),RegionEdit.Left,Cursors.SizeWE),(new(400,200),new(30,0),RegionEdit.Right,Cursors.SizeWE),
            (new(250,100),new(0,-30),RegionEdit.Top,Cursors.SizeNS),(new(250,300),new(0,30),RegionEdit.Bottom,Cursors.SizeNS),
            (new(100,100),new(-30,-30),RegionEdit.Left|RegionEdit.Top,Cursors.SizeNWSE),(new(400,100),new(30,-30),RegionEdit.Right|RegionEdit.Top,Cursors.SizeNESW),
            (new(400,300),new(30,30),RegionEdit.Right|RegionEdit.Bottom,Cursors.SizeNWSE),(new(100,300),new(-30,30),RegionEdit.Left|RegionEdit.Bottom,Cursors.SizeNESW)
        ];
        foreach(var edit in edits) {
            var draft=new RegionSelectionState(original);draft.Begin(edit.Point,screen,8,8);
            Check(draft.Operation==edit.Mode && RegionSelector.CursorFor(draft.Operation)==edit.Cursor,"Resize hit/cursor wrong: "+edit.Mode);
            draft.Move(new(edit.Point.X+edit.Delta.X,edit.Point.Y+edit.Delta.Y));draft.End();
            int l=original.X+(edit.Mode.HasFlag(RegionEdit.Left)?-30:0),t=original.Y+(edit.Mode.HasFlag(RegionEdit.Top)?-30:0);
            Check(draft.Area==new PixelRect(l,t,original.Width+(edit.Delta.X!=0?30:0),original.Height+(edit.Delta.Y!=0?30:0)),"Resize expansion wrong: "+edit.Mode);
            draft=new(original);draft.Begin(edit.Point,screen,8,8);draft.Move(new(edit.Point.X-edit.Delta.X*2,edit.Point.Y-edit.Delta.Y*2));draft.End();
            Check(draft.Area is {} shrunk && shrunk.Width==300-(edit.Delta.X!=0?60:0) && shrunk.Height==200-(edit.Delta.Y!=0?60:0),"Resize shrink wrong: "+edit.Mode);
            draft=new(original);draft.Begin(edit.Point,screen,8,8);draft.Move(new(edit.Point.X-edit.Delta.X*100,edit.Point.Y-edit.Delta.Y*100));draft.End();
            Check(draft.Area is {IsValid:true,Width:>=4,Height:>=4} minimum && RegionSelectionState.Contains(screen,minimum),"Resize crossed opposite edge/minimum: "+edit.Mode);
        }
        var tiny=new RegionSelectionState(new(100,100,4,4));
        Check(tiny.HitTest(new(102,102),20,20)==RegionEdit.Move,"Minimum rectangle lost its movable interior");
        Check(tiny.HitTest(new(100,102),20,20)==RegionEdit.Left,"Minimum rectangle lost edge resizing");
        var negative=new PixelRect(-1920,0,1920,1080);var moved=new RegionSelectionState(new(-1700,200,400,200));
        moved.Begin(new(-1500,300),negative,8,8);moved.Move(new(-3000,-100));moved.End();Check(moved.Area==new PixelRect(-1920,0,400,200),"Move escaped negative monitor edge");
        moved.Begin(new(-1700,100),negative,8,8);moved.Move(new(5000,5000));moved.End();Check(moved.Area is {} bounded && RegionSelectionState.Contains(negative,bounded),"Move escaped right/bottom screen edge");
        var aborted=new RegionSelectionState(original);aborted.Begin(new(800,600),screen,8,8);aborted.Move(new(801,601));aborted.End();Check(aborted.Area==original,"Accidental tiny new drag discarded prior draft");
        aborted.Begin(new(250,200),screen,8,8);aborted.Move(new(300,300));aborted.CancelGesture();Check(aborted.Area==original,"Failed mouse capture did not restore draft");
    }
    internal static void DpiAndMonitors()
    {
        var portrait=new PixelRect(-1600,0,1600,2560);var primary=new PixelRect(0,0,1920,1080);
        var draft=new RegionSelectionState(new(-1500,400,500,300));int confirmed=0;
        var left=Track(new RegionSelector(portrait,draft,_=>confirmed++));var right=Track(new RegionSelector(primary,draft,_=>confirmed++));
        try {
            left.UpdateVisual(new(2.5,2.5));var rectangle=Named<Rectangle>(left,"SelectionOutline");
            Check(Canvas.GetLeft(rectangle)==40 && Canvas.GetTop(rectangle)==160 && rectangle.Width==200 && rectangle.Height==120,"Physical pixels mixed with 250% DIP display coordinates");
            Check(Named<TextBlock>(left,"SelectionSize").Text=="500 × 300 px"
                && Named<TextBlock>(left,"SelectionPosition").Text=="X -1500 · Y 400",
                "Toolbar displayed scaled DIPs instead of negative physical-pixel coordinates");
            left.UpdateVisual(new(3,3));
            Check(Grid.GetRow(Named<StackPanel>(left,"SelectionInfo"))==1,
                "Narrow high-DPI screen did not move area details below the title");
            foreach(double scale in new[]{1d,1.25d,1.5d}) {
                right.UpdateVisual(new(scale,scale));Layout(right,primary.Width/scale,primary.Height/scale);
                Check(Named<Border>(right,"SelectionToolbar").ActualWidth<=primary.Width/scale-24+1,
                    "Toolbar escaped the monitor work area at DPI " + scale);
            }
            Check(Button(left,"확정").IsEnabled && !Button(right,"확정").IsEnabled,"Confirm enabled on wrong monitor");
            Check(draft.HitTest(new(-1515,550),20,20)==RegionEdit.Left,"High DPI edge hit target too small");
            var seam=new RegionSelectionState(new(-400,100,400,200));seam.Begin(new(2,200),primary,8,8);
            Check(seam.Operation==RegionEdit.Create,"Adjacent monitor edge grabbed a rectangle on another monitor");
            right.BeginEdit(new(100,200));draft.Move(new(600,500));draft.End();
            Check(draft.Area==new PixelRect(100,200,500,300) && !Button(left,"확정").IsEnabled && Button(right,"확정").IsEnabled,"Monitor views diverged when drawing new area");
            Click(Button(right,"확정"));Check(confirmed==1,"Multiple monitor views confirmed twice");
        } finally {Close(left);Close(right);}
        var crossing=Track(new RegionSelector(primary,new RegionSelectionState(new(-100,100,200,200)),_=>{}));
        try {
            Check(!Button(crossing,"확정").IsEnabled
                && Named<TextBlock>(crossing,"SelectionStatus").Visibility==Visibility.Visible
                && Named<TextBlock>(crossing,"SelectionStatus").Text.Contains("모니터 경계"),
                "Cross-monitor draft did not block confirmation or explain the boundary error");
        } finally {Close(crossing);}
    }
    internal static void SaveAndRestore()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.RegionTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var settings=new AppSettings();settings.Validate();var profile=settings.DefaultProfile;var store=new SettingsStore(dir);
        profile.CaptureMode = KoTrans.Core.CaptureMode.Region;
        var target=new WindowTarget(new IntPtr(20),20,20,"Synthetic game","game");PixelRect gameBounds=new(100,150,900,600);bool unavailable=false;
        var window=Track(new MainWindow(store,new ApiKeyStore(dir),()=>[target],_=>unavailable?throw new CaptureUnavailableException("Synthetic unavailable game"):gameBounds));window.ApplyLoadedSettings(settings);
        try {
            Check(window.InitialCaptureArea() is null && !profile.CaptureAreaConfigured,"New app placeholder treated as confirmed area");
            var cancel=window.ConfirmCaptureAreaAsync(null,profile.Id);Complete(cancel);
            Check(!cancel.Result && !profile.CaptureAreaConfigured && !File.Exists(Path.Combine(dir,"settings.json")),"Cancel wrote new settings");
            var selected=new PixelRect(140,220,300,180);var save=window.ConfirmCaptureAreaAsync(selected,profile.Id);Complete(save);Check(save.Result,"Confirmed area save failed");
            var loaded=store.LoadAsync();Complete(loaded);Check(loaded.Result.DefaultProfile.CaptureAreaConfigured && loaded.Result.DefaultProfile.CaptureArea==selected,"Confirmed rectangle did not survive disk roundtrip");
            Check(window.InitialCaptureArea()==selected,"Existing screen rectangle did not reopen as draft");
            var draft=new RegionSelectionState(selected);draft.Begin(new(250,300),new(0,0,1920,1080),8,8);draft.Move(new(350,350));draft.End();
            Check(profile.CaptureArea==selected,"Draft movement changed persisted profile before confirmation");
            byte[] before=File.ReadAllBytes(Path.Combine(dir,"settings.json"));Complete(window.ConfirmCaptureAreaAsync(null,profile.Id));
            Check(File.ReadAllBytes(Path.Combine(dir,"settings.json")).SequenceEqual(before) && profile.CaptureArea==selected,"Cancel edited an existing area");
            window.SelectGameWindow(target);Check(profile.WindowCaptureRegion==new PixelRect(40,70,300,180),"Window client conversion changed existing area");
            gameBounds=new(500,350,900,600);Check(window.InitialCaptureArea()==new PixelRect(540,420,300,180),"Saved window-relative draft did not follow moved game");
            var screenArea=new PixelRect(580,460,320,190);Complete(window.ConfirmCaptureAreaAsync(screenArea,profile.Id));
            Check(profile.WindowCaptureRegion==new PixelRect(80,110,320,190) && profile.CaptureArea==screenArea,"Confirmed window area lost relative coordinates");
            gameBounds=new(500,350,100,80);Check(window.InitialCaptureArea()==screenArea,"Resized game prevented opening editor to repair old crop");
            gameBounds=new(500,350,900,600);unavailable=true;Check(window.InitialCaptureArea()==screenArea,"Unavailable game lost last editable screen position");unavailable=false;
            before=File.ReadAllBytes(Path.Combine(dir,"settings.json"));bool rejected=false;
            try {Complete(window.ConfirmCaptureAreaAsync(new(0,0,200,200),profile.Id));}catch(CaptureUnavailableException){rejected=true;}
            Check(rejected && File.ReadAllBytes(Path.Combine(dir,"settings.json")).SequenceEqual(before) && profile.CaptureArea==screenArea,"Invalid game-relative confirmation erased old area");
            var other=settings.CreateGameProfile("Other");Complete(window.SwitchProfileAsync(other));before=File.ReadAllBytes(Path.Combine(dir,"settings.json"));
            var stale=window.ConfirmCaptureAreaAsync(new(10,10,300,200),profile.Id);Complete(stale);
            Check(!stale.Result && !other.CaptureAreaConfigured && other.CaptureArea==new PixelRect(0,0,800,300)
                && other.WindowCaptureRegion is null && profile.CaptureArea==screenArea
                && File.ReadAllBytes(Path.Combine(dir,"settings.json")).SequenceEqual(before),"Old edit session changed another profile");
        } finally {Close(window);Directory.Delete(dir,true);}
    }
    internal static void Scaling()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.ScaledRegion",Guid.NewGuid().ToString("N"));
        var settings=new AppSettings();settings.Validate();var p=settings.DefaultProfile;
        var target=new WindowTarget(new IntPtr(30),30,30,"Synthetic game","game");var bounds=new PixelRect(100,150,900,600);
        WindowScaleTransform? scale=null;int reads=0;
        var store=new SettingsStore(root);var window=Track(new MainWindow(store,new ApiKeyStore(root),()=>[target],_=>bounds,
            getWindowScale:_=>{reads++;return scale;}));window.ApplyLoadedSettings(settings);
        try {
            window.SelectGameWindow(target);
            Complete(window.ConfirmCaptureAreaAsync(new(140,220,300,180),p.Id));
            var original=p.WindowCaptureRegion;
            scale=new(WindowScaleProvider.Magpie,target.Handle,new IntPtr(31),bounds,new(400,30,1800,1200));
            Check(window.InitialCaptureArea()==new PixelRect(480,170,600,360) && p.WindowCaptureRegion==original,"Saved unscaled selection did not follow active scaler");
            var edit=window.CaptureAreaEditContext();
            Complete(window.ConfirmCaptureAreaAsync(new(600,270,640,380),p.Id,edit));
            Check(p.WindowCaptureRegion==new PixelRect(100,120,320,190) && p.CaptureArea==new PixelRect(200,270,320,190),"Selection made after scaling was stored in output coordinates");
            var load=store.LoadAsync();Complete(load);
            Check(load.Result.DefaultProfile.WindowCaptureRegion==p.WindowCaptureRegion,"Original-relative scaled selection did not persist");
            scale=scale with {Destination=new(-1500,100,1350,900)};
            var saved=p.WindowCaptureRegion;
            for(int i=0;i<5;i++) {
                edit=window.CaptureAreaEditContext();var initial=window.InitialCaptureArea(edit);
                Complete(window.ConfirmCaptureAreaAsync(initial,p.Id,edit));
            }
            Check(p.WindowCaptureRegion==saved,"Fractional scale reconfirmation expanded the saved area");
            edit=window.CaptureAreaEditContext();var draft=window.InitialCaptureArea(edit);
            byte[] before=File.ReadAllBytes(Path.Combine(root,"settings.json"));
            scale=scale with {Destination=scale.Destination with {X=-1400}};
            bool rejected=false;try {Complete(window.ConfirmCaptureAreaAsync(draft,p.Id,edit));}catch(CaptureUnavailableException) {rejected=true;}
            Check(rejected && p.WindowCaptureRegion==saved && File.ReadAllBytes(Path.Combine(root,"settings.json")).SequenceEqual(before),"Changed scaler edit silently saved stale coordinates");
            scale=null;
            Check(window.InitialCaptureArea()==WindowCaptureBounds.Resolve(bounds,saved),"Stopping scaler left the region in expanded coordinates");
            scale=new(WindowScaleProvider.Magpie,new IntPtr(999),new IntPtr(31),bounds,new(400,30,1800,1200));
            Check(window.InitialCaptureArea()==WindowCaptureBounds.Resolve(bounds,saved),"Another scaled game changed selection geometry");
            p.CaptureMode=KoTrans.Core.CaptureMode.Region;int oldReads=reads;
            Complete(window.ConfirmCaptureAreaAsync(new(0,0,500,200),p.Id));
            Check(reads==oldReads && p.CaptureArea==new PixelRect(0,0,500,200) && p.WindowCaptureRegion is null,"Fixed screen-region mode acquired automatic scaler mapping");
        } finally {Close(window);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
