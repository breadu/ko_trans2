using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class ScaledOriginalOptionTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.ScaledOriginal",Guid.NewGuid().ToString("N"));
        var settings=new AppSettings();settings.Validate();var game=settings.CreateGameProfile("Synthetic game");var other=settings.CreateGameProfile("Other game");
        settings.ActiveProfileId=game.Id;game.CaptureMode=KoTrans.Core.CaptureMode.Window;
        var target=new WindowTarget(new IntPtr(50),50,50,"Synthetic","game");var bounds=new PixelRect(100,200,800,600);
        var scale=new WindowScaleTransform(WindowScaleProvider.Magpie,target.Handle,new IntPtr(51),bounds,new(0,0,1600,1200));bool lossless=false;
        var store=new SettingsStore(root);var main=Track(new MainWindow(store,new ApiKeyStore(root),()=>[target],_=>bounds,
            getWindowScale:_=>scale,useOriginalWindowCapture:_=>lossless));main.ApplyLoadedSettings(settings);
        try {
            main.SelectGameWindow(target);main.ShowProfileSettingsPage(1);Layout(main,1060,900);
            var option=Named<CheckBox>(main,"OriginalCaptureWhenScaled");
            Check(!game.OriginalCaptureWhenScaled && !option.IsChecked.GetValueOrDefault()
                && !System.Text.Json.JsonSerializer.Deserialize<Profile>("{}")!.OriginalCaptureWhenScaled,"Existing profiles automatically switched capture backend");
            Check(!main.CaptureOriginalWindow(target,scale) && option.IsEnabled,"Default Magpie path changed or the option is inaccessible");
            Check(option.Foreground is SolidColorBrush {Color:{R:>180,G:>180,B:>180}},"Original capture option is unreadable on dark background");
            var pipeline=main.CreateProfilePipeline(new NoOcr(),new MockTranslationService(),_=>throw new Exception("No capture expected"));
            main.ObserveTranslation(pipeline,game.Id,watch:false);pipeline.Start(false);
            option.IsChecked=true;
            Check(!pipeline.IsRunning && game.OriginalCaptureWhenScaled && !other.OriginalCaptureWhenScaled && !settings.DefaultProfile.OriginalCaptureWhenScaled,"Toggle did not stop old work or changed another profile");
            Check(main.FeedbackToast.Detail.Contains("다시 시작"),"Backend change lacks restart guidance");Complete(pipeline.DisposeAsync().AsTask());
            var edit=main.CaptureAreaEditContext()!;
            Check(edit.OriginalPreview && main.CaptureOriginalWindow(target,scale),"Opt-in Magpie capture did not select original-window rendering/preview");
            Complete(main.ConfirmSourceCaptureAreaAsync(new(50,60,200,100),game.Id,edit));
            Check(game.WindowCaptureRegion==new PixelRect(50,60,200,100) && game.CaptureArea==new PixelRect(150,260,200,100),"Original preview was incorrectly inverse-scaled as Magpie output");
            var loaded=store.LoadAsync();Complete(loaded);
            Check(loaded.Result.Profiles.Single(p=>p.Id==game.Id).OriginalCaptureWhenScaled && !loaded.Result.Profiles.Single(p=>p.Id==other.Id).OriginalCaptureWhenScaled,"Preference did not persist per profile");
            Check(!main.CaptureOriginalWindow(target,null),"Opt-in changed ordinary non-scaled capture");
            lossless=true;Check(main.CaptureOriginalWindow(target,null),"Lossless original capture was disabled by the new option");
            option.IsChecked=false;Check(main.CaptureOriginalWindow(target,null) && !main.CaptureOriginalWindow(target,scale),"Disabling Magpie opt-in affected Lossless or failed to restore previous Magpie path");
            Complete(main.SwitchProfileAsync(other));Check(option.IsChecked==false,"Switching profiles retained another game's preference");
            Complete(main.SwitchProfileAsync(game));Check(option.IsChecked==false,"Disabled capture preference did not roundtrip");
        } finally {Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class NoOcr:IOcrService
    {
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t)=>throw new Exception("No OCR expected");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
