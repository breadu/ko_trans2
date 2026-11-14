using System.Collections.Generic;
using System.Text.Json;
using KoTrans.Infrastructure;

namespace KoTrans.App;

public partial class MainWindow
{
    internal FirstRunWizardWindow? FirstRunWizard { get; private set; }
    private TranslationPipeline? mockTranslationPipeline;
    internal Func<CancellationToken,Task<WindowClickPickResult>>? PickWizardWindowOverride {get;set;}
    internal IReadOnlyList<Profile> WizardProfiles=>settings.Profiles.ToArray();
    internal bool WizardUsesDefault(Profile profile)=>profile.Id==settings.DefaultProfileId;
    internal (string Name,string Language) WizardProfileDraft(Profile profile)=>profile.Id==Current.Id
        ? (ProfileName.Text,SourceLanguage.SelectedIndex==1?"일본어":"영어") : (profile.Name,profile.Language);
    internal void ShowWizardMainPage(int page)
    {
        RestoreMainWindow();ShowProfileSettingsPage(page);
    }
    internal Profile WizardProfile=>credentialReturnProfileId is {} previous && settings.CanEditGlobal
        && FirstRunWizard?.Controller is {Step:FirstRunStep.Service} controller && controller.ProfileId==previous
        ? settings.Profiles.FirstOrDefault(p=>p.Id==previous)??Current : Current;
    private TranslationSettingsEditor? WizardServiceDraft => WizardProfile == Current && embeddedProfileEditor is { HasChanges: true } editor ? editor : null;
    internal TranslationProviderInfo WizardSelectedProvider => TranslationProviderCatalog.Get(WizardServiceDraft?.SelectedProvider ?? WizardProfile.Translation.Provider);
    internal LocalTranslationModel WizardSelectedLocalModel => WizardServiceDraft?.SelectedLocalModel ?? WizardProfile.Translation.LocalModel;
    internal bool WizardClosing=>closing || closeApproved;
    // Area selection errors and cancellation are reported through the main
    // status; the wizard mirrors only the relevant outcome after the action.
    internal string WizardAreaActionStatus => model.Status;
    internal WindowPlacement WizardPlacement => settings.FirstRunWizardWindow;
    internal Task WizardPlacementSave { get; private set; } = Task.CompletedTask;
    internal void RememberWizardPlacement(WindowPlacement placement)
    {
        settings.FirstRunWizardWindow = placement;
        if (!WizardClosing) WizardPlacementSave = SaveSettings();
    }
    private bool WizardSaved(bool saved)
    {
        if(!saved)FirstRunWizard?.Controller.ReportError("설정을 저장하지 못했습니다. "+model.Status);
        return saved;
    }
    private void OpenFirstRunWizard(object sender,RoutedEventArgs e)=>OpenFirstRunWizard();
    internal FirstRunWizardWindow OpenFirstRunWizard(bool show=true)
    {
        if(FirstRunWizard is {} existing) {
            if(show) {existing.Show();if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;existing.Activate();existing.PresentCurrentStep();}
            return existing;
        }
        var window=new FirstRunWizardWindow(this);FirstRunWizard=window;
        window.Closed+=(_,_)=>{if(ReferenceEquals(FirstRunWizard,window))FirstRunWizard=null;};
        if(show){window.Show();window.StartMonitoring();}
        return window;
    }
    internal async Task<bool> CreateWizardProfileAsync(CancellationToken token)
    {
        if(closing || token.IsCancellationRequested)return false;
        ShowWizardMainPage(0);model.Status="새 게임 프로필을 만들고 있습니다.";
        if(!WizardSaved(await CreateProfileWithFeedbackAsync()))return false;
        token.ThrowIfCancellationRequested();
        ShowWizardMainPage(0);
        model.Status="새 프로필을 만들었습니다. 처음 사용하기에서 이름과 원문 언어를 설정한 뒤 ‘저장 후 다음’을 누르세요.";
        if(IsVisible && new System.Windows.Interop.WindowInteropHelper(this).Handle!=IntPtr.Zero) {ProfileName.Focus();ProfileName.SelectAll();}
        return true;
    }
    // "기본 정보 보기" is navigation, not the wizard's Save/Next action.
    // Switch only when the wizard selected a different profile; that switch
    // uses the existing durable profile transition. Do not save pending wizard
    // name/language drafts until the user explicitly presses Save/Next.
    internal async Task<bool> ShowWizardBasicInformationAsync(Profile? selected,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(closing || selected is null || !WizardProfiles.Contains(selected))return false;
        if(Current.Id!=selected.Id && !WizardSaved(await SwitchProfileAsync(selected)))return false;
        token.ThrowIfCancellationRequested();
        if(Current.Id!=selected.Id)return false;
        return await PresentWizardStepAsync(FirstRunStep.Profile,token);
    }

    internal async Task<bool> SaveWizardProfileAsync(Profile? selected,string? name,string? language,CancellationToken token)
    {
        if(closing || token.IsCancellationRequested || selected is null || !WizardProfiles.Contains(selected))return false;
        ShowWizardMainPage(0);
        if(language is not null && language is not ("영어" or "일본어")
            || name is not null && !WizardUsesDefault(selected) && (string.IsNullOrWhiteSpace(name) || name.Trim().Length>80))
            throw new InvalidDataException("게임 이름을 1–80자로 입력하고 원문 언어를 선택해 주세요.");
        if(!WizardSaved(await SwitchProfileAsync(selected)))return false;
        token.ThrowIfCancellationRequested();
        if(Current.Id!=selected.Id)return false;
        // Only explicit wizard edits override the main form. In particular, the
        // name typed in Basic Information must survive a click on Next.
        if(name is not null && !WizardUsesDefault(selected))ProfileName.Text=name.Trim();
        if(language is not null)SourceLanguage.SelectedIndex=language=="일본어"?1:0;
        return WizardSaved(await SaveCurrentProfileAsync());
    }
    internal async Task<bool> PresentWizardStepAsync(FirstRunStep step,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(closing)return false;
        var profile=Current.Id;
        switch(step) {
            case FirstRunStep.Profile:
                ShowWizardMainPage(0);
                return await AlignWizardItemAsync(BasicPage, ProfileNameGroup, profile, token);
            case FirstRunStep.Game:
                return await PrepareWizardGameStepAsync(token);
            case FirstRunStep.Area:
                return await PrepareWizardGameStepAsync(token,area:true);
            case FirstRunStep.Service:
                ShowWizardMainPage(2);
                if (embeddedProfileEditor is null) return false;
                var (scroll, caption) = embeddedProfileEditor.ProviderNavigation;
                return await AlignWizardItemAsync(scroll, caption, profile, token);
            case FirstRunStep.Start:
                RestoreMainWindow();
                if(!await ReturnToLauncherAsync())return false;
                return await AlignWizardItemAsync(LauncherScroll, WatchButton, profile, token);
            // The user confirms the actual game/subtitle here. Do not cover it.
            default:return true;
        }
    }
    private async Task<bool> AlignWizardItemAsync(ScrollViewer scroll, FrameworkElement target, Guid profile, CancellationToken token)
    {
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        token.ThrowIfCancellationRequested();
        if(closing || Current.Id!=profile)return false;
        if (scroll is SmoothScrollViewer smoothScroll) smoothScroll.StopAnimation();
        scroll.UpdateLayout();
        if (scroll.Content is not FrameworkElement content || !content.IsAncestorOf(target)) return false;
        // Use the laid-out item's position; the scroll viewer clamps it to the available range.
        scroll.ScrollToVerticalOffset(target.TranslatePoint(new Point(0, 0), content).Y + content.Margin.Top);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        token.ThrowIfCancellationRequested();
        if(closing || Current.Id!=profile)return false;
        target.UpdateLayout();return true;
    }
    internal async Task<bool> PrepareWizardGameStepAsync(CancellationToken token,bool area=false)
    {
        token.ThrowIfCancellationRequested();
        if(closing)return false;
        var profile=Current.Id;ShowWizardMainPage(1);
        if(Current.CaptureMode!=CaptureMode.Window) {
            if(FirstRunWizard is {} wizard)wizard.RequiresAreaConfirmation=true;
            InputMode.SelectedIndex=CaptureModeToSelectionIndex(CaptureMode.Window);
        }
        var target = area ? (FrameworkElement)SelectAreaButton : GameCaptureCard;
        if (!await AlignWizardItemAsync(CapturePage, target, profile, token)) return false;
        return WizardSaved(await SaveCurrentProfileAsync());
    }
    internal Task<IReadOnlyList<WindowTarget>> FindWizardWindowsAsync(CancellationToken token)=>Task.Run(findWindows,token).WaitAsync(token);
    internal async Task<bool> ConnectWizardWindowAsync(WindowTarget chosen,IReadOnlyList<WindowTarget> candidates,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        getWindowBounds(chosen); // A minimized/dead candidate is not a completed connection.
        if((Current.CaptureMode!=CaptureMode.Window || Current.GameWindow is {} previous
            && !previous.MatchesStableIdentity(chosen.Identity) && !previous.Matches(chosen.Identity))
            && FirstRunWizard is {} wizard)wizard.RequiresAreaConfirmation=true;
        if(Current.CaptureMode!=CaptureMode.Window || target?.Handle!=chosen.Handle)StopTranslation();
        return await CommitPickedWindowAsync(chosen,Current.Id,message=>FirstRunWizard?.Controller.Report(message),candidates);
    }
    internal async Task<WindowTarget?> PickWizardGameAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(closing || selectingTargetWindow || selectingArea)return null;
        selectingTargetWindow=true;var profile=Current.Id;
        try {
            StopTranslation();
            // Step 2 belongs to the guide. Hide a covering settings window, but
            // never restore/activate it after selecting the game.
            if(IsVisible)Hide();
            var picked=await (PickWizardWindowOverride?.Invoke(token)??WindowClickSelector.PickAsync(findWindows,token)).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if(closing || Current.Id!=profile)return null;
            if(picked.Canceled) {FirstRunWizard?.Controller.Report("대상 창 직접 클릭을 취소했습니다. 기존 연결을 유지합니다.");return null;}
            if(picked.Target is not {} chosen) {FirstRunWizard?.Controller.ReportError("선택 가능한 게임 창을 찾지 못했습니다. 다시 클릭하거나 목록에서 선택해 주세요.");return null;}
            return await ConnectWizardWindowAsync(chosen,[chosen],token)?chosen:null;
        } finally {selectingTargetWindow=false;}
    }
    internal async Task<bool> WholeWizardWindowAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ShowWizardMainPage(1);
        var edit=CaptureAreaEditContext()??throw new CaptureUnavailableException("먼저 게임 창을 연결해 주세요.");
        bool saved=await ConfirmSourceCaptureAreaAsync(new(0,0,edit.Window.Width,edit.Window.Height),Current.Id,edit with {OriginalPreview=true});
        if(saved && FirstRunWizard is {} wizard)wizard.RequiresAreaConfirmation=false;
        return saved;
    }
    internal async Task<bool> WizardProviderSettingsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A separate API-key action opens the global page. The bottom "번역 설정
        // 보기" action always returns to the game's provider editor, including
        // after visiting global credentials.
        if(credentialReturnProfileId is {} id && settings.CanEditGlobal
            && FirstRunWizard?.Controller.ProfileId==id) {
            if(!await SaveWizardSettingsAsync(token))return false;
            token.ThrowIfCancellationRequested();
        }
        return await PresentWizardStepAsync(FirstRunStep.Service,token);
    }
    internal async Task OpenWizardApiSettingsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RestoreMainWindow();
        await NavigateToGlobalSettingsAsync();
    }
    internal async Task<bool> SaveWizardSettingsAsync(CancellationToken token, bool restoreMain=true)
    {
        if(restoreMain) RestoreMainWindow();
        if(!WizardSaved(await SaveCurrentProfileAsync()))return false;
        token.ThrowIfCancellationRequested();
        if(credentialReturnProfileId is {} id && settings.CanEditGlobal && FirstRunWizard?.Controller.ProfileId==id) {
            var game=settings.Profiles.FirstOrDefault(p=>p.Id==id);
            if(game is null || !await SwitchProfileAsync(game))return false;
            credentialReturnProfileId=null;ShowProfileSettingsPage(2);
        }
        return true;
    }
    internal async Task<FirstRunReadiness> WizardReadinessAsync(FirstRunStep step,CancellationToken token)
    {
        var p=WizardProfile;bool profile=WizardProfiles.Contains(p)
            && !string.IsNullOrWhiteSpace(p.Name) && p.Language is "영어" or "일본어";
        bool game=false,area=false;string message="게임 프로필과 원문 언어를 선택해 주세요.";
        if(profile)message="게임을 실행하고 사용할 창을 선택해 연결해 주세요.";
        if(profile && p==Current && p.CaptureMode==CaptureMode.Window && target is {} chosen && p.GameWindow is {} saved
            && (saved.MatchesStableIdentity(chosen.Identity)||saved.Matches(chosen.Identity))) {
            try {
                var bounds=getWindowBounds(chosen);game=true;
                var region=WindowRegion(p,chosen);WindowCaptureBounds.Resolve(bounds,region);
                area=(p.CaptureAreaConfigured || p.WindowCaptureRegion is not null) && FirstRunWizard?.RequiresAreaConfirmation!=true;
                message=area?"게임 창과 대사 영역이 준비되었습니다.":"대사가 나올 때까지 게임을 진행한 뒤 대사 영역을 지정해 주세요.";
            } catch(Exception ex) when(ex is CaptureUnavailableException or InvalidDataException) {message=ex.Message;}
        }
        if (step == FirstRunStep.Area && game)
            message = area ? "인식 영역이 설정되었습니다. 다음 단계로 진행하세요."
                : FirstRunWizard?.RequiresAreaConfirmation == true
                    && (p.WindowCaptureRegion is not null || p.CaptureAreaConfigured)
                    ? "현재 게임 창에 맞게 인식 영역을 다시 확인하세요."
                    : "대사가 나오면 인식 영역을 지정하세요.";
        bool service=false;
        LocalTranslationModel? checkedLocalModel = null;
        var editor = step == FirstRunStep.Service ? WizardServiceDraft : null;
        var translation = p.Translation;
        var provider=TranslationProviderCatalog.Get(editor?.SelectedProvider ?? translation.Provider);
        string serviceMessage="";
        try {
            if (editor is not null) translation = editor.ReadTranslationOptions();
            else (translation with { }).Validate();
            if(provider.RequiresModel && string.IsNullOrWhiteSpace(translation.Model))serviceMessage="번역 서비스 설정에서 모델을 선택해 주세요.";
            else if(provider.Credentials.Any(c=>!vault.ContainsCredential(provider.Provider,c.Id)))serviceMessage="공통 설정에서 API 키를 등록해 주세요.";
            else if(provider.Provider==TranslationProvider.HyMtLocal) {
                if (step >= FirstRunStep.Service) {
                    service = CanManageLocalModel && await PreparationFor(translation.LocalModel).IsInstalledAsync(token);
                    checkedLocalModel = translation.LocalModel;
                }
                serviceMessage=service?"로컬 AI 모델이 설치되어 있습니다.":"로컬 AI 모델 설치가 필요합니다. 설치 버튼을 누르면 다운로드 동의를 확인합니다.";
            } else {service=true;serviceMessage=provider.Provider==TranslationProvider.Ollama
                ? "설정 준비 완료 · Ollama 서버를 실행해 주세요. 연결은 번역 시작 후 확인합니다."
                : "설정 준비 완료 · 실제 연결과 번역 성공은 번역 시작 후 확인합니다.";}
        } catch(Exception ex) when (ex is InvalidDataException or TranslationException) {serviceMessage=ex.Message;}
        if(step>=FirstRunStep.Service && profile && game && area)
            message=step==FirstRunStep.Service ? serviceMessage : provider.DisplayName+" · "+serviceMessage;
        // The start step is an action, not another settings step. Keep the
        // provider identity while avoiding a duplicate "ready" announcement.
        if(step==FirstRunStep.Start && profile && game && area && service && p==Current)
            message=provider.Provider switch {
                TranslationProvider.Ollama => $"번역 서비스: {provider.DisplayName} · 서버 실행 여부를 확인하세요. 실제 연결은 번역 시작 후 확인합니다.",
                TranslationProvider.HyMtLocal => $"번역 서비스: {provider.DisplayName} · 로컬 모델 준비 완료",
                _ => $"번역 서비스: {provider.DisplayName} · 실제 연결은 번역 시작 후 확인합니다."
            };
        bool running=p==Current && pipeline?.IsRunning==true;
        if(step==FirstRunStep.FirstSubtitle && running)message=RecognitionInputPlan.For(p,true).UseTriggers
            ? "게임을 진행한 뒤 설정한 입력 트리거를 누르세요. 완성된 한국어 자막을 기다립니다."
            : "대사가 나올 때까지 게임을 진행해 주세요. 완성된 한국어 자막을 기다립니다.";
        if(p!=Current)message=provider.Credentials.Count>0
            ? "API 키를 등록한 뒤 ‘설정 확인 후 다음’을 누르거나 ‘이전 게임 프로필로 돌아가기’를 눌러 주세요."
            : "‘설정 확인 후 다음’을 누르거나 ‘이전 게임 프로필로 돌아가기’를 눌러 주세요.";
        // The first step must describe the first step, even when a previously
        // configured game/window happens to be connected already.
        if(step==FirstRunStep.Profile)
            message=profile ? "게임 프로필 이름과 원문 언어를 확인한 뒤 ‘저장 후 다음’을 누르세요."
                : "사용할 게임 프로필과 원문 언어를 선택해 주세요.";
        string config=JsonSerializer.Serialize(new {p.Id,p.GameWindow,p.CaptureMode,p.WindowCaptureRegion,p.CaptureArea,p.CaptureAreaConfigured,
            p.Language,p.Vertical,p.ReadingMode,p.OcrEngineId,p.OcrModelPresetId,p.OcrGpuAcceleration,Translation=translation,p.AutomaticWatch,p.Triggers,p.ShowTranslation});
        return new(p.Id,profile,game || p!=Current,area || p!=Current,service,running,config,message,checkedLocalModel);
    }
    internal async Task<bool> SelectWizardAreaAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ShowWizardMainPage(1);
        return await SelectCaptureAreaAsync();
    }
    internal async Task InstallWizardModelAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (WizardProfile != Current || WizardSelectedProvider.Provider != TranslationProvider.HyMtLocal) return;
        var selection = WizardSelectedLocalModel;
        ShowWizardMainPage(2);
        await ManageLocalModelAsync(false, Current.Id, this, selection);
    }
    // The wizard button and the main launcher/hotkey share this exact path.
    // In particular, both arm the controller before asynchronous OCR starts.
    internal async Task<bool> StartWizardTranslationAsync(FirstRunWizardController controller,
        CancellationToken token, bool hideMainForStart=true)
    {
        if(controller.Step!=FirstRunStep.Start || controller.ProfileId!=WizardProfile.Id)return false;
        // A global hotkey may be pressed while the game has focus; keep it
        // focused instead of restoring the main window merely to save settings.
        var profileId=controller.ProfileId;
        if(!await SaveWizardSettingsAsync(token,restoreMain:hideMainForStart))return false;
        if(Current.Id!=profileId || controller.Step!=FirstRunStep.Start)return false;
        if(!Current.ShowTranslation) {
            // This explicit start owns the transition. The checkbox's async
            // display-change handler must not launch a competing restart.
            StopTranslation();
            bool wasChanging=changing;changing=true;
            try {ShowKorean.IsChecked=true;}
            finally {changing=wasChanging;}
            ApplyOptions();
        }
        var state=await WizardReadinessAsync(FirstRunStep.Start,token);controller.Refresh(state);
        token.ThrowIfCancellationRequested();
        if(controller.Step!=FirstRunStep.Start || Current.Id!=state.ProfileId || !state.ProfileReady || !state.GameReady || !state.AreaReady || !state.ServiceReady)return false;
        controller.Arm(Current.Id);
        if(automatic && pipeline is {IsRunning:true} existing) {
            controller.Bind(existing,Current.Id,existing.Version);
            // Korean subtitles may have just been enabled. Bind the effective
            // configuration so the next readiness refresh cannot reset step 6.
            var effectiveRunning=await WizardReadinessAsync(FirstRunStep.FirstSubtitle,token);
            token.ThrowIfCancellationRequested();
            controller.Started(true,effectiveRunning.Configuration);
            return true;
        }
        bool started=await StartFromLauncherAsync(hideMainForStart);
        var effective=started?await WizardReadinessAsync(FirstRunStep.FirstSubtitle,token):state;
        controller.Started(started,effective.Configuration);
        if(!started)controller.ReportError(model.Status);
        return started;
    }
    // Only an active step-5 wizard for this game receives a main-window start.
    // Other launcher and hotkey starts retain their normal behavior.
    internal async Task<bool> StartMainTranslationAsync(bool hideMainForStart)
    {
        var wizard=FirstRunWizard;
        if(wizard is { } live && !WizardClosing
            && live.Controller.Step==FirstRunStep.Start && live.Controller.ProfileId==Current.Id) {
            // Share the wizard's busy/error/lifetime guards for launcher and
            // hotkey starts, including time spent saving and checking readiness.
            return await live.StartFromMainAsync(hideMainForStart);
        }
        return await StartFromLauncherAsync(hideMainForStart);
    }

    private Task<bool> StartFromLauncherAsync(bool hideMain) => hideMain
        ? StartWithMainHiddenAsync(() => StartTranslationAsync(true))
        : StartTranslationAsync(true);

    private void WizardSubtitleApplied(TranslationPipeline active,Guid profile,PipelineUpdate update)
    {
        var window=FirstRunWizard;if(window is null)return;
        bool displayed=overlay is {IsVisible:true} && Current.ShowTranslation && overlay.MatchesFinalSubtitle(update);
        window.Controller.SubtitleApplied(active,profile,update,displayed,App.MockAi || ReferenceEquals(mockTranslationPipeline,active));
        // A new OCR pass is not proof that a failed AI translation recovered.
        // Respect the pipeline's recovery-area signals, just as the main
        // status bar does, and preserve actionable capture wait notices.
        window.Controller.RecoverPipelineAreas(update.RecoveredAreas);
        if(update.State==PipelineState.Error || update.Important)
            window.Controller.ReportPipelineIssue(update.Message,update.FailureArea,update.State==PipelineState.Error);
        else if(!window.Controller.HasError) {
            if(update.State is PipelineState.Recognizing or PipelineState.Translating)
                window.Controller.Report(update.State==PipelineState.Recognizing
                    ? "게임 대사를 인식하고 있습니다." : "한국어 번역을 기다리고 있습니다.");
            else if(update.State==PipelineState.Watching && update.RecoveredAreas!=PipelineStatusArea.None)
                window.Controller.Report(update.Message);
        }
        window.Render();
    }
}
