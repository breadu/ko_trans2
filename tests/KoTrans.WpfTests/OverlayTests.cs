using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;
namespace KoTrans.WpfTests;
internal static class OverlayTests
{
    internal static void Interaction()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.OverlayTests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var store=new SettingsStore(dir); var settings=new AppSettings(); settings.Validate();
        var a=settings.DefaultProfile; var b=settings.CreateGameProfile("B"); a.OverlayClickThrough=true;b.OverlayClickThrough=false;
        var main=Track(new MainWindow(store,new ApiKeyStore(dir)));main.ApplyLoadedSettings(settings);
        var window=Track(main.CreateOverlayWindow());Track(window.Chrome);
        try {
            Layout(window.Chrome,640,280);
            var checkbox=Named<CheckBox>(window.Chrome,"OverlayPassThrough");
            Check(checkbox.IsChecked==true && window.Chrome.IsMoveHandleVisible,"Profile click-through not applied");
            Check(!window.CanDragBody(Elements<TextBlock>(window).First()),"On-mode body intercepted game drag");
            Check(!Elements<Button>(window.Chrome).Any(c=>Equals(c.Content,"번역 시작/중지") || Equals(c.Content,"게임에 클릭 전달"))
                && !Text(window).Contains("여기를 드래그"),"Removed overlay buttons/text remain");
            var regions=window.Chrome.InputRegions(640,280);
            Check(!regions.Any(r=>r.Contains(new Point(320,140))),"Input frame blocks body click-through");
            Check(regions.Any(r=>r.Contains(new Point(20,20))) && regions.Any(r=>r.Contains(new Point(620,20))),"Handle or X has no input region");
            foreach(var point in new Point[]{new(2,140),new(638,140),new(320,2),new(320,278),new(8,8),new(632,8),new(8,272),new(632,272)})
                Check(regions.Any(r=>r.Contains(point)),"An edge/corner has no interactive region");
            var edges=Elements<Thumb>(window.Chrome).Where(t=>t.Name.StartsWith("Resize")).ToArray();
            Check(edges.Length==8 && edges.All(t=>t.Cursor is not null),"Missing invisible resize edge/cursor");
            window.Left=100;window.Top=150;window.Width=640;window.Height=280;
            foreach(var mode in new[]{RegionEdit.Left,RegionEdit.Right,RegionEdit.Top,RegionEdit.Bottom,RegionEdit.Left|RegionEdit.Top,RegionEdit.Right|RegionEdit.Top,RegionEdit.Left|RegionEdit.Bottom,RegionEdit.Right|RegionEdit.Bottom}) {
                window.Left=100;window.Top=150;window.Width=640;window.Height=280;
                var thumb=edges.Single(t=>t.Name=="Resize"+mode.ToString().Replace(", ",""));Delta(thumb,20,15);
                Check(window.Width==(mode.HasFlag(RegionEdit.Left)?620:mode.HasFlag(RegionEdit.Right)?660:640)
                    && window.Height==(mode.HasFlag(RegionEdit.Top)?265:mode.HasFlag(RegionEdit.Bottom)?295:280),"Resize edge/corner geometry wrong: "+mode);
            }
            window.Left=100;window.Top=150;window.Width=640;window.Height=280;
            var left=edges.Single(t=>t.Name=="ResizeLeft");Delta(left,30,0);
            Check(window.Left==130 && window.Width==610 && window.Top==150,"Left edge did not preserve opposite border");
            var corner=edges.Single(t=>t.Name=="ResizeRightBottom");Delta(corner,40,25);
            Check(window.Width==650 && window.Height==305 && window.PlacementEdited,"Corner resize failed without edit mode");
            Delta(left,10000,0);Check(window.Width==window.MinWidth && window.Left+window.Width==780,"Minimum width moved opposite edge");
            checkbox.IsChecked=false;
            Until(()=>File.Exists(Path.Combine(dir,"settings.json")),"Checkbox not saved");
            var load=store.LoadAsync();Complete(load);
            Check(!load.Result.DefaultProfile.OverlayClickThrough && !a.OverlayClickThrough && !window.ClickThrough,"Overlay checkbox did not persist profile value");
            Check(window.CanDragBody(Elements<TextBlock>(window).First()) && !window.CanDragBody(new ScrollBar()) && !window.CanDragBody(new Button()),"Off-mode body drag policy swallowed scroll/control input");
            Check(!window.Chrome.IsMoveHandleVisible && !window.Chrome.InputRegions(640,280).Any(r=>r.Contains(new Point(20,20))),"Off mode retained move handle");
            Check(((CheckBox)main.FindName("OverlayPassThrough")).IsChecked==false,"Settings checkbox not synchronized");
            Complete(main.SwitchProfileAsync(b));Check(((CheckBox)main.FindName("OverlayPassThrough")).IsChecked==false,"Profile B click-through lost");
            var toggle=(CheckBox)main.FindName("OverlayPassThrough");toggle.IsChecked=true;
            Complete(main.SaveCurrentProfileAsync());Complete(main.SwitchProfileAsync(a));
            Check(toggle.IsChecked==false && b.OverlayClickThrough && !a.OverlayClickThrough,"Click-through leaked across profiles");
            Complete(main.SwitchProfileAsync(b));Check(toggle.IsChecked==true,"Profile B checkbox did not restore");
            load=store.LoadAsync();Complete(load);Check(!load.Result.DefaultProfile.OverlayClickThrough && load.Result.Profiles.Single(p=>p.Id==b.Id).OverlayClickThrough,"A/B click-through disk roundtrip lost");
        } finally {Close(window.Chrome);Close(window);Close(main);Directory.Delete(dir,true);}
    }
    private static void Delta(Thumb t,double x,double y) => t.RaiseEvent(new DragDeltaEventArgs(x,y){RoutedEvent=Thumb.DragDeltaEvent});
    internal static void CloseStopsWork()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.OverlayClose",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var settings=new AppSettings();settings.Validate();var main=Track(new MainWindow(new SettingsStore(dir),new ApiKeyStore(dir)));main.ApplyLoadedSettings(settings);
        var engine=new HeldOcr();var ai=new NoAi();int captures=0;
        var pipeline=main.CreateProfilePipeline(engine,ai,_=>{captures++;return Task.FromResult(new ImageFrame(20,20,80,new byte[1600]));});
        var overlay=Track(main.CreateOverlayWindow());Track(overlay.Chrome);
        try {
            main.ObserveTranslation(pipeline,settings.ActiveProfileId);pipeline.Start(false);var work=pipeline.RequestManualAsync();
            Until(()=>engine.Started,"OCR did not start");Click(Button(overlay.Chrome,"×"));
            Check(!pipeline.IsRunning,"Closing X left OCR/translation running");
            engine.Result.TrySetResult(new([new("late source",1,new(1,1,16,8))],TimeSpan.Zero));Complete(work);Complete(pipeline.DisposeAsync().AsTask());Pump();
            Check(ai.Calls==0 && ((TextBox)main.FindName("OcrText")).Text=="" && ((TextBox)main.FindName("TranslatedText")).Text=="","Closed overlay accepted late OCR or sent AI");
            Check(!Application.Current.Windows.Cast<Window>().Contains(overlay.Chrome),"Overlay close leaked input frame");
            var reopened=Track(main.CreateOverlayWindow());Track(reopened.Chrome);
            try {Check(!pipeline.IsRunning && captures==1,"Reopening overlay restarted paid/capture work");}finally{Close(reopened);Close(reopened.Chrome);}
        } finally {engine.Result.TrySetResult(new([],TimeSpan.Zero));Complete(pipeline.DisposeAsync().AsTask());Close(overlay);Close(overlay.Chrome);Close(main);Directory.Delete(dir,true);}
    }
    private sealed class HeldOcr : IOcrService
    {
        public bool Started;public TaskCompletionSource<OcrResult> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token){Started=true;return Result.Task;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class NoAi : ITranslationService
    {
        public int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken token){Calls++;return Task.FromResult(new TranslationResult("unexpected",TimeSpan.Zero,0));}
    }
}
