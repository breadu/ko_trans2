using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class TrayTests
{
    internal static void Lifecycle()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.TrayTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var settings=new AppSettings();settings.Validate();
        var main=Track(new MainWindow(new SettingsStore(dir),new ApiKeyStore(dir)));main.ApplyLoadedSettings(settings);
        var tray=new Tray();main.AttachTray(tray);
        var ocr=new NoOcr();var ai=new NoAi();
        var pipeline=new TranslationPipeline(ocr,ai,_=>throw new Exception("Background tray test captured"),new("en","ADV",false),new(),new(),"test");
        try {
            main.ObserveTranslation(pipeline,settings.ActiveProfileId,watch:true);pipeline.Start(false);
            main.Close();Pump();
            Check(main.IsInTray && !tray.Disposed && Application.Current.Windows.Cast<Window>().Contains(main),"X closed the application instead of hiding");
            Check(pipeline.IsRunning,"X stopped ongoing translation");
            tray.Open();Pump();
            Check(!main.IsInTray && pipeline.IsRunning,"Tray open changed translation or failed to route restoration");
            Complete(main.StartWithMainHiddenAsync(() => Task.FromResult(false)));
            Check(!main.IsInTray,"Failed automatic start did not restore the main window");
            var start=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion=main.StartWithMainHiddenAsync(() => {
                Check(main.IsInTray,"Main window was not hidden before preparation began");
                return start.Task;
            });
            Check(main.IsInTray && !completion.IsCompleted,"Main window remained visible during preparation");
            start.SetResult(true);Complete(completion);
            Check(main.IsInTray && pipeline.IsRunning,"Successful automatic start did not hide through the X policy");
            tray.Open();Pump();main.WindowState=WindowState.Minimized;tray.Open();Pump();
            Check(!main.IsInTray && main.WindowState==WindowState.Normal,"Opening minimized main window failed to restore state");
            // The user can reopen the main window while preparation is still pending.
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = main.StartWithMainHiddenAsync(() => start.Task);
            tray.Open();Pump();start.SetResult(true);Complete(completion);
            Check(!main.IsInTray,"Startup completion hid a window that the user reopened");
            tray.Exit();Until(()=>!Application.Current.Windows.Cast<Window>().Contains(main),"Tray exit failed to close main window");
            Check(tray.Disposed && !pipeline.IsRunning,"Exit leaked tray or translation work");
            Check(File.Exists(Path.Combine(dir,"settings.json")),"Explicit exit skipped settings save");
            Check(ocr.Calls==0 && ai.Calls==0,"Tray lifecycle invoked OCR/API");
            Check(tray.Subscribers==0,"Closed main window retained tray event handlers");
            Check(!main.IsVisible,"Background lifecycle displayed a window");
        }finally{main.DisposeTray();Complete(pipeline.DisposeAsync().AsTask());Close(main);Directory.Delete(dir,true);}
    }
    internal static void FailedExitAndFallback()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.TrayCancel",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var settings=new AppSettings();settings.Validate();var game=settings.CreateGameProfile("Game");settings.ActiveProfileId=game.Id;
        var main=Track(new MainWindow(new SettingsStore(dir),new ApiKeyStore(dir)));main.ApplyLoadedSettings(settings);
        var tray=new Tray();main.AttachTray(tray);
        try {
            main.Close();Check(main.IsInTray,"Close did not hide before exit-cancel test");
            ((TextBox)main.FindName("ProfileName")).Text="";tray.Exit();Pump();
            Check(!main.IsInTray && !tray.Disposed && Application.Current.Windows.Cast<Window>().Contains(main),"Invalid settings exited app or left it inaccessible");
            ((TextBox)main.FindName("ProfileName")).Text="Game";main.Close();Pump();
            Check(main.IsInTray,"Canceled exit left X permanently in exit mode");
            tray.Available=false;tray.Open();Pump();
            Check(!main.HideToTray() && !main.IsInTray && main.WindowState==WindowState.Minimized,"Missing tray hid the only recovery path");
            tray.Available=true;tray.Exit();Until(()=>tray.Disposed,"Exit after cancellation failed");
        }finally{main.DisposeTray();Close(main);Directory.Delete(dir,true);}
    }
    internal static void LauncherCaptions()
    {
        // Pure caption contract covers the registered-hotkey fallback without
        // installing global hotkeys in background WPF tests.
        Check(MainWindow.HideSettingsCaption(true, true, "F1") == "트레이로 숨기기 (F1로 복원)"
            && MainWindow.HideSettingsCaption(true, false, "F1") == "트레이로 숨기기 (트레이 아이콘으로 복원)"
            && MainWindow.HideSettingsCaption(false, true, "F4") == "메인 창 숨기기 (F4로 복원)"
            && MainWindow.HideSettingsCaption(false, false, "F4") == "작업 표시줄로 최소화",
            "Hide captions do not match the available tray and hotkey recovery paths");

        string directory = Path.Combine(Path.GetTempPath(), "KoTrans2.TrayCaptions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new AppSettings();
        settings.Validate();
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => []));
        var tray = new Tray();
        try
        {
            main.ApplyLoadedSettings(settings);
            var hint = Named<TextBlock>(main, "LauncherHotkeyHint");
            var hide = Named<Button>(main, "HideSettingsButton");
            Check(hint.Text == "번역 시작·중지: Shift+F1  ·  메인 창 표시: F1"
                && Equals(hide.Content, "작업 표시줄로 최소화"),
                "Default main-screen captions are inaccurate without recovery controls");

            settings.DefaultProfile.ShowTranslation = false;
            settings.Hotkeys.ToggleTranslation = "Shift+F4";
            settings.Hotkeys.ShowSettings = "F4";
            main.ApplyLoadedSettings(settings);
            Check(hint.Text == "원문 인식 시작·중지: Shift+F4  ·  메인 창 표시: F4",
                "Original-only mode or saved shortcut changes are missing from the launcher");

            main.AttachTray(tray);
            Check(Equals(hide.Content, "트레이로 숨기기 (트레이 아이콘으로 복원)"),
                "A tray without a registered restore key promised the F4 shortcut");
            tray.Available = false;
            main.ApplyLoadedSettings(settings);
            Check(Equals(hide.Content, "작업 표시줄로 최소화"),
                "Unavailable tray did not switch to the actual fallback caption");
            tray.Available = true;
            main.ApplyLoadedSettings(settings);
            Check(Equals(hide.Content, "트레이로 숨기기 (트레이 아이콘으로 복원)"),
                "Restored tray did not update the hide button caption");
        }
        finally { main.DisposeTray(); Close(main); Directory.Delete(directory, true); }
    }

    internal static void HotkeyDefaults()
    {
        using var icon=ApplicationTray.LoadIcon();
        Check(icon.Width>0 && icon.Height>0,"Embedded native tray icon could not be decoded");
        var fresh=new HotkeyOptions();
        Check(fresh.ShowSettings=="F1" && fresh.ToggleTranslation=="Shift+F1","Requested defaults not applied");
        var saved=System.Text.Json.JsonSerializer.Deserialize<HotkeyOptions>("{\"ShowSettings\":\"F4\",\"ToggleTranslation\":\"Shift+F4\"}")!;saved.Validate();
        Check(saved.ShowSettings=="F4" && saved.ToggleTranslation=="Shift+F4","Custom saved keys overwritten by new defaults");
        Check(DesktopInput.HotkeyFailure("F12",0x7B,false,1409).Contains("디버거 예약키"),"F12 failure lacks reserved-key cause");
        Check(DesktopInput.HotkeyFailure("F1",0x70,true,0).Contains("앱 내부"),"Internal duplicate mislabeled as another app");
        Check(DesktopInput.HotkeyFailure("F1",0x70,false,1409).Contains("Win32 1409"),"Registration error code missing");
        Check(fresh.SaveGameQa=="F8" && fresh.Explain=="Shift+F2","Study or QA shortcut default incorrect");
        Check(fresh.StudyScrollUp=="Shift+PageUp" && fresh.StudyScrollDown=="Shift+PageDown","Study scroll defaults incorrect");
        Check(DesktopInput.ParseHotkey(fresh.Explain)==(4u,0x71u),"Study chord did not parse as Shift+F2");
        Check(DesktopInput.ParseHotkey(fresh.StudyScrollUp)==(4u,0x21u)
            && DesktopInput.ParseHotkey(fresh.StudyScrollDown)==(4u,0x22u),"Global scroll chords did not parse as Shift+PageUp/PageDown");
        var legacy=new AppSettings {Hotkeys=new() {ShowSettings="F12",ToggleTranslation="Shift+F12"}};legacy.Validate();
        Check(legacy.Hotkeys.ShowSettings=="F1" && legacy.Hotkeys.ToggleTranslation=="Shift+F1","Unchanged legacy default pair was not migrated");
        var conflict=new AppSettings {Hotkeys=new() {ShowSettings="F12",ToggleTranslation="Shift+F12",Explain="F1"}};conflict.Validate();
        Check(conflict.Hotkeys.ShowSettings=="F12" && conflict.Hotkeys.Explain=="F1","Default migration introduced an internal collision");
        var custom=new AppSettings {Hotkeys=new() {ShowSettings="F4",ToggleTranslation="Shift+F12"}};custom.Validate();
        Check(custom.Hotkeys.ShowSettings=="F4" && custom.Hotkeys.ToggleTranslation=="Shift+F12","Partly customized legacy shortcuts changed");
    }
    internal sealed class Tray : IApplicationTray
    {
        public bool Available=true,Disposed;public bool IsAvailable=>Available&&!Disposed;
        public event Action? OpenRequested;public event Action? ExitRequested;
        public int Subscribers=>(OpenRequested?.GetInvocationList().Length??0)+(ExitRequested?.GetInvocationList().Length??0);
        public void Open()=>OpenRequested?.Invoke();public void Exit()=>ExitRequested?.Invoke();
        public void Dispose(){Disposed=true;OpenRequested=ExitRequested=null;}
    }
    private sealed class NoOcr : IOcrService
    {
        public int Calls;public OcrState State=>OcrState.NotConfigured;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>throw new Exception("No model loading");
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t){Calls++;throw new Exception("No OCR");}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class NoAi : ITranslationService
    {
        public int Calls;public Task<TranslationResult> TranslateAsync(TranslationRequest r,CancellationToken t){Calls++;throw new Exception("No API");}
    }
}
