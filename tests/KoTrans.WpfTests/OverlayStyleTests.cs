using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;
namespace KoTrans.WpfTests;
internal static class OverlayStyleTests
{
    internal static void Lifecycle()
    {
        string dir=Temp();var settings=new AppSettings();settings.Validate();var main=Track(new MainWindow(new SettingsStore(dir),new ApiKeyStore(dir)));main.ApplyLoadedSettings(settings);
        var vm=(MainViewModel)main.DataContext;var frame=new ImageFrame(80,40,320,new byte[12800]);
        var engine=new ImmediateOcr();var ai=new ImmediateAi();var pipeline=main.CreateProfilePipeline(engine,ai,_=>Task.FromResult(frame));
        var first=Track(main.CreateOverlayWindow());Track(first.Chrome);
        try {
            main.ObserveTranslation(pipeline,settings.ActiveProfileId);pipeline.Start(false);Complete(pipeline.RequestManualAsync());
            Until(()=>((TextBox)main.FindName("TranslatedText")).Text=="mock translation","Mock initial translation missing");
            Check(ReferenceEquals(first,main.CreateOverlayWindow()) && pipeline.IsRunning,"Repeated open replaced/stopped live overlay");
            ((Slider)main.FindName("OriginalFontSlider")).Value=28;((ComboBox)main.FindName("OriginalFontColor")).SelectedItem="#FFE68A";
            Check(pipeline.IsRunning && ReferenceEquals(first,main.CreateOverlayWindow()),"Changing text style stopped translation/closed overlay");
            Complete(main.SwitchProfileAsync(settings.DefaultProfile));
            Check(pipeline.IsRunning && ReferenceEquals(first,main.CreateOverlayWindow()),"Reselecting active profile closed/stopped translation");
            first.CloseForReplacement();Pump();
            Check(pipeline.IsRunning && !vm.Status.Contains("자막창을 닫아"),"Internal overlay replacement triggered user stop");
            var replacement=Track(main.CreateOverlayWindow());Track(replacement.Chrome);
            try {
                Check(!Text(replacement).Contains("자막창을 닫아"),"Replacement copied stale closed message");
                Complete(pipeline.RequestManualAsync(true));Until(()=>ai.Calls==2,"Replacement interrupted Mock translation");
                Click(Button(replacement.Chrome,"×"));Check(!pipeline.IsRunning && vm.Status.Contains("자막창을 닫아"),"User X did not stop work");
                var reopened=Track(main.CreateOverlayWindow());Track(reopened.Chrome);
                try {
                    Check(!Text(reopened).Contains("자막창을 닫아") && Equals(Named<Button>(reopened.Chrome,"OverlayToggleTranslation").Content,"시작"),"Reopened overlay retained closed status or lacks start command");
                    Check(!pipeline.IsRunning && ai.Calls==2,"Opening overlay automatically sent a request");
                    var other=settings.CreateGameProfile("Other");Complete(main.SwitchProfileAsync(other));
                    Check(reopened.ClosedForReplacement && !vm.Status.Contains("자막창을 닫아"),"Profile replacement marked as user close");
                    var changed=Track(main.CreateOverlayWindow());Track(changed.Chrome);
                    try {Check(!Text(changed).Contains("자막창을 닫아"),"New profile displayed old close reason");} finally {Close(changed);Close(changed.Chrome);}
                } finally {Close(reopened);Close(reopened.Chrome);}
            } finally {Close(replacement);Close(replacement.Chrome);}
        } finally {Complete(pipeline.DisposeAsync().AsTask());Close(first);Close(first.Chrome);Close(main);Directory.Delete(dir,true);}
    }
    internal static void Styles()
    {
        string dir=Temp();var store=new SettingsStore(dir);var settings=new AppSettings();settings.Validate();var a=settings.DefaultProfile;
        a.OverlayFontSize=31;a.OverlayColor="#AA1234";a.OverlayOriginalFontSize=19;a.OverlayOriginalColor="#12AB34";
        var b=settings.CreateGameProfile("Other");b.OverlayFontSize=22;b.OverlayColor="#1234AB";b.OverlayOriginalFontSize=27;b.OverlayOriginalColor="#ABCD12";
        string? selected=null;string? initial=null;Action? duringPicker=null;
        var main=Track(new MainWindow(store,new ApiKeyStore(dir),pickOverlayColor:color=>{initial=color;duringPicker?.Invoke();return selected;}));main.ApplyLoadedSettings(settings);
        try {
            var overlay=Track(main.CreateOverlayWindow());Track(overlay.Chrome);overlay.SetResult("original","translation","test");
            try {
                Check(((Slider)main.FindName("OriginalFontSlider")).Value==19,"Original slider not loaded");
                main.ShowProfileSettingsPage(3);
                Layout(main,1060,740);
                Check(((ComboBox)main.FindName("FontColor")).ActualWidth>70 && ((ComboBox)main.FindName("OriginalFontColor")).ActualWidth>70
                    && ((Button)main.FindName("TranslationColorPicker")).ActualWidth==36 && ((Button)main.FindName("OriginalColorPicker")).ActualWidth==36,"Font/color controls clipped at minimum window width");
                var original=Elements<TextBlock>(overlay).Single(t=>t.Text=="original");var translated=Elements<TextBlock>(overlay).Single(t=>t.Text=="translation");
                Check(original.FontSize==19 && translated.FontSize==31 && Hex(original)=="#12AB34" && Hex(translated)=="#AA1234","Independent text styles not applied");
                ((Slider)main.FindName("OriginalFontSlider")).Value=36;Check(original.FontSize==36 && translated.FontSize==31,"Original slider changed Korean size");
                ((ComboBox)main.FindName("FontColor")).SelectedItem="#FFE68A";Check(Hex(translated)=="#FFE68A" && Hex(original)=="#12AB34","Korean preset changed original color");
                var sourceColors=(ComboBox)main.FindName("OriginalFontColor");
                Check(new[]{"#FFFFFF","#FFE68A","#A5DEFF"}.All(c=>sourceColors.Items.Contains(c)),"Original preset palette missing");
                selected="#f012a3";Click((Button)main.FindName("OriginalColorPicker"));
                var saved=store.LoadAsync();Complete(saved);
                Check(initial=="#12AB34" && original.FontSize==36 && Hex(original)=="#F012A3" && Hex(translated)=="#FFE68A","Custom original color not applied/initialized correctly");
                Check(saved.Result.DefaultProfile.OverlayOriginalColor=="#F012A3" && saved.Result.DefaultProfile.OverlayOriginalFontSize==36,"Native-choice result not persisted");
                selected="#112233";Click((Button)main.FindName("TranslationColorPicker"));saved=store.LoadAsync();Complete(saved);
                Check(initial=="#FFE68A" && Hex(translated)=="#112233" && Hex(original)=="#F012A3","Custom Korean color changed original");
                Check(saved.Result.DefaultProfile.OverlayColor=="#112233","Korean custom color not saved");
                Check(((SolidColorBrush)((Button)main.FindName("OriginalColorPicker")).Background).Color==Color.FromRgb(240,18,163),"Color icon swatch stale");
                byte[] before=File.ReadAllBytes(Path.Combine(dir,"settings.json"));selected=null;Click((Button)main.FindName("OriginalColorPicker"));
                Check(a.OverlayOriginalColor=="#F012A3" && File.ReadAllBytes(Path.Combine(dir,"settings.json")).SequenceEqual(before),"Cancel changed style/settings");
                selected="#GG0000";Click((Button)main.FindName("OriginalColorPicker"));Check(a.OverlayOriginalColor=="#F012A3","Invalid picker result accepted");
                Complete(main.SwitchProfileAsync(b));
                Check(((Slider)main.FindName("OriginalFontSlider")).Value==27 && sourceColors.SelectedItem as string=="#ABCD12","Profile B source style not restored");
                Check(((Slider)main.FindName("FontSlider")).Value==22 && ((ComboBox)main.FindName("FontColor")).SelectedItem as string=="#1234AB","Profile B Korean style not restored");
                Complete(main.SwitchProfileAsync(a));
                Check(((Slider)main.FindName("OriginalFontSlider")).Value==36 && sourceColors.SelectedItem as string=="#F012A3","Profile A source style lost");
                selected="#556677";duringPicker=()=>Complete(main.SwitchProfileAsync(b));Click((Button)main.FindName("OriginalColorPicker"));duringPicker=null;
                Check(b.OverlayOriginalColor=="#ABCD12" && a.OverlayOriginalColor=="#F012A3","Stale color dialog applied to another profile");
                var load=store.LoadAsync();Complete(load);
                var restarted=Track(new MainWindow(store,new ApiKeyStore(dir)));restarted.ApplyLoadedSettings(load.Result);
                try {
                    Complete(restarted.SwitchProfileAsync(load.Result.DefaultProfile));
                    Check(((Slider)restarted.FindName("OriginalFontSlider")).Value==36 && ((ComboBox)restarted.FindName("OriginalFontColor")).SelectedItem as string=="#F012A3","Source style not restored into new UI");
                    Check(((Slider)restarted.FindName("FontSlider")).Value==31 && ((ComboBox)restarted.FindName("FontColor")).SelectedItem as string=="#112233","Korean style not restored into new UI");
                } finally {Close(restarted);}
            } finally {Close(overlay);Close(overlay.Chrome);}
        } finally {Close(main);Directory.Delete(dir,true);}
    }
    private static string Hex(TextBlock text)=>((SolidColorBrush)text.Foreground).Color.ToString()[..1]+((SolidColorBrush)text.Foreground).Color.ToString()[3..];
    private static string Temp(){string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.OverlayStyle",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);return dir;}
    private sealed class ImmediateOcr:IOcrService
    {
        public OcrState State=>OcrState.Ready;public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)=>Task.FromResult(new OcrResult([new("mock source",1,new(2,2,60,20))],TimeSpan.Zero));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class ImmediateAi:ITranslationService
    {
        public int Calls;public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken token){Calls++;return Task.FromResult(new TranslationResult("mock translation",TimeSpan.Zero,0));}
    }
}
