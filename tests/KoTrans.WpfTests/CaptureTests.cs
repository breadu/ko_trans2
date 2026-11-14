using System.IO;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class CaptureTests
{
    internal static void Occlusion()
    {
        WaitingBinding();
        var capture = new PixelRect(100,100,400,200);
        var own = new CaptureWindowState(true,false,false,new(50,50,600,400));
        var game = new PixelRect(0,0,1000,700);
        Check(!WindowTargets.BlocksCapture(own,capture,game,true),"App entirely behind game incorrectly blocks capture");
        var expanded=new PixelRect(0,0,1600,1200);var expandedArea=new PixelRect(100,850,1400,250);
        var behind=own with {Bounds=new PixelRect(200,800,800,300)};
        Check(!WindowTargets.BlocksCapture(behind,expandedArea,expanded,true),"App hidden by verified expanded image blocked scaled capture");
        Check(WindowTargets.BlocksCapture(behind,expandedArea,expanded,false),"App above expanded image bypassed capture protection");
        Check(WindowTargets.BlocksCapture(own,capture,game,false),"App above game bypasses capture guard");
        Check(WindowTargets.BlocksCapture(own,capture,null,true),"Unknown or transparent foreground bypasses capture guard");
        Check(WindowTargets.BlocksCapture(own,capture,new(0,0,300,700),true),"Exposed part outside foreground client bypasses capture guard");
        Check(!WindowTargets.BlocksCapture(own with { Bounds=new PixelRect(120,120,40,40) },capture,new(100,100,100,100),true),"Only the captured app intersection should need covering");
        Check(!WindowTargets.BlocksCapture(own,capture with {X=2000},null,false),"Unrelated monitor window blocks capture");
        Check(!WindowTargets.BlocksCapture(own with {Minimized=true},capture,null,false),"Native-minimized window with stale normal rectangle blocks capture");
        Check(!WindowTargets.BlocksCapture(own with {Visible=false},capture,null,false),"Native-hidden window with stale rectangle blocks capture");
        Check(!WindowTargets.BlocksCapture(own with {Cloaked=true},capture,null,false),"Window on another virtual desktop blocks capture");
        Check(!WindowTargets.BlocksCapture(own with {Bounds=null},capture,null,false),"Destroyed window blocks capture");
        Check(!WindowTargets.BlocksCapture(own,capture with {X=650},null,false),"Touching rectangle border counted as overlap");
        var negative = own with {Bounds=new PixelRect(-1900,100,600,400)};
        Check(!WindowTargets.BlocksCapture(negative,new(-1800,200,100,100),new(-1920,0,1920,1080),true),"Physical coordinates on negative monitor mixed with DIPs");
        foreach(var name in new[]{"설정창","학습창","번역창"}) Check(MainWindow.CaptureOverlapMessage(name).Contains(name),"Error does not identify blocking app window");
    }
    private static void WaitingBinding()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.CaptureBinding",Guid.NewGuid().ToString("N"));
        var chosen=new WindowTarget(new IntPtr(123),123,123,"Synthetic game","synthetic","C:\\synthetic\\game.exe","SyntheticClass");
        var candidates=new List<WindowTarget> {chosen}; CaptureWaitReason wait=CaptureWaitReason.None; bool gone=false;
        var window=Track(new MainWindow(new SettingsStore(root),new ApiKeyStore(root),()=>candidates,
            _=>gone ? throw new CaptureUnavailableException("Synthetic closed target")
                : wait!=CaptureWaitReason.None ? throw new CaptureUnavailableException("Synthetic wait",wait) : new PixelRect(0,0,800,600)));
        try {
            var settings=new AppSettings();settings.Validate();settings.DefaultProfile.CaptureMode=CaptureMode.Window;
            settings.DefaultProfile.GameWindow=chosen.Identity;window.ApplyLoadedSettings(settings);
            candidates.Clear();
            foreach(var reason in new[] {CaptureWaitReason.HiddenOrMinimized,CaptureWaitReason.NotForeground,CaptureWaitReason.Occluded}) {
                wait=reason;Check(window.RequireGameWindow()==chosen && window.ConnectedTarget==chosen,"Expected capture wait lost the selected live window");
            }
            wait=CaptureWaitReason.None;Check(window.RequireGameWindow()==chosen,"Restored window required reselection");
            gone=true;bool failed=false;
            try {window.RequireGameWindow();}catch(CaptureUnavailableException ex) {failed=!ex.IsExpectedWait;}
            Check(failed && window.ConnectedTarget is null,"Actual target loss was treated as an expected pause");
        } finally {Close(window);if(Directory.Exists(root)) Directory.Delete(root,true);}
    }
    internal static void Recovery()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.CaptureTests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings=new AppSettings(); settings.Validate();
        var window=Track(new MainWindow(new SettingsStore(dir),new ApiKeyStore(dir))); window.ApplyLoadedSettings(settings);
        int phase=0, scene=1;
        var ocr=new FakeOcr(); var ai=new FakeTranslation();
        Task<ImageFrame> Capture(CancellationToken token)
        {
            int state=Volatile.Read(ref phase);
            var own=new CaptureWindowState(state!=3,state==2,state==4,new(0,0,200,100));
            bool excluded=state==6;
            // Use the same production visibility filter and occlusion decision. No HWND or screen access.
            if(MainWindow.NeedsCaptureOverlapCheck(true,false,false,excluded)
                && WindowTargets.BlocksCapture(own,new(0,0,200,100),new(0,0,200,100),state==1))
                throw new CaptureUnavailableException(MainWindow.CaptureOverlapMessage(state==5?"학습창":"설정창"));
            byte current=(byte)Volatile.Read(ref scene);
            var pixels=new byte[80000]; Array.Fill(pixels,(byte)(current*80)); pixels[0]=current;
            return Task.FromResult(new ImageFrame(200,100,800,pixels));
        }
        var pipeline=new TranslationPipeline(ocr,ai,Capture,new("en","ADV",false),new(),new(),"synthetic",TimeSpan.FromMilliseconds(12),TimeSpan.FromMilliseconds(25));
        var model=(MainViewModel)window.DataContext;
        var translation=(TextBox)window.FindName("TranslatedText");
        try {
            window.ObserveTranslation(pipeline,settings.ActiveProfileId); pipeline.Start(true);
            Until(()=>model.Status.Contains("설정창이 캡처 영역을 가립니다"),"Genuine capture obstruction was not surfaced");
            Check(ai.Count==0,"AI was called while own app visibly blocked source");
            Volatile.Write(ref phase,1);
            Until(()=>translation.Text=="translation1" && model.Status.Contains("다음 입력 대기"),"Moving app behind game did not recover capture/status");
            Check(ai.Count==1,"App behind game sent duplicate AI requests");
            foreach(int recovered in new[]{2,3,4,6}) {
                Volatile.Write(ref phase,recovered==6?5:0);
                string blocked=recovered==6?"학습창":"설정창";
                Until(()=>model.Status.Contains(blocked+"이 캡처 영역을 가립니다"),"Transient overlap not reported");
                Check(translation.Text=="translation1","Capture obstruction cleared readable translation");
                Volatile.Write(ref phase,recovered);
                Until(()=>model.Status.Contains("다음 입력 대기"),"Minimize/hide/cloak/exclusion left stale capture error on UI");
                Check(translation.Text=="translation1" && ai.Count==1,"Capture recovery cleared/resubmitted existing result");
            }
            Volatile.Write(ref scene,2);
            Until(()=>translation.Text=="translation2" && model.Status.Contains("다음 입력 대기"),"New dialogue not translated after capture recovery");
            Check(ai.Count==2,"Recovery corrupted duplicate suppression");
        } finally { Complete(pipeline.DisposeAsync().AsTask()); Close(window); Directory.Delete(dir,true); }
    }
    private sealed class FakeOcr : IOcrService
    {
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)=>Task.FromResult(new OcrResult([new("complete dialogue "+frame.Pixels.Span[0],1,new(0,0,200,100))],TimeSpan.Zero));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class FakeTranslation : ITranslationService
    {
        private int count; public int Count=>Volatile.Read(ref count);
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken token)
        {
            int number=Interlocked.Increment(ref count);
            return Task.FromResult(new TranslationResult("translation"+number,TimeSpan.Zero,0));
        }
    }
}
