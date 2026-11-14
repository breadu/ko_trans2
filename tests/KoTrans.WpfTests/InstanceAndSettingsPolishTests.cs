using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class InstanceAndSettingsPolishTests
{
    internal static void Activation()
    {
        string directory = FeedbackTests.DirectoryPath(); var data = new AppSettings(); data.Validate();
        var main = Track(new MainWindow(new SettingsStore(directory), new ApiKeyStore(directory), () => [])); main.ApplyLoadedSettings(data);
        main.AttachTray(new Tray());
        int callbacks = 0, foregroundPid = 0;
        var instance = InstanceActivation.TryOwn(directory, () => main.Dispatcher.BeginInvoke(new Action(() => { callbacks++; main.RestoreMainWindow(); }))) ?? throw new Exception("No first instance ownership");
        try {
            Check(InstanceActivation.TryOwn(directory, () => { }) is null, "Second launch acquired settings ownership");
            Check(InstanceActivation.PipeName(directory) == InstanceActivation.PipeName(Path.Combine(directory, "."))
                && InstanceActivation.PipeName(directory) == InstanceActivation.PipeName(directory.ToUpperInvariant()), "Equivalent directory produced another pipe");
            for (int i = 0; i < 3; i++) {
                main.HideToTray(); main.WindowState = WindowState.Minimized;
                var request = InstanceActivation.RequestAsync(directory, pid => foregroundPid = pid); Complete(request);
                Until(() => !main.IsInTray, "Second launch did not restore tray state");
                Check(request.Result && foregroundPid == Environment.ProcessId && main.WindowState == WindowState.Normal, "Activation handshake did not restore the same instance");
            }
            Check(callbacks == 3 && !main.IsVisible, "Activation duplicated callbacks or showed a Background window");
            main.HideToTray();
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(InstanceAndSettingsPolishTests).Assembly.Location);
            start.ArgumentList.Add("--activation-client"); start.ArgumentList.Add(directory);
            using (var child = System.Diagnostics.Process.Start(start)!) {
                Complete(child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                Until(()=>!main.IsInTray,"Another process did not restore the owner window");
                Check(child.ExitCode==0 && callbacks==4,"Another process acquired ownership or failed to activate");
            }
            // Invalid protocol input cannot activate the app and cannot poison the next connection.
            var invalid = Task.Run(async () => {
                using var pipe = new NamedPipeClientStream(".", InstanceActivation.PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous);
                using var deadline = new CancellationTokenSource(2000); await pipe.ConnectAsync(deadline.Token);
                var pid = new byte[4]; await pipe.ReadExactlyAsync(pid, deadline.Token); await pipe.WriteAsync(new byte[] { 0 }, deadline.Token);
            }); Complete(invalid);
            var valid = InstanceActivation.RequestAsync(directory); Complete(valid); Until(() => callbacks == 5, "Bad command blocked later activation");
            Check(valid.Result && callbacks == 5, "Invalid pipe command activated the application");
            string other = Path.Combine(directory, "isolated-test-data");
            using (var isolated = InstanceActivation.TryOwn(other, () => { })) Check(isolated is not null, "Isolated test-data instance was blocked");
            instance.Dispose(); Complete(instance.Completion); instance.Dispose();
            using var restarted = InstanceActivation.TryOwn(directory, () => { });
            Check(restarted is not null, "Normal exit kept the instance file locked");
            restarted!.Dispose(); Complete(restarted.Completion);
            var absent = InstanceActivation.RequestAsync(directory, timeout: TimeSpan.FromMilliseconds(60)); Complete(absent);
            Check(!absent.Result, "Missing instance was reported as activated");
        } finally { instance?.Dispose(); if(instance is not null) Complete(instance.Completion); main.DisposeTray(); Close(main); Directory.Delete(directory,true); }
    }
    internal static void Controls()
    {
        string directory = FeedbackTests.DirectoryPath(); var store = new SettingsStore(directory);
        var data = new AppSettings(); data.Validate(); var profile = data.DefaultProfile;
        var game = data.CreateGameProfile("기본 프로필"); // ID, not name, defines the distinguished profile.
        var main = Track(new MainWindow(store, new ApiKeyStore(directory), () => [])); main.ApplyLoadedSettings(data);
        try {
            main.ShowProfileSettingsPage(0); Layout(main,1060,900);
            var profiles = Named<ComboBox>(main,"Profiles");
            var selected = OverlayActivityTests.Visuals<TextBlock>(profiles).Single(t=>t.Text=="기본 프로필");
            Check(selected.Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(0x96,0x3C,0x4B), "Default selected caption is not muted red");
            var sample = (TextBlock)profiles.ItemTemplate.LoadContent(); sample.DataContext = game;
            var host = Track(new Window {Content=sample}); Layout(host,300,80);
            Check(sample.Foreground is SolidColorBrush other && other.Color == Color.FromRgb(0x16,0x28,0x3B), "Same-named game received default profile color"); Close(host);
            var modes = Named<ComboBox>(main,"ReadingMode");
            Check(Equals(((ComboBoxItem)modes.Items[0]).Content,"일반 (대사 중심)") && Equals(((ComboBoxItem)modes.Items[1]).Content,"노벨 (본문 중심)"), "Reading mode labels clarify ADV/NVL without changing their indices");
            var language=Named<ComboBox>(main,"SourceLanguage"); var vertical=Named<CheckBox>(main,"VerticalText");
            Check(vertical.Visibility==Visibility.Collapsed,"English shows Japanese vertical option");
            language.SelectedIndex=1; vertical.IsChecked=true; modes.SelectedIndex=1;
            Check(vertical.Visibility==Visibility.Visible && profile.OcrOptions().Vertical && profile.ReadingMode=="NVL","Japanese vertical/NVL settings failed");
            language.SelectedIndex=0;
            Check(vertical.Visibility==Visibility.Collapsed && vertical.IsChecked==true && profile.Vertical && !profile.OcrOptions().Vertical,"English erased Japanese preference or enabled vertical inference");
            Complete(main.SaveCurrentProfileAsync()); var load=store.LoadAsync();Complete(load);
            Check(load.Result.DefaultProfile.Vertical && load.Result.DefaultProfile.ReadingMode=="NVL","Hidden vertical preference or persisted mode ID changed");
            language.SelectedIndex=1;Check(vertical.Visibility==Visibility.Visible && vertical.IsChecked==true,"Returning to Japanese lost vertical preference");
            modes.SelectedIndex=0;Check(profile.ReadingMode=="ADV","General mode changed the persisted contract");
            main.ShowProfileSettingsPage(7);Layout(main,1060,900);
            var cards=new[]{Named<Border>(main,"GlobalStudyCard"),Named<Border>(main,"GlobalCredentialsCard"),Named<Border>(main,"GlobalHotkeysCard")};
            var content=(StackPanel)cards[0].Parent;
            content.Measure(new Size(740,double.PositiveInfinity));content.Arrange(new Rect(content.DesiredSize));content.UpdateLayout();
            double Y(Border card)=>System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(card).Y;
            Check(cards.All(c=>c.Visibility==Visibility.Visible) && Y(cards[0]) < Y(cards[1]) && Y(cards[1]) < Y(cards[2]),"Global cards are not in requested order");
            string CardText(Border card)=>string.Join(" ",Elements<TextBlock>(card).Select(t=>t.Text));
            Check(Elements<CheckBox>(cards[0]).Single().Name=="StudyTopmostOption" && CardText(cards[0]).Contains("문장 해설창"),"Study card contains the wrong fields");
            Check(Elements<ComboBox>(cards[1]).Any(c=>c.Name=="CredentialProvider") && CardText(cards[1]).Contains("API 제공자 인증정보"),"Credential card is missing protected provider controls");
            Check(Elements<HotkeyCaptureBox>(cards[2]).Any() && CardText(cards[2]).Contains("단축키") && !CardText(cards[2]).Contains("Ctrl을 사용할 수 없습니다"),"Hotkey grouping or removed copy is wrong");
            Named<CheckBox>(main,"StudyTopmostOption").IsChecked=false;Complete(main.SaveCurrentProfileAsync());
            load=store.LoadAsync();Complete(load);Check(!load.Result.StudyTopmost,"Grouped topmost checkbox no longer persists");
            Complete(main.SwitchProfileAsync(game));
            Check(!((MainViewModel)main.DataContext).Status.Contains("지금 번역"),"Profile status advertises a hidden action");
            main.ShowProfileSettingsPage(4); Named<RadioButton>(main,"GameTriggersEnabled").IsChecked=false; Named<RadioButton>(main,"AutomaticWatch").IsChecked=false;
            Check(!Named<TextBlock>(main,"RecognitionModeHelp").Text.Contains("지금 번역"),"Recognition help advertises a hidden action");
            var picker=Track(new ModelListWindow(TranslationProvider.Gemini,_=>Task.FromResult<IReadOnlyList<ProviderModel>>([])));
            try {Layout(picker,540,500);Check(Text(picker).Contains("검색어 입력:") && Text(picker).Contains("선택된 모델:"),"Model inputs are not labeled");}
            finally {Close(picker);}
        } finally {Close(main);Directory.Delete(directory,true);}
    }
    private sealed class Tray : IApplicationTray
    {
        public bool IsAvailable=>true;
        public event Action? OpenRequested {add{} remove{}}
        public event Action? ExitRequested {add{} remove{}}
        public void Dispose(){}
    }
}
