using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class SourceRegionTests
{
    internal static void Run()
    {
        var frame=new ImageFrame(800,600,3200,new byte[800*600*4]);PixelRect? selected=null;
        var picker=Track(new SourceRegionSelector(frame,null,r=>selected=r));
        try {
            Layout(picker,500,600);
            Check(!Named<Button>(picker,"ConfirmSourceRegion").IsEnabled,"Empty original-frame selection can be confirmed");
            picker.Begin(new(20,40));picker.Draft.Move(new(420,240));picker.Draft.End();Layout(picker,900,750);
            Check(picker.Draft.Area==new PixelRect(20,40,400,200) && Named<Button>(picker,"ConfirmSourceRegion").IsEnabled,"Preview selection was stored in display/DIP coordinates");
            Click(Named<Button>(picker,"ConfirmSourceRegion"));Check(selected==new PixelRect(20,40,400,200),"Preview confirmed the wrong source pixels");
            Layout(picker,360,480);Check(picker.Draft.Area==selected,"Resizing preview changed source selection");
            Check(Text(picker).Contains("원본 게임 화면") && Text(picker).Contains("원본 영역"),"Preview has no original-image explanation");
        }finally{Close(picker);}
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.SourceRegion",Guid.NewGuid().ToString("N"));
        var settings=new AppSettings();settings.Validate();var profile=settings.DefaultProfile;
        var target=new WindowTarget(new IntPtr(100),100,100,"Synthetic game","game");var bounds=new PixelRect(500,300,800,600);bool covered=true;
        var main=Track(new MainWindow(new SettingsStore(root),new ApiKeyStore(root),()=>[target],_=>bounds,useOriginalWindowCapture:_=>covered));
        main.ApplyLoadedSettings(settings);
        try {
            main.SelectGameWindow(target);var edit=main.CaptureAreaEditContext()!;
            Check(edit.OriginalPreview && edit.Scale is null,"Lossless discovery did not select source preview");
            Complete(main.ConfirmSourceCaptureAreaAsync(selected,profile.Id,edit));
            Check(profile.WindowCaptureRegion==selected && profile.CaptureArea==new PixelRect(520,340,400,200),"Original preview saved output coordinates instead of canonical game region");
            byte[] before=File.ReadAllBytes(Path.Combine(root,"settings.json"));
            Complete(main.ConfirmSourceCaptureAreaAsync(null,profile.Id,edit));
            Check(File.ReadAllBytes(Path.Combine(root,"settings.json")).SequenceEqual(before),"Preview cancel rewrote profile settings");
            covered=false;
            Check(!main.CaptureAreaEditContext()!.OriginalPreview && main.InitialCaptureArea()==new PixelRect(520,340,400,200),"Stopping scaler changed source area or opened preview for an ordinary game");
        } finally {Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
