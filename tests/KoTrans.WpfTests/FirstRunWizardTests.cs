using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class FirstRunWizardTests
{
    internal static void Controller()
    {
        var profile=Guid.NewGuid();var state=new FirstRunReadiness(profile,true,true,true,true,false,"config","ready");
        FirstRunWizardController Prepared() {
            var c=new FirstRunWizardController();c.Refresh(state);
            for(int i=0;i<4;i++)Check(c.Next(state),"A completed setup step forced reconfiguration");
            Check(c.Step==FirstRunStep.Start,"Wizard skipped or lost a setup step");return c;
        }
        var controller=Prepared();var session=new object();
        controller.Arm(profile);controller.Bind(session,profile,5);controller.Started(true);
        Check(controller.Waiting && !controller.Completed,"Starting alone completed the wizard");
        var final=new PipelineUpdate(6,PipelineState.Watching,"done","synthetic source","합성 번역",OcrRequests:1,TranslationRequests:1);
        foreach(var bad in new[]{final with {State=PipelineState.Translating},final with {State=PipelineState.Error},
            final with {Original=null},final with {Translation=""},final with {OcrRequests=0},final with {Version=5}}) {
            controller.SubtitleApplied(session,profile,bad,true,false);Check(!controller.Completed,"Partial, empty, non-OCR or old result completed wizard");
        }
        controller.SubtitleApplied(session,profile,final,false,false);Check(!controller.Completed,"Hidden subtitle completed wizard");
        controller.SubtitleApplied(session,profile,final,true,true);Check(!controller.Completed,"Mock translation completed wizard");
        controller.SubtitleApplied(new object(),profile,final,true,false);Check(!controller.Completed,"Previous pipeline completed wizard");
        controller.SubtitleApplied(session,Guid.NewGuid(),final,true,false);Check(!controller.Completed,"Previous profile completed wizard");
        controller.SubtitleApplied(session,profile,final with {CacheHits=1,HttpAttempts=0},true,false);
        Check(controller.Completed,"A valid displayed cached translation was rejected");
        controller.Refresh(state with {Running=false});
        Check(controller.Completed && controller.Step==FirstRunStep.FirstSubtitle
            && controller.Message=="설정이 완료되었습니다!",
            "Stopping translation revoked a real, already verified first subtitle");
        controller.Refresh(state with {Running=false,GameReady=false,AreaReady=false});
        Check(controller.Completed && controller.Step==FirstRunStep.FirstSubtitle,
            "Temporarily losing the game window revoked an already completed wizard");
        controller.Refresh(state with {Running=true,Configuration="changed"});
        Check(!controller.Completed && controller.Step==FirstRunStep.Start,"Configuration change retained completion");
        controller.SubtitleApplied(session,profile,final,true,false);Check(!controller.Completed,"Late result survived a configuration change");
        controller.Dispose();Check(!controller.Next(state),"Disposed controller advanced");

        using var early=Prepared();early.Arm(profile);early.Bind(session,profile);
        early.SubtitleApplied(session,profile,final,true,false);Check(!early.Completed,"Pending startup claimed completion");
        early.Started(true,"startup-normalized-region");
        early.Refresh(state with {Running=true,Configuration="startup-normalized-region"});
        Check(early.Completed,"First subtitle arriving before Start returned was lost");
        early.Back();Check(!early.Completed && early.Step==FirstRunStep.Start,"Back retained previous completion");
        early.Arm(profile);early.Bind(session,profile);early.Started(false);
        Check(!early.Waiting && !early.Completed && early.Message.Contains("시작하지"),"Failed start retained pending success");
        early.ReportError("synthetic failure");early.Refresh(state with {Configuration="startup-normalized-region"});
        Check(early.Message=="synthetic failure","Polling erased an actionable failure");
        early.ClearError();early.Refresh(state with {Configuration="startup-normalized-region"});Check(early.Message=="ready","Retry did not clear failure");

        using var alerted=Prepared();
        alerted.Arm(profile);alerted.Bind(session,profile);alerted.Started(true);
        alerted.ReportPipelineIssue("번역 API 실패",PipelineStatusArea.Translation);
        alerted.Refresh(state with {Running=true});
        Check(alerted.HasError && alerted.Message=="번역 API 실패",
            "Polling erased an unresolved pipeline failure");
        alerted.RecoverPipelineAreas(PipelineStatusArea.Capture | PipelineStatusArea.Ocr);
        Check(alerted.HasError,"Successful OCR incorrectly cleared a failed AI translation");
        alerted.RecoverPipelineAreas(PipelineStatusArea.Translation);
        alerted.Refresh(state with {Running=true});
        Check(!alerted.HasError && alerted.Message=="ready",
            "Confirmed translation recovery did not clear its own failure");
        alerted.ReportPipelineIssue("캡처 일시 대기",PipelineStatusArea.Capture);
        alerted.RecoverPipelineAreas(PipelineStatusArea.Translation);
        Check(alerted.HasError,"Unrelated translation recovery erased a capture warning");
        alerted.RecoverPipelineAreas(PipelineStatusArea.Capture);
        Check(!alerted.HasError,"Recovered capture never cleared its warning");

        using var transient=Prepared();
        transient.ReportRefreshIssue("상태 조회 오류");
        Check(transient.HasError && transient.Message=="상태 조회 오류",
            "A readiness query error was not shown");
        transient.Refresh(state);
        Check(!transient.HasError && transient.Message=="ready",
            "A successful readiness query did not clear its own transient error");
        transient.ReportPipelineIssue("실제 캡처 문제",PipelineStatusArea.Capture);
        transient.ReportRefreshIssue("상태 조회 오류");
        transient.Refresh(state);
        Check(transient.HasError && transient.Message=="실제 캡처 문제",
            "A transient readiness failure masked or cleared an unresolved capture problem");

        const string gdi="캡처 일시 대기 · 대상 창을 맨 앞으로 가져오세요. GDI 창 캡처는 대상 창이 활성화된 동안만 진행합니다.";
        const string friendly="게임 창을 앞으로 가져와 주세요. 활성화되면 인식을 계속합니다.";
        Check(FirstRunWizardWindow.FriendlyWizardStatus(FirstRunStep.Start,gdi)==friendly
            && FirstRunWizardWindow.FriendlyWizardStatus(FirstRunStep.FirstSubtitle,gdi)==friendly
            && FirstRunWizardWindow.FriendlyWizardStatus(FirstRunStep.Area,gdi)==gdi,
            "Wizard 5-6 did not replace technical GDI capture guidance only in the relevant steps");
        Check(FirstRunWizardWindow.FriendlyWizardStatus(FirstRunStep.FirstSubtitle,"대사 대기 중",
                mockAi:true).Contains("모의 AI 모드")
            && FirstRunWizardWindow.FriendlyWizardStatus(FirstRunStep.FirstSubtitle,"기본 오류",
                mockAi:true,hasError:true)=="기본 오류"
            && FirstRunWizardWindow.FriendlyWizardStatus(FirstRunStep.FirstSubtitle,"설정이 완료되었습니다!",
                mockAi:true,completed:true)=="설정이 완료되었습니다!",
            "Mock AI guidance hid actionable errors or a previously completed milestone");

        using var stopped=Prepared();stopped.Arm(profile);stopped.Bind(session,profile);stopped.Started(true);
        stopped.Refresh(state);Check(!stopped.Waiting && stopped.Step==FirstRunStep.Start,"Stop was not observed");
        stopped.Refresh(state with {GameReady=false});Check(stopped.Step==FirstRunStep.Game,"Lost game did not return to connection step");
        stopped.Refresh(state with {ProfileId=Guid.NewGuid(),ProfileReady=false});Check(stopped.Step==FirstRunStep.Profile,"Profile switch kept another game's progress");
    }
    internal static void PlacementRestoration()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.WizardPlacement",Guid.NewGuid().ToString("N"));
        var store=new SettingsStore(root);var data=new AppSettings();data.Validate();
        data.FirstRunWizardWindow=new() {X=-1350,Y=180,Width=540,Height=610,MonitorId="synthetic-secondary",
            MonitorDevice="DISPLAY2",MonitorWorkX=-1600,MonitorWorkY=0};
        var first=Track(new MainWindow(store,new ApiKeyStore(root),()=>[]));first.ApplyLoadedSettings(data);
        var wizard=Track(first.OpenFirstRunWizard(false));
        try {
            Check(wizard.Width==540 && wizard.Height==610 && !wizard.IsVisible,
                "Saved wizard dimensions were ignored before the first display");
            first.RememberWizardPlacement(new() {X=-1220,Y=210,Width=560,Height=640,
                MonitorId="synthetic-secondary",MonitorDevice="DISPLAY2",MonitorWorkX=-1600,MonitorWorkY=0});
            Complete(first.WizardPlacementSave);
            var loaded=store.LoadAsync();Complete(loaded);
            var placement=loaded.Result.FirstRunWizardWindow;
            Check(placement.X==-1220 && placement.Y==210 && placement.Width==560 && placement.Height==640
                && placement.MonitorId=="synthetic-secondary" && placement.MonitorDevice=="DISPLAY2"
                && placement.MonitorWorkX==-1600 && placement.MonitorWorkY==0,
                "Wizard position, size or secondary-monitor identity was not saved");
            var second=Track(new MainWindow(store,new ApiKeyStore(root),()=>[]));second.ApplyLoadedSettings(loaded.Result);
            var reopened=Track(second.OpenFirstRunWizard(false));
            try {Check(reopened.Width==560 && reopened.Height==640,
                "Reopened wizard did not use the saved dimensions");}
            finally {Close(reopened);Close(second);}
        } finally {Close(wizard);Close(first);Directory.Delete(root,true);}
    }
    internal static void Ui()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.Wizard",Guid.NewGuid().ToString("N"));
        var settings=new AppSettings();settings.Validate();settings.DefaultProfile.Translation=new() {Provider=TranslationProvider.Ollama,Model="synthetic"};
        var a=new WindowTarget(new(70),70,70,"Synthetic game","game","C:\\Synthetic\\game.exe","GameClass");
        var b=new WindowTarget(new(71),71,71,"Another game","other","C:\\Synthetic\\other.exe","OtherClass");
        IReadOnlyList<WindowTarget> windows=[a];bool minimized=false;int searches=0,launches=0;
        var store=new SettingsStore(root);var vault=new ApiKeyStore(root);
        var localRuntime = new LocalModelRuntime(worker: Path.Combine(root, "unused-worker.exe"));
        var main=Track(new MainWindow(store,vault,()=>{Interlocked.Increment(ref searches);return windows;},
            _=>minimized?throw new CaptureUnavailableException("restore game",CaptureWaitReason.HiddenOrMinimized):new(50,70,800,600),
            launchGame:(_,_)=>{launches++;return Task.FromResult(GameLaunchOutcome.Requested);}));
        main.ApplyLoadedSettings(settings);
        FirstRunWizardWindow? wizard=null;TranslationPipeline? active=null;TranslationWindow? overlay=null;
        try {
            Check(main.FirstRunWizard is null && searches==0,"Wizard added monitoring before opening");
            var entry=Named<Button>(main,"FirstRunWizardButton");
            Check(entry.ToolTip.ToString()!.Contains("게임 설정"),"Missing first-run help tooltip");
            foreach(double width in new[]{820d,540d,360d}) {
                main.ApplyAvailableWindowSize(new(width,900));Layout(main,width,900);
                var actions=Named<WrapPanel>(main,"LauncherPrimaryActions");var hide=Named<Button>(main,"HideSettingsButton");
                Check(entry.ActualWidth>90 && entry.ActualHeight>=38 && hide.ActualWidth>0,"Launcher lost help/hide actions");
                var box=entry.TransformToAncestor(actions).TransformBounds(new Rect(entry.RenderSize));
                Check(box.Left>=0 && box.Right<=actions.ActualWidth+1,"Narrow launcher clips first-run button");
            }
            wizard=Track(main.OpenFirstRunWizard(false));Complete(wizard.RefreshAsync());
            Check(ReferenceEquals(wizard,main.OpenFirstRunWizard(false)) && !wizard.IsMonitoring,"Duplicate wizard or hidden monitoring");
            Check(wizard.ProfileChoice.SelectedItem==settings.DefaultProfile && wizard.ProfileName.IsReadOnly
                && wizard.ProfileName.Text==settings.DefaultProfile.Name && Ready(main,FirstRunStep.Profile).ProfileReady,
                "Default profile was blank, editable or unusable");
            Check(Named<Button>(wizard,"WizardRetry").Visibility==Visibility.Visible
                && Equals(Named<Button>(wizard,"WizardRetry").Content,"기본 정보 보기")
                && !Elements<ProgressBar>(wizard).Any()
                && Elements<TextBlock>(wizard).Any(t=>t.Text=="게임 프로필 이름")
                && Named<TextBlock>(wizard,"WizardDescription").Text.Contains("이름과 원문 언어")
                && Named<TextBlock>(wizard,"WizardStatus").Text.Contains("저장 후 다음"),
                "Wizard first step must provide basic-info navigation without duplicate progress");
            main.ShowLauncher();main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardRetry");
            Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==0
                && Named<ScrollViewer>(main,"BasicPage").Visibility==Visibility.Visible && wizard.Controller.Step==FirstRunStep.Profile,
                "Profile presentation did not restore Basic Information or unexpectedly advance the wizard");
            wizard.SetCaptureExclusion(false);Check(Named<TextBlock>(wizard,"WizardCaptureWarning").Text.Contains("최소화"),"Missing capture exclusion fallback guidance");
            wizard.SetCaptureExclusion(true);Check(Named<TextBlock>(wizard,"WizardCaptureWarning").Text=="","Capture warning remained after exclusion succeeded");
            Layout(wizard,wizard.Width-16,wizard.Height-38);
            var footerButtons = new[] { wizard.Next, wizard.Previous, Named<Button>(wizard, "WizardRetry"), Named<Button>(wizard, "WizardCancel") };
            var footer = (UIElement)wizard.Next.Parent;
            Check(footerButtons.All(button => button.ActualHeight == wizard.Next.ActualHeight
                && Math.Abs(button.TranslatePoint(new Point(), footer).Y - wizard.Next.TranslatePoint(new Point(), footer).Y) < .1)
                && wizard.Next.ActualWidth > wizard.Previous.ActualWidth,
                "Default wizard actions must fit one row with content-sized widths and matching heights");
            var initialScroll=Named<ScrollViewer>(wizard,"WizardBody");
            Check(initialScroll.ScrollableHeight<.5 && initialScroll.ComputedVerticalScrollBarVisibility!=Visibility.Visible,
                $"Initial wizard needs scrolling: extent={initialScroll.ExtentHeight}, viewport={initialScroll.ViewportHeight}");
            Click(wizard,"WizardNext");Check(wizard.Controller.Step==FirstRunStep.Game && settings.ActiveProfileId==settings.DefaultProfileId
                && Named<Button>(wizard,"WizardRetry").Visibility==Visibility.Visible,
                "Default profile could not advance or the game search retry button stayed hidden");
            wizard.Previous.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Complete(wizard.PendingAction);
            foreach(var size in new[]{new Size(490,595),new Size(340,520),new Size(300,380)}) {
                wizard.ApplyAvailableSize(size);Layout(wizard,size.Width,size.Height);
                var actions = new[] { wizard.Next, wizard.Previous, Named<Button>(wizard, "WizardRetry"), Named<Button>(wizard, "WizardCancel") };
                var wizardContent = (UIElement)wizard.Content;
                Check(wizard.Next.ActualWidth > 0 && actions.All(button =>
                {
                    var origin = button.TranslatePoint(new Point(), wizardContent);
                    return button.ActualHeight == wizard.Next.ActualHeight
                        && origin.X >= 0 && origin.X + button.ActualWidth <= size.Width
                        && origin.Y >= 0 && origin.Y + button.ActualHeight <= size.Height;
                }), "Narrow wizard action heights differ or overflow the window");
            }
            main.ShowLauncher();main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardCreateProfile");
            var game=settings.Profiles.Single(p=>p.Id==settings.ActiveProfileId);
            Check(game.Id!=settings.DefaultProfileId && game.Name=="새 게임 프로필" && game.Language==settings.DefaultProfile.Language,
                "Wizard creation did not use the existing New action");
            Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==0
                && Named<ScrollViewer>(main,"BasicPage").Visibility==Visibility.Visible,
                "Creation did not restore the main Basic Information page");
            Check(wizard.Controller.Step==FirstRunStep.Profile && wizard.ProfileChoice.SelectedItem==game,
                "New profile advanced automatically or failed to select the created profile");
            Check(game.GameWindow is null && !game.CaptureAreaConfigured && game.WindowCaptureRegion is null,"Copied profile retained another game's capture");
            Named<TextBox>(main,"ProfileName").Text="Edited in main";Named<ComboBox>(main,"SourceLanguage").SelectedIndex=1;
            Complete(wizard.RefreshAsync());
            Check(wizard.ProfileName.Text=="Edited in main" && Equals(wizard.SourceLanguageChoice.SelectedItem,"일본어"),
                "Wizard did not reflect unsaved Basic Information edits");
            // Edit once more after the last poll: Next must save the main form,
            // not overwrite it with the now-stale wizard fields.
            Named<TextBox>(main,"ProfileName").Text="New synthetic game";
            Click(wizard,"WizardNext");
            Check(game.Name=="New synthetic game" && game.Language=="일본어","Next overwrote main profile edits");
            Check(wizard.Controller.Step==FirstRunStep.Game && game.GameWindow is null && wizard.GameChoice.SelectedItem is WindowTarget,
                "Single candidate was silently connected or not offered");
            wizard.Previous.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Complete(wizard.PendingAction);
            wizard.ProfileName.Text="Wizard draft name";
            Named<ComboBox>(main,"SourceLanguage").SelectedIndex=0;Complete(wizard.RefreshAsync());
            Check(wizard.ProfileName.Text=="Wizard draft name" && Equals(wizard.SourceLanguageChoice.SelectedItem,"영어"),
                "Polling lost wizard name edits or failed to sync the main language");
            main.ShowLauncher();main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardRetry");
            Check(main.WindowState==WindowState.Normal
                && Named<ListBox>(main,"ProfilePages").SelectedIndex==0
                && wizard.Controller.Step==FirstRunStep.Profile
                && ReferenceEquals(wizard.ProfileChoice.SelectedItem,game)
                && settings.ActiveProfileId==game.Id
                && wizard.ProfileName.Text=="Wizard draft name"
                && Equals(wizard.SourceLanguageChoice.SelectedItem,"영어")
                && game.Name!="Wizard draft name",
                "Opening Basic Information lost an unsaved wizard draft, changed profile or saved it implicitly");
            Click(wizard,"WizardNext");
            Check(game.Name=="Wizard draft name" && game.Language=="영어","Wizard and main field edits were not combined");
            main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardNext");Check(game.GameWindow?.ExecutablePath==a.ExecutablePath && wizard.Controller.Step==FirstRunStep.Area,"Game confirmation did not connect/save");
            Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==1,
                "Entering the area step did not present capture settings");
            var areaStatus=Named<TextBlock>(wizard,"WizardStatus");
            Check(Equals(Named<Button>(wizard,"WizardPickArea").Content,"대사 영역 지정")
                && Equals(Named<Button>(wizard,"WizardWholeGame").Content,"게임 창 전체를 인식 영역으로 사용")
                && Equals(Named<Button>(wizard,"WizardRetry").Content,"영역 설정 보기")
                && Named<TextBlock>(wizard,"WizardDescription").Text.Contains("기존 영역")
                && areaStatus.Visibility==Visibility.Collapsed && !wizard.Next.IsEnabled,
                "Area step must have concise actions and hide only the repeated neutral prompt");
            Check(!FirstRunWizardWindow.ShowAreaStatus("대사가 나오면 인식 영역을 지정하세요.",false)
                && FirstRunWizardWindow.ShowAreaStatus("대사가 나오면 인식 영역을 지정하세요.",true)
                && FirstRunWizardWindow.ShowAreaStatus("인식 영역이 설정되었습니다. 다음 단계로 진행하세요.",false)
                && FirstRunWizardWindow.ShowAreaStatus("현재 게임 창에 맞게 인식 영역을 다시 확인하세요.",false),
                "Area step hid important success, reconfirmation or error feedback");
            var canceledFeedback=FirstRunWizardWindow.AreaActionFailure(
                "영역 편집을 취소했습니다. 저장된 영역을 유지합니다.","기존 안내");
            var failedFeedback=FirstRunWizardWindow.AreaActionFailure(
                "게임 창 크기가 바뀌었습니다. 영역을 다시 지정하세요.","기존 안내");
            Check(canceledFeedback.Canceled && canceledFeedback.Message.Contains("기존 영역")
                && !failedFeedback.Canceled && failedFeedback.Message.Contains("게임 창 크기"),
                "Cancel must be nonfatal and a specific region error must remain visible");
            var oldArea=game.CaptureArea;var cancelArea=main.ConfirmCaptureAreaAsync(null,game.Id);Complete(cancelArea);
            Check(!cancelArea.Result && game.CaptureArea==oldArea && !game.CaptureAreaConfigured,"Cancel overwrote capture region");
            main.ShowLauncher();main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardWholeGame");Check(game.WindowCaptureRegion==new PixelRect(0,0,800,600),"Whole window did not use existing region storage");
            Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==1,
                "Whole-window action did not restore the main capture settings");
            Check(Ready(main,FirstRunStep.Area).AreaReady && areaStatus.Visibility==Visibility.Visible
                && areaStatus.Text=="인식 영역이 설정되었습니다. 다음 단계로 진행하세요."
                && wizard.Next.IsEnabled,"Successful area selection was not acknowledged or Next stayed disabled");
            wizard.RequiresAreaConfirmation=true;Complete(wizard.RefreshAsync());
            Check(!wizard.Next.IsEnabled && areaStatus.Visibility==Visibility.Visible
                && areaStatus.Text.Contains("다시 확인"),"Changed game/window did not require a new area confirmation");
            wizard.RequiresAreaConfirmation=false;Complete(wizard.RefreshAsync());
            main.ShowLauncher();main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardRetry");
            Check(main.WindowState==WindowState.Normal
                && Named<ListBox>(main,"ProfilePages").SelectedIndex==1
                && Equals(Named<Button>(wizard,"WizardRetry").Content,"영역 설정 보기"),
                "Area footer did not navigate to the capture-area settings");
            Click(wizard,"WizardNext");Check(wizard.Controller.Step==FirstRunStep.Service,"Confirmed area did not advance");
            var state=Ready(main,FirstRunStep.Service);Check(state.ServiceReady && state.Message.Contains("실행"),"Ollama falsely claimed a live connection or lost configuration readiness");
            var provider = Named<ComboBox>(main, "Provider");
            var apiSettings = Named<Button>(wizard, "WizardApiSettings");
            var selectedService = Named<TextBlock>(wizard, "WizardService");
            var serviceStatus = Named<TextBlock>(wizard, "WizardStatus");
            Check(Equals(Named<Button>(wizard,"WizardRetry").Content,"번역 설정 보기")
                && !Elements<Button>(wizard).Any(b=>b.Name=="WizardProviderSettings")
                && Named<TextBlock>(wizard,"WizardDescription").Text.Contains("실제 번역을 요청하지 않습니다.")
                && wizard.Next.IsEnabled && serviceStatus.Text.StartsWith("설정 준비 완료")
                && !serviceStatus.Text.StartsWith("Ollama · ")
                && apiSettings.Visibility == Visibility.Collapsed
                && selectedService.Text.Contains("Ollama") && selectedService.Text.Contains("서버 주소"),
                "Service step duplicated settings actions/provider status or disabled ready Ollama");
            main.ShowLauncher();main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardRetry");
            Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==2
                && settings.ActiveProfileId==game.Id && wizard.Controller.Step==FirstRunStep.Service,
                "Footer translation-settings action failed to reveal the game's provider controls");
            byte[] bytes = "GGUF wizard status fixture"u8.ToArray();
            var spec = new LocalModelSpec("fixture", "test.gguf", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new("https://example.invalid/test.gguf"));
            var installed = new LocalModelInstaller(Path.Combine(root, "installed"), spec);
            var missing = new LocalModelInstaller(Path.Combine(root, "missing"), spec);
            Directory.CreateDirectory(installed.DirectoryPath);
            File.WriteAllBytes(installed.ModelPath, bytes);
            main.LocalPreparationFactoryOverride = model => new(model == LocalTranslationModel.Standard7B ? installed : missing, localRuntime, model);
            provider.SelectedItem = TranslationProvider.HyMtLocal;
            var installModel = Named<Button>(wizard, "WizardInstallModel");
            Check(apiSettings.Visibility == Visibility.Collapsed && selectedService.Text.Contains("로컬 AI 번역")
                && installModel.Visibility == Visibility.Visible, "Unsaved local provider did not update the wizard");
            Until(() => serviceStatus.Text.Contains("설치되어 있습니다") && wizard.Next.IsEnabled,
                "Installed local model did not become ready for the next step");
            Check(!installModel.IsEnabled, "Installed model can be installed again");
            var localChoice = Named<ComboBox>(main, "LocalModelChoice");
            localChoice.SelectedValue = LocalTranslationModel.Lightweight18B;
            Until(() => installModel.IsEnabled && !wizard.Next.IsEnabled
                && serviceStatus.Text.Contains("설치"), "Missing local model did not disable Next or enable installation");
            localChoice.SelectedValue = LocalTranslationModel.Standard7B;
            Check(!installModel.IsEnabled, "Previous model state leaked into the installed model");
            Until(() => serviceStatus.Text.Contains("설치되어 있습니다") && wizard.Next.IsEnabled,
                "Switching back did not restore readiness or Next");
            provider.SelectedItem = TranslationProvider.Gemini;
            Check(apiSettings.Visibility == Visibility.Visible && selectedService.Text.Contains("Gemini"), "Unsaved Gemini provider did not update the wizard");
            provider.SelectedItem = TranslationProvider.OpenAI;
            Named<TextBox>(main, "Model").Text = "synthetic-model";
            Check(apiSettings.Visibility == Visibility.Visible && selectedService.Text.Contains("OpenAI")
                && game.Translation.Provider == TranslationProvider.Ollama, "Provider preview was stale or saved an uncommitted change");
            var missingKey = Ready(main,FirstRunStep.Service);
            Check(!missingKey.ServiceReady && !missingKey.Message.StartsWith("OpenAI · ")
                && missingKey.Message.Contains("API 키"),
                "Unsaved OpenAI selection did not report the missing API key without duplicate provider name");
            Complete(wizard.RefreshAsync());
            Until(() => !wizard.Next.IsEnabled && serviceStatus.Text.Contains("API 키"),
                "Missing API key did not disable Next and explain the required action");
            Click(wizard,"WizardRetry");
            Check(Named<ListBox>(main,"ProfilePages").SelectedIndex==2
                && settings.ActiveProfileId==game.Id && !wizard.Next.IsEnabled,
                "Footer settings action redirected to global keys or accidentally bypassed readiness");
            Click(wizard, "WizardApiSettings");
            Check(settings.ActiveProfileId==settings.DefaultProfileId && main.WizardProfile.Id==game.Id
                && wizard.Controller.Step==FirstRunStep.Service && Named<ListBox>(main, "ProfilePages").SelectedIndex == 7
                && selectedService.Text.Contains("OpenAI"),"Opening shared credentials lost the wizard's game profile or selection");
            Click(wizard, "WizardApiSettings");
            Check(main.WizardProfile.Id == game.Id, "Reopening API settings lost the return profile");
            Click(wizard, "WizardRetry");
            Check(settings.ActiveProfileId==game.Id && main.WizardProfile.Id==game.Id
                && Named<ListBox>(main,"ProfilePages").SelectedIndex==2,
                "Footer settings action did not return from global credentials to the game provider");
            Click(wizard, "WizardApiSettings");
            vault.Save(TranslationProvider.OpenAI,"synthetic-key");Complete(main.SaveWizardSettingsAsync(CancellationToken.None));
            Check(settings.ActiveProfileId==game.Id && Ready(main,FirstRunStep.Service).ServiceReady,"Registered credential did not return to game or reuse key");
            game.Translation.SwitchProvider(TranslationProvider.HyMtLocal);
            game.Translation.LocalModel = LocalTranslationModel.Lightweight18B;
            Check(!Ready(main,FirstRunStep.Service).ServiceReady,"Uninstalled local model was reported ready");
            game.Translation.SwitchProvider(TranslationProvider.Ollama);game.Translation.Model="synthetic";
            main.ApplyLoadedSettings(settings);Complete(wizard.RefreshAsync());
            Click(wizard,"WizardNext");Check(wizard.Controller.Step==FirstRunStep.Start,"Ready provider did not advance");
            var startStatus=Ready(main,FirstRunStep.Start);
            Check(startStatus.Message.StartsWith("번역 서비스: Ollama ·")
                && startStatus.Message.Contains("서버 실행 여부")
                && !startStatus.Message.Contains("설정 준비 완료")
                && Equals(wizard.Next.Content,"번역 시작")
                && Equals(Named<Button>(wizard,"WizardRetry").Content,"메인 화면 보기")
                && Equals(Named<Button>(wizard,"WizardReviewSettings").Content,"인식 방식·트리거 설정 보기 (선택)")
                && Named<TextBlock>(wizard,"WizardDescription").Text.Contains("아래 ‘번역 시작’")
                && Named<TextBlock>(wizard,"WizardDescription").Text.Contains("게임 창을 앞으로"),
                "Step 5 must distinguish optional recognition settings from the main wizard start action");
            Click(wizard,"WizardReviewSettings");
            Check(Named<ListBox>(main,"ProfilePages").SelectedIndex==4
                && Named<Grid>(main,"ProfileSettingsView").Visibility==Visibility.Visible
                && wizard.Controller.Step==FirstRunStep.Start,
                "Optional recognition-settings button did not show the correct profile page");
            Click(wizard,"WizardRetry");
            Check(Named<Grid>(main,"LauncherView").Visibility==Visibility.Visible
                && wizard.Controller.Step==FirstRunStep.Start
                && Equals(Named<Button>(wizard,"WizardRetry").Content,"메인 화면 보기"),
                "Step 5 footer failed to restore the main launch view or changed wizard progress");
            overlay=Track(main.CreateOverlayWindow());Track(overlay.Chrome);
            var final=new PipelineUpdate(1,PipelineState.Watching,"done","source","합성 번역",OcrRequests:1);
            overlay.SetPipelineResult(final);Check(overlay.MatchesFinalSubtitle(final),"Valid final subtitle did not match actual controls");
            overlay.SetPipelineResult(final with {State=PipelineState.Translating,Translation="부분"});
            Check(!overlay.MatchesFinalSubtitle(final),"Partial control content matched old complete subtitle");
            overlay.SetPipelineResult(final);game.ShowTranslation=false;overlay.Apply(game);
            Check(!overlay.MatchesFinalSubtitle(final),"Hidden Korean control counted as displayed");
            game.ShowTranslation=true;overlay.Apply(game);
            overlay.WindowState=WindowState.Minimized;Check(!overlay.MatchesFinalSubtitle(final),"Minimized subtitle counted as displayed");overlay.WindowState=WindowState.Normal;
            active=main.CreateProfilePipeline(new NoOcr(),new MockTranslationService(),_=>throw new Exception("No actual capture expected"));
            main.ObserveTranslation(active,game.Id,watch:true);active.Start(false);
            main.WindowState=WindowState.Minimized;
            Complete(main.StartMainTranslationAsync(hideMainForStart:false));
            Check(wizard.Controller.Step==FirstRunStep.FirstSubtitle
                && wizard.Controller.Waiting && !wizard.Controller.Completed
                && active.IsRunning && main.WindowState==WindowState.Minimized,
                "A main-window/hotkey start failed to adopt the wizard or stole game focus");
            Check(Equals(wizard.Next.Content,"첫 자막 대기 중")
                && Equals(Named<Button>(wizard,"WizardRetry").Content,"상태 새로고침")
                && Equals(Named<Button>(wizard,"WizardCancel").Content,"마법사 닫기")
                && (Named<Button>(wizard,"WizardRetry").ToolTip as string)?.Contains("다시 시작하지") == true,
                "Step 6 failed to distinguish refresh, close and wait actions");
            const string captureWait="캡처 일시 대기 · 대상 창을 맨 앞으로 가져오세요. GDI 창 캡처는 대상 창이 활성화된 동안만 진행합니다.";
            wizard.Controller.ReportPipelineIssue(captureWait,PipelineStatusArea.Capture);
            wizard.Render();
            Click(wizard,"WizardRetry");
            Check(wizard.Controller.HasError && wizard.Controller.Message==captureWait
                && Named<TextBlock>(wizard,"WizardStatus").Text=="게임 창을 앞으로 가져와 주세요. 활성화되면 인식을 계속합니다."
                && (Named<TextBlock>(wizard,"WizardStatus").ToolTip as string)==captureWait
                && wizard.Controller.Step==FirstRunStep.FirstSubtitle
                && main.WindowState==WindowState.Minimized,
                "Refreshing the waiting step cleared a real capture error or stole game focus");
            wizard.Controller.RecoverPipelineAreas(PipelineStatusArea.Capture);
            Complete(wizard.RefreshAsync());
            Check(!wizard.Controller.HasError && wizard.Controller.Step==FirstRunStep.FirstSubtitle,
                "The recovered capture error remained stuck in the wizard");
            var running=active;Close(wizard);wizard=null;
            Check(main.FirstRunWizard is null && running.IsRunning,"Cancel stopped existing translation");
            wizard=Track(main.OpenFirstRunWizard(false));Complete(wizard.RefreshAsync());
            Check(wizard.ProfileChoice.SelectedItem is Profile && game.WindowCaptureRegion is not null,"Reentry lost completed configuration");
            Complete(main.SwitchProfileAsync(settings.DefaultProfile));Complete(wizard.RefreshAsync());
            Check(wizard.ProfileChoice.SelectedItem==settings.DefaultProfile && wizard.ProfileName.IsReadOnly
                && wizard.ProfileName.Text.Length>0 && wizard.Controller.Step==FirstRunStep.Profile,
                "Selecting default in main left the wizard blank");
            // The wizard may select a different profile before pressing Save/Next.
            // Basic Information must open that selected profile, not stale defaults.
            wizard.ProfileChoice.SelectedItem=game;
            wizard.ProfileName.Text="Uncommitted game title";
            wizard.SourceLanguageChoice.SelectedItem="일본어";
            main.ShowLauncher();main.WindowState=WindowState.Minimized;
            Click(wizard,"WizardRetry");
            Check(settings.ActiveProfileId==game.Id
                && wizard.Controller.Step==FirstRunStep.Profile
                && ReferenceEquals(wizard.ProfileChoice.SelectedItem,game)
                && main.WindowState==WindowState.Normal
                && Named<ListBox>(main,"ProfilePages").SelectedIndex==0
                && wizard.ProfileName.Text=="Uncommitted game title"
                && Equals(wizard.SourceLanguageChoice.SelectedItem,"일본어")
                && game.Name!="Uncommitted game title"
                && game.Language=="영어",
                "Viewing another profile lost uncommitted fields, saved them early or opened the wrong profile");
            minimized=true;Check(!Ready(main,FirstRunStep.Game).GameReady,"Minimized game was considered connected");minimized=false;
            windows=[a,b];Complete(wizard.RefreshAsync(true));
            var connect=main.ConnectWizardWindowAsync(b,windows,CancellationToken.None);Complete(connect);
            Check(connect.Result && wizard.RequiresAreaConfirmation && !Ready(main,FirstRunStep.Area).AreaReady,"Another game reused old area without confirmation");
            windows=[];game.GameWindow=null;main.ApplyLoadedSettings(settings);
            Check(!Ready(main,FirstRunStep.Game).GameReady,"Missing game was considered connected");
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            var search=main.FindWizardWindowsAsync(canceled.Token);
            try {Complete(search);throw new Exception("Canceled search succeeded");}catch(OperationCanceledException){Check(true,"Canceled search");}
            Check(launches==0,"Wizard launched a game without a user launch request");
            Check(File.Exists(Path.Combine(root,"settings.json")),"Wizard settings were not persisted");
        } finally {
            if(wizard is not null)Close(wizard);Close(main);
            if(overlay is not null){Close(overlay);Close(overlay.Chrome);}
            if(active is not null)Complete(active.DisposeAsync().AsTask());
            Complete(localRuntime.DisposeAsync().AsTask());
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    internal static void GameStepRefreshAndPick()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.WizardGameStep",Guid.NewGuid().ToString("N"));
        var data=new AppSettings();data.Validate();var game=data.CreateGameProfile("game");data.ActiveProfileId=game.Id;
        var a=new WindowTarget(new(80),80,80,"Candidate A","game-a","C:\\Synthetic\\a.exe","A");
        var b=new WindowTarget(new(81),81,81,"Clicked B","game-b","C:\\Synthetic\\b.exe","B");
        IReadOnlyList<WindowTarget> windows=[a,b];TaskCompletionSource<IReadOnlyList<WindowTarget>>? pending=null;
        int pendingEntered=0,selectionChanges=0,enabledChanges=0;
        var main=Track(new MainWindow(new SettingsStore(root),new ApiKeyStore(root),()=> {
            if(pending is {} gate) {Interlocked.Exchange(ref pendingEntered,1);return gate.Task.GetAwaiter().GetResult();}
            return windows.Select(w=>w with {}).ToArray();
        },_=>new(0,0,800,600)));main.ApplyLoadedSettings(data);
        var wizard=Track(main.OpenFirstRunWizard(false));
        try {
            Complete(wizard.RefreshAsync());
            var ready=Ready(main,FirstRunStep.Profile);wizard.Controller.Next(ready);wizard.Render();
            var stepStatus = Named<TextBlock>(wizard,"WizardStatus");
            Check(Equals(Named<Button>(wizard,"WizardRetry").Content,"캡처 설정 보기")
                && Elements<TextBlock>(wizard).Any(t=>t.Text=="연결할 게임 창")
                && Named<TextBlock>(wizard,"WizardDescription").Text.Contains("캡처 설정 보기"),
                "Game step labels must match the actual capture-settings navigation");
            Check(!FirstRunWizardWindow.ShowGameStatus("연결할 게임 창을 선택해 주세요.",false)
                && !FirstRunWizardWindow.ShowGameStatus("실행 중인 게임 창을 찾고 있습니다.",false)
                && FirstRunWizardWindow.ShowGameStatus("연결할 게임 창을 선택해 주세요.",true)
                && FirstRunWizardWindow.ShowGameStatus("연결됨: synthetic game",false),
                "Only duplicate neutral game guidance may be hidden; explicit errors and success remain visible");
            Check(Named<TextBlock>(wizard,"WizardGamePlaceholder").Visibility==Visibility.Visible
                && Named<TextBlock>(wizard,"WizardGamePlaceholder").Text.Length>0,"Initial game field was blank");
            windows=[];
            Complete(wizard.RefreshAsync(true));
            Check(stepStatus.Visibility==Visibility.Visible
                && stepStatus.Text.Contains("게임을 실행하거나")
                && !stepStatus.Text.Contains("다시 찾기"),
                "No-game result did not provide a useful action or referred to the renamed button");
            windows=[a,b];
            Complete(wizard.RefreshAsync(true));
            Check(wizard.GameChoice.SelectedItem is null && Named<TextBlock>(wizard,"WizardGamePlaceholder").Text.Contains("선택")
                && stepStatus.Visibility==Visibility.Collapsed,
                "Multiple candidates produced a blank field or duplicate status instruction");
            Check(!Elements<Button>(wizard).Any(button=>button.Name=="WizardLaunchGame"),"Wizard retained Game Launch button");
            main.WindowState=WindowState.Minimized;
            wizard.GameChoice.SelectedItem=wizard.GameChoice.Items.Cast<WindowTarget>().Single(w=>w.Handle==a.Handle);
            Check(stepStatus.Visibility==Visibility.Visible && stepStatus.Text.Contains("선택한 창 연결"),
                "Selected but unconnected game did not show actionable status");
            wizard.Controller.ReportError("연결할 게임 창을 선택해 주세요.");
            wizard.Render();
            Check(stepStatus.Visibility==Visibility.Visible && stepStatus.Text=="연결할 게임 창을 선택해 주세요.",
                "An explicit error with the same wording as a neutral hint was hidden");
            wizard.Controller.ClearError();
            Complete(wizard.RefreshAsync(true));
            var items=wizard.GameChoice.ItemsSource;var selected=wizard.GameChoice.SelectedItem;
            string message=Named<TextBlock>(wizard,"WizardStatus").Text;
            wizard.GameChoice.SelectionChanged+=(_,_)=>selectionChanges++;
            wizard.Next.IsEnabledChanged+=(_,_)=>enabledChanges++;
            for(int i=0;i<3;i++) {
                Complete(wizard.RefreshInBackgroundAsync(true));
                Check(ReferenceEquals(items,wizard.GameChoice.ItemsSource) && ReferenceEquals(selected,wizard.GameChoice.SelectedItem),
                    "Unchanged automatic search replaced items/selection");
                Check(Named<TextBlock>(wizard,"WizardStatus").Text==message && wizard.Next.IsEnabled,
                    "Automatic refresh alternated guidance or disabled buttons");
            }
            Check(selectionChanges==0 && enabledChanges==0,"Automatic refresh flickered selection/buttons");
            pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stale=wizard.RefreshInBackgroundAsync(true);Until(()=>Volatile.Read(ref pendingEntered)==1,"Background search did not start");
            Check(!stale.IsCompleted && wizard.Next.IsEnabled && Named<TextBlock>(wizard,"WizardStatus").Text==message,
                "Pending automatic search showed busy state or disabled controls");
            main.PickWizardWindowOverride=_=>Task.FromResult(new WindowClickPickResult(b,false));
            Click(wizard,"WizardPickGame");
            Check(wizard.GameChoice.SelectedItem is WindowTarget picked && picked.Handle==b.Handle
                && game.GameWindow?.ExecutablePath==b.ExecutablePath && Equals(wizard.Next.Content,"다음"),
                "Direct pick did not immediately select/connect the clicked game");
            Check(main.WindowState==WindowState.Minimized && Named<ListBox>(main,"ProfilePages").SelectedIndex==0,
                "Direct pick restored or navigated the main window");
            pending.SetResult([a]);Complete(stale);pending=null;
            Check(wizard.GameChoice.SelectedItem is WindowTarget kept && kept.Handle==b.Handle,"Stale search overwrote a direct pick");
            windows=[a];Complete(wizard.RefreshInBackgroundAsync(true));
            Check(wizard.GameChoice.SelectedItem is WindowTarget connected && connected.Handle==b.Handle,
                "A temporarily unlisted live connected window disappeared");
            string connectedMessage=Named<TextBlock>(wizard,"WizardStatus").Text;
            Complete(wizard.RefreshInBackgroundAsync());
            Check(Named<TextBlock>(wizard,"WizardStatus").Text==connectedMessage
                && stepStatus.Visibility==Visibility.Visible && connectedMessage.Contains(b.Title),
                "Connected guidance changed without a state change or was hidden");
            main.PickWizardWindowOverride=_=>Task.FromResult(new WindowClickPickResult(null,true));Click(wizard,"WizardPickGame");
            Check(game.GameWindow?.ExecutablePath==b.ExecutablePath && (wizard.GameChoice.SelectedItem as WindowTarget)?.Handle==b.Handle,
                "Cancel cleared the previous connection");
            Layout(main,1060,740);
            Check(Equals(Named<Button>(wizard,"WizardRetry").Content,"캡처 설정 보기"),
                "Capture settings action changed its caption before navigation");
            Click(wizard,"WizardRetry");Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==1
                && game.CaptureMode==CaptureMode.Window,"Capture settings action did not present game capture with Window input");
            CheckGameCaptureAtTop(main);
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            var cancelPick=WindowClickSelector.PickAsync(()=>throw new Exception("No desktop enumeration expected"),canceled.Token);
            try {Complete(cancelPick);throw new Exception("Canceled selector succeeded");}catch(OperationCanceledException){Check(true,"Canceled before native selector startup");}
        } finally {pending?.TrySetResult([]);Close(wizard);Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    internal static void StepPresentationAndDynamicScroll()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.WizardStepNavigation",Guid.NewGuid().ToString("N"));
        var data=new AppSettings();data.Validate();data.DefaultProfile.Translation=new() {Provider=TranslationProvider.Ollama,Model="synthetic"};
        var game=data.CreateGameProfile("navigation game");data.ActiveProfileId=game.Id;
        var target=new WindowTarget(new(90),90,90,"Navigation game","game","C:\\Synthetic\\game.exe","GameClass");
        var store=new SettingsStore(root);
        var main=Track(new MainWindow(store,new ApiKeyStore(root),()=>[target],_=>new(0,0,800,600)));main.ApplyLoadedSettings(data);
        var wizard=Track(main.OpenFirstRunWizard(false));
        void Present(FirstRunStep step) {
            var task=main.PresentWizardStepAsync(step,CancellationToken.None);
            Layout(main,1060,740);Complete(task);Check(task.Result,"Step presentation failed");Layout(main,1060,740);
        }
        void Within(FrameworkElement item,ScrollViewer viewer,string message) {
            var rect=item.TransformToAncestor(viewer).TransformBounds(new Rect(item.RenderSize));
            Check(viewer.ViewportHeight>0 && rect.Top>=-1 && rect.Bottom<=viewer.ViewportHeight+1,
                $"{message}: target={rect}, viewport={viewer.ViewportHeight}");
        }
        void CheckAlignment(FrameworkElement item, ScrollViewer viewer)
        {
            double top = item.TranslatePoint(new Point(0, 0), viewer).Y;
            Check(Math.Abs(top) <= 1 || (top > 0 && Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) <= 1),
                $"Wizard target was not at the top or the scroll limit: top={top}, offset={viewer.VerticalOffset}, limit={viewer.ScrollableHeight}");
            Within(item, viewer, "Wizard target was not visible after scrolling");
        }
        try {
            main.ShowLauncher();main.WindowState=WindowState.Minimized;wizard.StartMonitoring();Complete(wizard.PendingAction);
            Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==0
                && Named<ScrollViewer>(main,"BasicPage").Visibility==Visibility.Visible,"Initial step one did not open Basic Information");
            var basic = Named<ScrollViewer>(main,"BasicPage");
            ((StackPanel)basic.Content).Children.Insert(0, new Border { Height = 360 });
            Present(FirstRunStep.Profile);
            CheckAlignment(Named<FrameworkElement>(main,"ProfileNameGroup"), basic);
            var capture=Named<SmoothScrollViewer>(main,"CapturePage");
            var content=(StackPanel)capture.Content;var inserted=new Border {Height=360};content.Children.Insert(0,inserted);
            double previousOffset=-1;
            foreach(double height in new[]{360d,900d}) {
                inserted.Height=height;main.ShowLauncher();main.WindowState=WindowState.Minimized;
                Present(FirstRunStep.Game);
                Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==1
                    && game.CaptureMode==CaptureMode.Window && Named<ComboBox>(main,"InputMode").SelectedIndex==0,
                    "Game entry failed to restore capture settings and select Window input");
                CheckGameCaptureAtTop(main);
                if(previousOffset>=0)Check(capture.VerticalOffset>previousOffset,"Navigation reused a fixed offset after preceding content grew");
                previousOffset=capture.VerticalOffset;
            }
            main.SelectGameWindow(target);wizard.RequiresAreaConfirmation=true;
            Present(FirstRunStep.Area);
            CheckAlignment(Named<Button>(main,"SelectAreaButton"), capture);
            var confirm=main.ConfirmCaptureAreaAsync(new(20,30,400,160),game.Id);Complete(confirm);
            Check(confirm.Result && !wizard.RequiresAreaConfirmation && Ready(main,FirstRunStep.Area).AreaReady,
                "Region confirmed in the main UI was not accepted by the guide");
            var editor=Named<ContentControl>(main,"ProfileEditorHost");var editorScroll=Elements<SmoothScrollViewer>(editor).Single();
            ((StackPanel)editorScroll.Content).Children.Insert(0, new Border { Height = 360 });
            main.WindowState=WindowState.Minimized;Present(FirstRunStep.Service);
            Check(main.WindowState==WindowState.Normal && Named<ListBox>(main,"ProfilePages").SelectedIndex==2,"Service entry did not open translation settings");
            CheckAlignment(Elements<TextBlock>(editor).Single(t => t.Text == "번역 제공자"), editorScroll);
            Within(Named<ComboBox>(main,"Provider"),editorScroll,"Translation provider was not revealed");
            var launcher = Named<ScrollViewer>(main,"LauncherScroll");
            ((StackPanel)launcher.Content).Children.Insert(0, new Border { Height = 480 });
            main.WindowState=WindowState.Minimized;Present(FirstRunStep.Start);
            Check(main.WindowState==WindowState.Normal && Named<Grid>(main,"LauncherView").Visibility==Visibility.Visible,"Start entry did not show game translation card");
            CheckAlignment(Named<Button>(main,"WatchButton"), launcher);
            main.WindowState=WindowState.Minimized;Present(FirstRunStep.FirstSubtitle);
            Check(main.WindowState==WindowState.Minimized,"First subtitle confirmation activated main settings");
            var restored=store.LoadAsync();Complete(restored);
            Check(restored.Result.Profiles.Single(p=>p.Id==game.Id).CaptureMode==CaptureMode.Window,"Window input was not saved");
        } finally {Close(wizard);Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    internal static void LifetimeAndSaveFailure()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.WizardLifetime",Guid.NewGuid().ToString("N"));
        var data=new AppSettings();data.Validate();var game=data.CreateGameProfile("game");data.ActiveProfileId=game.Id;
        var found=new TaskCompletionSource<IReadOnlyList<WindowTarget>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered=0, finished=0;
        var main=Track(new MainWindow(new SettingsStore(root),new ApiKeyStore(root),()=> {
            Interlocked.Exchange(ref entered,1);
            try {return found.Task.GetAwaiter().GetResult();}finally {Interlocked.Exchange(ref finished,1);}
        }));main.ApplyLoadedSettings(data);
        var wizard=Track(main.OpenFirstRunWizard(false));
        try {
            wizard.StartMonitoring();Complete(wizard.PendingAction);Check(wizard.IsMonitoring,"Visible wizard did not start guidance monitoring");
            var state=Ready(main,FirstRunStep.Profile);wizard.Controller.Next(state);
            var search=wizard.RefreshAsync(true);
            Until(()=>Volatile.Read(ref entered)==1,"Window search did not start");
            Check(!search.IsCompleted,"Window discovery blocked or completed on the UI thread");
            bool dispatcherRan=false;main.Dispatcher.BeginInvoke(new Action(()=>dispatcherRan=true));Pump();
            Check(dispatcherRan && !search.IsCompleted,"Pending discovery blocked Dispatcher");
            Close(wizard);
            try {Complete(search);throw new Exception("Closed wizard accepted pending search");}catch(OperationCanceledException){Check(true,"Canceled closed search");}
            Check(main.FirstRunWizard is null && !wizard.IsMonitoring,"Closed wizard retained monitoring");
            found.SetResult([]);Until(()=>Volatile.Read(ref finished)==1,"Window search did not drain");
        } finally {found.TrySetResult([]);Close(wizard);Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}

        root=Path.Combine(Path.GetTempPath(),"KoTrans.WizardSave",Guid.NewGuid().ToString("N"));
        data=new AppSettings();data.Validate();
        bool failSave=true;
        main=Track(new MainWindow(new SettingsStore(root),new ApiKeyStore(root),()=>[],persistSettings:_=>failSave?Task.FromException(new IOException("synthetic save failure")):Task.CompletedTask));
        main.ApplyLoadedSettings(data);wizard=Track(main.OpenFirstRunWizard(false));
        try {
            Complete(wizard.RefreshAsync());wizard.ProfileName.Text="Unsaved game";wizard.SourceLanguageChoice.SelectedItem="영어";
            Click(wizard,"WizardCreateProfile");
            Check(data.Profiles.Count==1 && data.ActiveProfileId==data.DefaultProfileId && wizard.Controller.Step==FirstRunStep.Profile,
                "Failed creation switched profile or advanced the wizard");
            Check(wizard.Controller.Message.Contains("저장"),"Wizard did not explain save failure");
        } finally {failSave=false;Close(wizard);Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    internal static void AreaSaveFailure()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.WizardAreaFailure",Guid.NewGuid().ToString("N"));
        var data=new AppSettings();data.Validate();
        var game=data.CreateGameProfile("Synthetic area");
        data.ActiveProfileId=game.Id;game.CaptureMode=CaptureMode.Window;game.CaptureAreaConfigured=false;
        var target=new WindowTarget(new(90),90,90,"Synthetic area game","test","C:\\Synthetic\\area.exe","Synthetic");
        bool failSave=true;
        var main=Track(new MainWindow(new SettingsStore(root),new ApiKeyStore(root),()=>[target],
            _=>new(50,70,800,600),
            persistSettings:_=>failSave?Task.FromException(new IOException("Synthetic area-save failure")):Task.CompletedTask));
        main.ApplyLoadedSettings(data);main.SelectGameWindow(target);
        var wizard=Track(main.OpenFirstRunWizard(false));
        try
        {
            Complete(wizard.RefreshAsync());
            Check(wizard.Controller.Next(Ready(main,FirstRunStep.Profile))
                && wizard.Controller.Next(Ready(main,FirstRunStep.Game)),
                "Could not reach the area step with an existing live game");
            wizard.Render();Complete(wizard.RefreshAsync());
            Check(wizard.Controller.Step==FirstRunStep.Area && !wizard.Next.IsEnabled
                && !Ready(main,FirstRunStep.Area).AreaReady,
                "Area failure fixture began in a ready state");

            Click(wizard,"WizardWholeGame");
            var status=Named<TextBlock>(wizard,"WizardStatus");
            Check(wizard.Controller.Step==FirstRunStep.Area && wizard.RequiresAreaConfirmation
                && !wizard.Next.IsEnabled && !Ready(main,FirstRunStep.Area).AreaReady
                && wizard.Controller.HasError && status.Visibility==Visibility.Visible
                && status.Text.Contains("저장하지 못했습니다."),
                "Failed area save let the wizard advance or hid the actionable failure");
            Check(FirstRunWizardWindow.AreaActionFailure(
                    "설정 저장 실패 · 저장 위치와 권한을 확인하세요.","이전 메시지").Message.Contains("저장하지 못했습니다."),
                "Persist failure was not classified as a save error");

            failSave=false;
            Click(wizard,"WizardWholeGame");
            Check(!wizard.RequiresAreaConfirmation && Ready(main,FirstRunStep.Area).AreaReady
                && wizard.Next.IsEnabled && !wizard.Controller.HasError
                && status.Text.Contains("인식 영역이 설정"),
                "Successful area retry did not clear confirmation/error and enable Next");
            Click(wizard,"WizardNext");
            Check(wizard.Controller.Step==FirstRunStep.Service,
                "Area step did not advance after a confirmed and persisted retry");
        }
        finally {failSave=false;Close(wizard);Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    private static void CheckGameCaptureAtTop(MainWindow main)
    {
        var capture = Named<SmoothScrollViewer>(main,"CapturePage");
        var card = Named<Border>(main,"GameCaptureCard");
        var pick = Named<Button>(main,"PickWindowButton");
        double cardTop = card.TranslatePoint(new Point(0, 0), capture).Y;
        double pickBottom = pick.TranslatePoint(new Point(0, pick.ActualHeight), capture).Y;
        Check(Math.Abs(cardTop) <= 1 && pick.ActualHeight > 0 && pickBottom <= capture.ViewportHeight + 1,
            $"Game Capture was not at the top with window selection visible: cardTop={cardTop}, pickBottom={pickBottom}, viewport={capture.ViewportHeight}");
    }
    private static FirstRunReadiness Ready(MainWindow main,FirstRunStep step) {var task=main.WizardReadinessAsync(step,CancellationToken.None);Complete(task);return task.Result;}
    private static void Click(FirstRunWizardWindow wizard,string name) {Named<Button>(wizard,name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Complete(wizard.PendingAction);}
    private sealed class NoOcr:IOcrService
    {
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)=>throw new Exception("No actual OCR expected");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
