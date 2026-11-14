using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class GameLaunchTests
{
    internal static void Controls()
    {
        string dir=FeedbackTests.DirectoryPath(); Directory.CreateDirectory(dir);
        string executable=Path.Combine(dir,"a game.exe"); File.WriteAllText(executable,"SYNTHETIC - NEVER EXECUTED");
        var saved=new GameWindowIdentity("a game",executable,"test-class","Game");
        var data=new AppSettings();data.Validate();var p=data.DefaultProfile;var other=data.CreateGameProfile("No executable");
        int calls=0;GameWindowIdentity? requested=null;
        TaskCompletionSource<GameLaunchOutcome>? gate=null;
        var main=Track(new MainWindow(new SettingsStore(dir),new ApiKeyStore(dir),()=>[],launchGame:(identity,token)=>{
            calls++;requested=identity;return gate?.Task??Task.FromResult(GameLaunchOutcome.Requested);
        }));
        try {
            foreach(var mode in Enum.GetValues<CaptureMode>()) {
                p.CaptureMode=mode;p.GameWindow=null;main.ApplyLoadedSettings(data);Layout(main,820,485);
                var button=Named<Button>(main,"LaunchGameButton");var watch=Named<Button>(main,"WatchButton");
                double originalWidth=watch.ActualWidth;
                Check(button.Visibility==Visibility.Collapsed,"Profile without executable shows launch action");
                p.GameWindow=saved;main.ApplyLoadedSettings(data);Layout(main,820,485);
                Check(button.Visibility==Visibility.Visible && Grid.GetColumn(button)==0 && Grid.GetColumn(watch)==1,"Saved executable not visible left of Start for capture mode "+mode);
                Check(watch.ActualWidth<originalWidth && watch.ActualHeight==button.ActualHeight,"Launcher didn't share action width/height");
                var command=main.LaunchCurrentGameAsync();Complete(command);
                Check(requested==saved && main.FeedbackToast.Message.Contains("실행을 요청") && !Equals(watch.Content,"번역 중지"),"Launch changed capture mode, started translation or claimed verified game startup");
                Check(p.CaptureMode==mode,"Game launch forced Window capture mode");
                p.GameWindow=saved with {ExecutablePath=" "};main.ApplyLoadedSettings(data);Layout(main,820,485);
                Check(button.Visibility==Visibility.Collapsed && Math.Abs(watch.ActualWidth-originalWidth)<.1,"Missing path didn't restore the original Start width");
            }
            p.GameWindow=saved;p.CaptureMode=CaptureMode.Region;main.ApplyLoadedSettings(data);
            gate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            int before=calls;var pending=main.LaunchCurrentGameAsync();Complete(main.LaunchCurrentGameAsync());
            Check(calls==before+1 && !Named<Button>(main,"LaunchGameButton").IsEnabled,"Repeated clicks started multiple launches");
            Complete(main.SwitchProfileAsync(other));int notices=main.FeedbackCount;
            gate.SetResult(GameLaunchOutcome.Requested);Complete(pending);
            Check(main.FeedbackCount==notices && Named<Button>(main,"LaunchGameButton").Visibility==Visibility.Collapsed,"Late launch result changed another profile's UI");
            Complete(main.SwitchProfileAsync(p));gate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending=main.LaunchCurrentGameAsync();gate.SetException(new Win32Exception(5));Complete(pending);
            Check(main.FeedbackToast.Kind==FeedbackKind.Error && main.FeedbackToast.Detail.Contains("권한") && Named<Button>(main,"LaunchGameButton").IsEnabled,"Launch failure wasn't explained or prevented retry");
            gate=new(TaskCreationOptions.RunContinuationsAsynchronously);pending=main.LaunchCurrentGameAsync();gate.SetResult(GameLaunchOutcome.Activated);Complete(pending);
            Check(main.FeedbackToast.Message.Contains("실행 중인 게임 창"),"Existing-window activation reported a new process");
            foreach(double width in new[]{360d,400d,640d,820d}) {
                main.ApplyAvailableWindowSize(new(width,1008));Layout(main,width-16,main.MinHeight-38);
                var button=Named<Button>(main,"LaunchGameButton");var watch=Named<Button>(main,"WatchButton");
                Check(button.ActualWidth==96 && watch.ActualWidth>=135 && Named<ScrollViewer>(main,"LauncherScroll").ScrollableHeight<.1,"Launcher action layout clips at width "+width);
            }
            var selected=OverlayActivityTests.Visuals<TextBlock>(Named<ComboBox>(main,"Profiles")).Single(t=>t.Text=="기본 프로필");
            var foreground=((SolidColorBrush)selected.Foreground).Color;
            static double Luminance(Color color) {
                static double Linear(byte value) {double s=value/255d;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);}
                return .2126*Linear(color.R)+.7152*Linear(color.G)+.0722*Linear(color.B);
            }
            foreach(var background in new[]{Color.FromRgb(236,242,250),Color.FromRgb(206,228,246)})
                Check((Luminance(background)+.05)/(Luminance(foreground)+.05)>=4.5,"Default profile lacks readable contrast on light combo backgrounds");
        } finally {gate?.TrySetCanceled();Close(main);Directory.Delete(dir,true);}
    }
    internal static void Contract()
    {
        string dir=FeedbackTests.DirectoryPath();Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"game with spaces.exe");File.WriteAllText(path,"SYNTHETIC - NEVER EXECUTED");
        var game=new GameWindowIdentity("game",path,"class","Title");
        try {
            var start=GameLauncher.PrepareStart(game);
            Check(start.FileName==path && start.WorkingDirectory==dir && start.UseShellExecute && start.Arguments=="" && start.Verb=="","Executable/working directory contract changed or command arguments were invented");
            foreach(var invalid in new[]{"game.exe", "https://example.invalid/game.exe",Path.Combine(dir,"missing.exe"),Path.Combine(dir,"game.cmd")}) {
                bool rejected=false;try {GameLauncher.PrepareStart(game with {ExecutablePath=invalid});} catch(TranslationException){rejected=true;}
                Check(rejected,"Unsafe/nonexistent executable accepted");
            }
            var window=new WindowTarget(new IntPtr(1234),1,1,"Title","game",path,"class");
            Check(GameLauncher.FindExisting(game,[window]).Single()==window,"Existing exact window not reused");
            Check(GameLauncher.FindExisting(game,[window with{Title="Updated title"}]).Count==1,"Changed game title could start a duplicate process");
            Check(GameLauncher.FindExisting(game,[window,window with{Handle=new IntPtr(1235)}]).Count==2,"Ambiguous running windows were silently narrowed");
            Check(GameLauncher.FindExisting(game,[window with{ExecutablePath=path+".other"}]).Count==0,"Unrelated executable matched");
        } finally {Directory.Delete(dir,true);}
    }
}
