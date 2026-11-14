using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;
using CaptureMode = KoTrans.Core.CaptureMode;

namespace KoTrans.WpfTests;
internal static class ProfileTests
{
    internal static void Restoration()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.WpfProfiles",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings=new AppSettings(); settings.Validate(); var defaults=settings.DefaultProfile;
        defaults.Language="일본어"; defaults.Translation=new() {Provider=TranslationProvider.Ollama,Model="model-X"};
        defaults.OverlayFontSize=25; defaults.OverlayColor="#123ABC";
        defaults.GameWindow=new("emulator","","Class","Default Game");
        var a=settings.CreateGameProfile("A"); var b=settings.CreateGameProfile("B");
        a.Language="영어"; a.ReadingMode="ADV"; a.CaptureMode=CaptureMode.Window; a.Vertical=false;
        a.CaptureArea=new(-1000,50,400,180); a.WindowCaptureRegion=new(30,40,400,180);
        a.GameWindow=new("emulator","","Class","Game A"); a.Triggers=new() {Keys="Enter",MouseButton="Left",GamepadButtons="Joy1"};
        a.OverlayWindow=new() {X=-1500,Y=300,Width=480,Height=300,MonitorId="portrait"};
        a.OverlayOpacity=.65; a.ShowOriginal=false; a.OverlayClickThrough=false; a.TriggerDelayMs=600; a.AutomaticWatch=false;
        a.Translation=new() {Provider=TranslationProvider.Ollama,Model="model-A",OllamaAddress="http://localhost:11435"};
        a.Translation.OllamaModels=a.Translation.OllamaModels.SetItem("model-A",new(OllamaRequestFormat.TranslateGemma,160,9));
        b.Language="일본어"; b.ReadingMode="NVL"; b.Vertical=true; b.CaptureMode=CaptureMode.Window;
        b.CaptureArea=new(80,120,650,300); b.WindowCaptureRegion=new(10,20,650,300); b.GameWindow=new("emulator","","Class","Game B");
        b.Triggers=new() {Enabled=false,Keys="Space",MouseButton="Right",GamepadButtons="Joy2"}; b.TriggerDelayMs=100; b.AutomaticWatch=true;
        b.OverlayWindow=new() {X=100,Y=700,Width=700,Height=200,MonitorId="main"}; b.OverlayColor="#456789";
        b.Translation=new() {Provider=TranslationProvider.OpenAI,Model="model-B"};
        var candidates=new List<WindowTarget> { new(new IntPtr(1),1,1,"Game A","emulator","","Class"), new(new IntPtr(2),2,2,"Game B","emulator","","Class") };
        PixelRect gameBounds=new(-1200,100,1000,700);
        var store=new SettingsStore(dir); var vault=new ApiKeyStore(dir);
        var window=Track(new MainWindow(store,vault,()=>candidates,_=>gameBounds)); window.ApplyLoadedSettings(settings);
        var engine=new RecordingOcr(); var ai=new RecordingTranslation();
        try {
            Check(((ComboBox)window.FindName("Profiles")).SelectedItem==defaults && ((Button)window.FindName("DeleteProfileButton")).IsEnabled==false,"Default unusable or deletable");
            Click(Button(window,"새로 만들기"));
            Until(() => !window.IsUserSavePending, "New profile save did not settle before profile switching");
            var created=settings.Profiles.Single(p=>p.Id==settings.ActiveProfileId);
            Check(created.Id!=defaults.Id && created.Language=="일본어" && created.OverlayFontSize==25 && created.Translation.Model=="model-X" && created.GameWindow is null && created.CaptureMode==CaptureMode.Window,"Actual New Profile command failed Default copy/reset/window default");
            Check(!ReferenceEquals(created.OverlayWindow,defaults.OverlayWindow),"Actual New Profile command shared placement");
            Complete(window.SwitchProfileAsync(defaults));
            var global=Track(window.CreateTranslationSettings());
            var study=Track(window.EnsureStudy());
            try {
                Check(Named<StackPanel>(global,"GlobalKeys").Visibility==Visibility.Visible && Named<StackPanel>(global,"GlobalHotkeys").Visibility==Visibility.Visible,"Default global UI hidden");
                Named<TextBox>(global,"ShowHotkey").Text="F7";
                Check(global.ApplyChanges(defaults,settings.Hotkeys)&&settings.Hotkeys.ShowSettings=="F7","Default cannot apply shared hotkey");
                // Use a provider that actually owns a DPAPI key; only synthetic test data.
                Named<ComboBox>(global,"Provider").SelectedItem=TranslationProvider.Gemini;
                Elements<PasswordBox>(global).Single().Password="synthetic-gemini-key"; Click(Button(global,"키 등록/변경"));
                Check(vault.Read(TranslationProvider.Gemini)=="synthetic-gemini-key","Default synthetic DPAPI edit failed");
                foreach(var selected in new[]{a,b,a,b}) {
                    Complete(window.SwitchProfileAsync(selected));
                    Check(settings.ActiveProfileId==selected.Id && ((ComboBox)window.FindName("SourceLanguage")).SelectedIndex==(selected.Language=="일본어"?1:0),"Language UI failed A/B switch");
                    Check(((ComboBox)window.FindName("ReadingMode")).SelectedIndex==(selected.ReadingMode=="NVL"?1:0) && ((CheckBox)window.FindName("VerticalText")).IsChecked==selected.Vertical,"Mode UI failed restoration");
                    Check(((ComboBox)window.FindName("InputMode")).SelectedIndex==0 && ((ComboBox)window.FindName("FontColor")).SelectedItem as string==selected.OverlayColor,"Capture mode/custom color restoration failed");
                    Check(window.ConnectedTarget?.Title==selected.GameWindow!.Title && !((TextBlock)window.FindName("TargetLabel")).Text.Contains("여러"),"Game target not restored");
                    Check(Named<StackPanel>(global,"GlobalKeys").Visibility==Visibility.Collapsed && Named<StackPanel>(global,"GlobalHotkeys").Visibility==Visibility.Collapsed,"Already open global editor not hidden");
                    Check(!Elements<CheckBox>(study).Any() && !Elements<Button>(study).Any(),
                        "Reader unexpectedly exposes profile-global commands or preferences");
                    Check(Named<CheckBox>(window, "StudyTopmostOption").Visibility == Visibility.Collapsed,
                        "A game profile must not expose shared reader preferences");
                    bool was=settings.StudyTopmost; study.RefreshGlobalAccess();
                    Check(settings.StudyTopmost==was && study.Topmost==was,
                        "Profile change should not alter the saved reader Topmost preference");
                    var dialog=Track(window.CreateTranslationSettings());
                    try {
                        Check(Named<TextBox>(dialog,"Model").Text==selected.Translation.Model && Named<KeyboardTriggerEditor>(dialog,"TriggerKeys").Value==selected.Triggers.Keys
                            && Named<GamepadTriggerEditor>(dialog,"TriggerPad").Value==selected.Triggers.GamepadButtons
                            && Named<TextBox>(dialog,"TriggerDelayMs").Text==selected.TriggerDelayMs.ToString(),"Model/trigger/delay UI failed restoration");
                        Check(Named<RadioButton>(dialog,"AutomaticWatch").IsChecked==selected.AutomaticWatch && window.WatchPreference==selected.AutomaticWatch,"Watch preference not used");
                        Named<TextBox>(dialog,"ShowHotkey").Text="F6";
                        Check(dialog.ApplyChanges(selected,settings.Hotkeys)&&settings.Hotkeys.ShowSettings=="F7","Regular profile changed global hotkey or could not apply local settings");
                        Elements<PasswordBox>(dialog).Single().Password="must-not-save";
                        Named<ComboBox>(dialog,"Provider").SelectedItem=TranslationProvider.Gemini;
                        Click(Button(dialog,"키 등록/변경")); Click(Button(dialog,"키 삭제"));
                        Check(vault.Read(TranslationProvider.Gemini)=="synthetic-gemini-key","Hidden key command changed shared credentials");
                    } finally { Close(dialog); }
                    var persisted=store.LoadAsync(); Complete(persisted);
                    Check(persisted.Result.ActiveProfileId==selected.Id && persisted.Result.DefaultProfileId==defaults.Id,"Switch did not save active ID");
                    var overlay=Track(new TranslationWindow(selected));
                    try { overlay.SetResult("original","translation","test"); Layout(overlay,500,300);
                        Check(overlay.Width==selected.OverlayWindow.Width && overlay.Height==selected.OverlayWindow.Height && overlay.Opacity==selected.OverlayOpacity,"Overlay size/style lost during switch");
                        overlay.CommitPlacement(); Check(selected.OverlayWindow.MonitorId==(selected==a?"portrait":"main"),"Unedited overlay overwrote saved monitor");
                    } finally { Close(overlay); }
                    var pipeline=window.CreateProfilePipeline(engine,ai,_=>Task.FromResult(new ImageFrame(800,400,3200,new byte[1280000])));
                    try { window.ObserveTranslation(pipeline,selected.Id); pipeline.Start(false); Check(ai.Requests==engine.Requests,"Starting alone called OCR/AI");
                        Complete(pipeline.RequestManualAsync()); Until(()=>ai.Requests==engine.Requests && ai.Last is not null && ((TextBox)window.FindName("TranslatedText")).Text=="synthetic translation","Mock profile pipeline did not complete");
                        Check(engine.Last==selected.OcrOptions() with {BypassOcrCaches=true},"Manual OCR did not preserve profile options and force fresh recognition");
                        Check(ai.Last!.Options.Provider==selected.Translation.Provider && ai.Last.Options.Model==selected.Translation.Model && ai.Last.SourceLanguage==selected.OcrOptions().Language,"Actual translation model/language arguments not restored");
                        Check(ai.Last.Options.OllamaAddress==selected.Translation.OllamaAddress && ai.Last.Options.SettingsForOllama()==selected.Translation.SettingsForOllama(),"Actual Ollama endpoint/format/timeout/keepalive changed");
                    } finally { Complete(pipeline.DisposeAsync().AsTask()); }
                }
                Named<TextBox>(global,"ShowHotkey").Text="F6";
                Check(!global.ApplyChanges(defaults,settings.Hotkeys)&&settings.Hotkeys.ShowSettings=="F7","Stale Default editor applied while game active");
                Click(Button(global,"키 삭제")); Check(vault.Read(TranslationProvider.Gemini)=="synthetic-gemini-key","Stale default key command bypassed access");
                Complete(window.SwitchProfileAsync(a)); var crop=a.WindowCaptureRegion;
                window.SelectGameWindow(candidates[0]); Check(a.WindowCaptureRegion==crop,"Reselecting same game cleared crop");
                Check(window.WindowRegion(a,candidates[0])==crop,"Old absolute region overwrote valid relative crop");
                a.WindowCaptureRegion=null; a.CaptureArea=new(-1170,140,400,180);
                window.SelectGameWindow(candidates[0]); Check(a.WindowCaptureRegion==crop,"Legacy absolute crop did not migrate at explicit game selection");
                candidates.Clear(); window.ReconnectTarget(); Check(window.ConnectedTarget is null && a.GameWindow is not null && a.WindowCaptureRegion==crop,"Closed game lost identity/crop");
                candidates.Add(new(new IntPtr(11),11,11,"Game A","emulator","","Class")); window.ReconnectTarget();
                Check(window.ConnectedTarget?.ProcessId==11 && a.WindowCaptureRegion==crop,"Restarted game did not reconnect");
                candidates.Add(new(new IntPtr(12),12,12,"Game A","emulator","","Class")); window.ReconnectTarget(); Check(window.ConnectedTarget is null,"Multiple game matches arbitrarily picked");
                candidates.RemoveAt(1); gameBounds=new(100,200,150,100); window.ReconnectTarget();
                Check(a.WindowCaptureRegion==crop && ((TextBlock)window.FindName("TargetLabel")).Text.Contains("영역 지정"),"Resize erased crop instead of explaining");
                bool resizeRejected=false; try { window.WindowRegion(a,candidates[0]); } catch(CaptureUnavailableException) { resizeRejected=true; }
                Check(resizeRejected && a.WindowCaptureRegion==crop,"Actual window crop path did not reject resized game while preserving saved region");
                Check(a.OverlayWindow.X==-1500 && b.OverlayWindow.X==100 && a.OverlayColor=="#123ABC" && b.OverlayColor=="#456789","A/B placement or custom RGB bled across profiles");
                Complete(window.SwitchProfileAsync(defaults));
                Check(Named<StackPanel>(global,"GlobalKeys").Visibility==Visibility.Visible
                    && study.Topmost==settings.StudyTopmost,
                    "Default global settings or reader Topmost policy did not restore");
                window.ShowProfileSettingsPage(7);
                var topmost = Named<CheckBox>(window,"StudyTopmostOption");
                Check(topmost.Visibility == Visibility.Visible
                    && topmost.IsChecked == settings.StudyTopmost,
                    "Common settings must expose the old reader Topmost preference");
                bool oldTopmost = settings.StudyTopmost;
                topmost.IsChecked = !oldTopmost;
                Check(settings.StudyTopmost == !oldTopmost && study.Topmost == !oldTopmost,
                    "Changing reader Topmost failed to apply immediately");
                topmost.IsChecked = oldTopmost;
                // Disabled dictionary keeps a missing path and never loads it.
                defaults.CharacterDictionaryPath=Path.Combine(dir,"missing.json"); defaults.CharacterDictionaryEnabled=false;
                Check(window.LoadProfileCharacters().Count==0,"Disabled dictionary was still loaded");
                var disabled=Track(window.CreateTranslationSettings()); try { Check(disabled.ApplyChanges(defaults,settings.Hotkeys)&&defaults.CharacterDictionaryPath.EndsWith("missing.json"),"Disabled dictionary path lost or validated as active"); } finally { Close(disabled); }
            } finally { Close(global); Close(study); }
            Check(ai.Requests==4 && engine.Requests==4,"Profile selection sent an unsolicited request");
            Complete(store.SaveAsync(settings));
            Check(!File.ReadAllText(Path.Combine(dir,"settings.json")).Contains("synthetic-gemini-key"),"DPAPI key was copied into profile settings");
        } finally { Close(window); Directory.Delete(dir,true); }
    }
    internal static void SaveCommands()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.ProfileSave",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var settings=new AppSettings();settings.Validate();
        settings.DefaultProfile.CaptureArea=new(120,140,400,180);
        settings.DefaultProfile.Language="일본어";
        var candidate=new WindowTarget(new IntPtr(200),200,200,"Game title","emulator","C:/synthetic/emulator.exe","GameClass");
        var candidates=new List<WindowTarget>{candidate};bool minimized=true;
        PixelRect Bounds(WindowTarget _)=>minimized?throw new CaptureUnavailableException("Synthetic minimized game"):new(100,100,900,600);
        var store=new SettingsStore(dir);
        var window=Track(new MainWindow(store,new ApiKeyStore(dir),()=>candidates.ToArray(),Bounds));window.ApplyLoadedSettings(settings);
        Guid createdId=Guid.Empty;GameWindowIdentity? savedIdentity=null;
        try {
            Click(Button(window,"새로 만들기")); Until(() => !window.IsUserSavePending, "New profile save did not finish");
            createdId=settings.ActiveProfileId;
            var created=settings.Profiles.Single(p=>p.Id==createdId);
            Check(createdId!=settings.DefaultProfileId && created.GameWindow is null,"New profile did not start independently");
            Check(!created.CaptureAreaConfigured && created.WindowCaptureRegion is null
                && window.WindowRegion(created, candidate) is null, "First target would silently use another game's crop");
            // An area explicitly chosen for THIS game must still survive target selection while minimized.
            created.CaptureArea = new(120,140,400,180);
            created.CaptureAreaConfigured = true;
            ((TextBox)window.FindName("ProfileName")).Text="게임 A · 保存";
            // Synthetic HWND match covers the direct-click resolver without
            // showing an overlay, touching the desktop or capturing real mouse input.
            Check(WindowClickSelector.MatchCandidate(candidates, candidate.Handle) == candidate
                && WindowClickSelector.MatchCandidate(candidates, new IntPtr(999)) is null,
                "Direct window-click selector picked a window not present in the existing list");
            PhysicalClickPoint? consumedClick = null;
            var mockOverlay = Track(new WindowClickOverlay(new System.Drawing.Rectangle(-1280, 0, 1280, 900),
                point => consumedClick = point, () => { }));
            try
            {
                Check(mockOverlay.AllowsTransparency && mockOverlay.Topmost && !mockOverlay.ShowInTaskbar
                    && mockOverlay.Cursor == System.Windows.Input.Cursors.Cross
                    && mockOverlay.WindowStyle == WindowStyle.None
                    && mockOverlay.Left == -1280,
                    "Window-click overlay lacks crosshair, transparency, or multi-monitor bounds");
                mockOverlay.BeginPointerPress(new PhysicalClickPoint(-500, 240));
                Check(consumedClick is null, "Target selection was committed on mouse-down and could leak mouse-up to the game");
                mockOverlay.FinishPointerPress();
                Check(consumedClick == new PhysicalClickPoint(-500, 240),
                    "Completing a mouse click did not select the intended physical pixel on the secondary monitor");
            }
            finally { Close(mockOverlay); }
            Named<ComboBox>(window, "InputMode").SelectedIndex = 0;
            Check(Named<Button>(window, "PickWindowDirectButton").IsEnabled,
                "Capture page has no direct-click target selection command");
            var missingArea = window.SelectCaptureAreaAsync();
            Complete(missingArea);
            Check(!missingArea.Result && window.FeedbackToast.Visibility == Visibility.Visible
                && window.FeedbackToast.Message == "번역할 게임 창을 선택하세요."
                && ((MainViewModel)window.DataContext).Status == window.FeedbackToast.Message,
                "Missing capture target did not show the selection toast and status");
            var picker=Track(window.CreateTargetPicker());
            try {
                var entries = Named<ListBox>(picker,"TargetCandidates");
                var choose = Button(picker,"선택");
                var reloadWindows = Button(picker,"새로 고침");
                Check(picker.MinWidth >= 540 && picker.MinHeight >= 310,
                    "Dark target picker lacks a usable minimum size");
                Check(picker.Background is SolidColorBrush { Color: var background }
                    && background == Color.FromRgb(0x10,0x1B,0x2A),
                    "Target picker is still using the default white window background");
                Check(entries.Background is SolidColorBrush { Color: var listColor }
                    && listColor == Color.FromRgb(0x12,0x22,0x35)
                    && entries.Foreground is SolidColorBrush { Color: var textColor }
                    && textColor.R > 160,
                    "Window candidates remain unreadable on a light list");
                Check(entries.ItemContainerStyle?.Triggers.OfType<Trigger>().Any(t =>
                    t.Property == ListBoxItem.IsSelectedProperty) == true,
                    "Target picker lacks a distinct selected-row theme");
                Check(choose.Background is SolidColorBrush { Color: var accent }
                    && accent == Color.FromRgb(0x32,0x8B,0xD0)
                    && reloadWindows.Background is SolidColorBrush { Color: var neutral }
                    && neutral == Color.FromRgb(0x2A,0x40,0x56),
                    "Target picker buttons do not match launcher colors");
                Layout(picker,700,420);
                Check(entries.ActualHeight > 100 && choose.ActualHeight >= 38,
                    "Window picker list or action row collapsed at its normal size");
                candidates.Clear();
                Click(reloadWindows);
                Check(entries.Items.Count == 0,"Refreshing the target picker kept removed windows");
                candidates.Add(candidate);
                Click(reloadWindows);
                Check(entries.Items.Count == 1 && Equals(entries.Items[0],candidate),
                    "Refreshing the target picker failed to rediscover windows");
                Layout(picker,700,420);
                var item = (ListBoxItem)entries.ItemContainerGenerator.ContainerFromItem(candidate);
                void DoubleClick(UIElement element, MouseButton button) => element.RaiseEvent(
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) { RoutedEvent = Control.MouseDoubleClickEvent });
                DoubleClick(entries, MouseButton.Left);
                DoubleClick(item, MouseButton.Right);
                Check(created.GameWindow is null, "Double-clicking outside a row or with the right button saved a target");
                DoubleClick(item, MouseButton.Left);
                Until(()=>Named<TextBlock>(picker,"TargetPickerStatus").Text=="대상 창을 저장했습니다.","Double-clicking a minimized game failed to save the target");
                var reload=store.LoadAsync();Complete(reload);var persisted=reload.Result.Profiles.Single(p=>p.Id==createdId);
                Check(persisted.GameWindow==candidate.Identity && persisted.CaptureMode==CaptureMode.Window,"Target identity/capture mode not saved by actual picker command");
                Check(persisted.Name=="게임 A · 保存" && ((ComboBox)window.FindName("InputMode")).SelectedIndex==0,"Picker lost name draft or failed window-mode selection");
                Check(persisted.CaptureArea==new PixelRect(120,140,400,180) && persisted.WindowCaptureRegion is null,"Unavailable game corrupted selected OCR area");
                Check(((TextBlock)window.FindName("TargetLabel")).Text.Contains("Game title")
                    && ((TextBlock)window.FindName("TargetLabel")).Text.Contains("게임 창을 복원"),"Saved minimized target was presented as unset or already capturable");
                savedIdentity=persisted.GameWindow;
            } finally {Close(picker);}
            Click(Button(window,"설정 저장"));var saved=store.LoadAsync();Complete(saved);
            Check(saved.Result.Profiles.Single(p=>p.Id==createdId).GameWindow==savedIdentity,"Settings Save erased selected game");
        } finally {Close(window);}
        var secondStore=new SettingsStore(dir);var loaded=secondStore.LoadAsync();Complete(loaded);
        var restored=loaded.Result;var game=restored.Profiles.Single(p=>p.Id==createdId);candidates.Clear();
        var restarted=Track(new MainWindow(secondStore,new ApiKeyStore(dir),()=>candidates,Bounds));restarted.ApplyLoadedSettings(restored);
        try {
            var label=(TextBlock)restarted.FindName("TargetLabel");
            Check(restored.ActiveProfileId==createdId && game.GameWindow==savedIdentity,"Fresh settings load reset new profile target");
            Check(restarted.ConnectedTarget is null && label.Text.Contains("Game title") && label.Text.Contains("연결 대기"),"Unavailable saved target looked reset instead of waiting");
            candidates.Add(candidate with {Title="Another game in same emulator"});restarted.ReconnectTarget();
            Check(restarted.ConnectedTarget==candidates[0] && label.Text.Contains("Game title") && game.GameWindow==savedIdentity,"Stable executable/class target failed to reconnect after a scene title change");
            candidates.Clear();candidates.Add(candidate);candidates.Add(candidate with {Handle=new IntPtr(201),ProcessId=201});restarted.ReconnectTarget();
            Check(restarted.ConnectedTarget is null && label.Text.Contains("Game title") && label.Text.Contains("여러"),"Ambiguous target was picked or saved name disappeared");
            candidates.RemoveAt(1);minimized=false;restarted.ReconnectTarget();
            Check(restarted.ConnectedTarget==candidate && game.GameWindow==savedIdentity,"Unique saved target did not reconnect");
            var name=(TextBox)restarted.FindName("ProfileName");name.Text="설정 저장으로 변경";Click(Button(restarted,"설정 저장"));
            var renamed=secondStore.LoadAsync();Complete(renamed);
            Check(game.Name==name.Text && renamed.Result.Profiles.Single(p=>p.Id==createdId).Name==name.Text,"Settings Save ignored edited profile name");
            Check(((ComboBox)restarted.FindName("Profiles")).SelectedItem is Profile {Name:"설정 저장으로 변경"} && game.GameWindow==savedIdentity,"Name save lost selected target/list label");
            name.Text="이름 변경으로 변경";Click(Button(restarted,"이름 변경"));
            var renamedAgain=secondStore.LoadAsync();Complete(renamedAgain);
            Check(renamedAgain.Result.Profiles.Single(p=>p.Id==createdId).Name=="이름 변경으로 변경" && renamedAgain.Result.Profiles.Single(p=>p.Id==createdId).GameWindow==savedIdentity,"Rename command differs from Settings Save or erases target");
            name.Text="전환 전 편집한 이름";Complete(restarted.SwitchProfileAsync(restored.DefaultProfile));Complete(restarted.SwitchProfileAsync(game));
            Check(name.Text=="전환 전 편집한 이름" && game.GameWindow==savedIdentity && restarted.ConnectedTarget==candidate,"Profile roundtrip lost unsaved name or chosen game");
            name.Text="  ";Click(Button(restarted,"설정 저장"));
            Check(game.Name=="전환 전 편집한 이름" && ((MainViewModel)restarted.DataContext).Status.Contains("1–80"),"Empty name was silently saved");
            ((ComboBox)restarted.FindName("Profiles")).SelectedItem=restored.DefaultProfile;Pump();
            Check(restored.ActiveProfileId==game.Id && ((ComboBox)restarted.FindName("Profiles")).SelectedItem==game,"Invalid name draft was discarded by switching profiles");
            name.Text=game.Name;
            var stalePicker=Track(restarted.CreateTargetPicker());
            try {
                Named<ListBox>(stalePicker,"TargetCandidates").SelectedItem=candidate;
                Complete(restarted.SwitchProfileAsync(restored.DefaultProfile));Click(Button(stalePicker,"선택"));
                Check(restored.DefaultProfile.GameWindow is null && Named<TextBlock>(stalePicker,"TargetPickerStatus").Text.Contains("프로필이 변경"),"Stale picker wrote a game into another profile");
            } finally {Close(stalePicker);}
            Complete(restarted.SwitchProfileAsync(game));name.Text="종료 시 저장한 이름";
        } finally {Close(restarted);}
        try {
            var finalLoad=new SettingsStore(dir).LoadAsync();Complete(finalLoad);
            Check(finalLoad.Result.Profiles.Single(p=>p.Id==createdId).Name=="종료 시 저장한 이름" && finalLoad.Result.Profiles.Single(p=>p.Id==createdId).GameWindow==savedIdentity,"Close/restart lost name or selected target");
        } finally {Directory.Delete(dir,true);}
    }
    private sealed class RecordingOcr : IOcrService
    {
        internal int Requests; internal OcrOptions? Last;
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token) { Last=options; Requests++; return Task.FromResult(new OcrResult([new("synthetic complete dialogue number " + Requests + ".",1,new(10,10,600,100))],TimeSpan.Zero)); }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class RecordingTranslation : ITranslationService
    {
        internal int Requests; internal TranslationRequest? Last;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken token) { Requests++; Last=request; return Task.FromResult(new TranslationResult("synthetic translation",TimeSpan.Zero,0)); }
    }
}
