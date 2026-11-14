using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Threading;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed class FirstRunWizardWindow : Window
{
    private readonly MainWindow host;
    internal FirstRunWizardController Controller { get; }=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromSeconds(3)};
    private readonly TextBlock progress=Text("WizardProgress"),heading=Text("WizardHeading"),description=Text("WizardDescription"),status=Text("WizardStatus");
    private readonly TextBlock exclusion=Text("WizardCaptureWarning"),service=Text("WizardService");
    private readonly Button apiSettings, installModel;
    private readonly TextBlock profileHint=Text("WizardProfileHint");
    private readonly TextBlock gamePlaceholder=new() {Name="WizardGamePlaceholder",Text="게임 창을 선택해 주세요.",IsHitTestVisible=false,
        Foreground=new SolidColorBrush(Color.FromRgb(80,98,116)),Margin=new(12,5,30,12),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    internal ComboBox ProfileChoice {get;}=new() {Name="WizardProfiles",DisplayMemberPath="Name",MinWidth=160};
    internal TextBox ProfileName {get;}=new() {Name="WizardProfileName",MaxLength=80};
    internal ComboBox SourceLanguageChoice {get;}=new() {Name="WizardLanguage",ItemsSource=new[]{"영어","일본어"},SelectedIndex=0};
    internal ComboBox GameChoice {get;}=new() {Name="WizardGames",MinWidth=160};
    internal Button Next {get;}=new() {Name="WizardNext"};
    internal Button Previous {get;}=new() {Name="WizardPrevious",Content="이전"};
    private readonly Button cancel=new() {Name="WizardCancel",Content="취소"},retry=new() {Name="WizardRetry",Content="다시 확인"};
    private readonly StackPanel[] pages=Enumerable.Range(0,6).Select(_=>new StackPanel()).ToArray();
    private IReadOnlyList<WindowTarget> candidates=[];
    private FirstRunReadiness? readiness;
    private DateTimeOffset lastSearch=DateTimeOffset.MinValue;
    private bool closed,busy,refreshing,rendering,monitoring,fittingDisplay;
    private bool nameEdited,languageEdited;
    private bool gameSearchCompleted;
    private long interactionVersion;
    private WindowTarget? lastConnectedTarget;
    private string profileList="";
    private Guid shownProfile;
    internal WindowPlacement? LastNormalPlacement { get; private set; }
    internal bool CaptureExcluded {get;private set;}
    internal bool IsMonitoring=>monitoring;
    internal bool IsBusy=>busy;
    internal bool RequiresAreaConfirmation {get;set;}
    internal Task PendingAction {get;private set;}=Task.CompletedTask;
    private static TextBlock Text(string name)=>new() {Name=name,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White,Margin=new(0,5,0,7)};
    internal FirstRunWizardWindow(MainWindow host)
    {
        this.host=host;Title="KO Trans 2 · 처음 사용하기";Width=620;Height=760;MinWidth=320;MinHeight=350;
        Background=new SolidColorBrush(Color.FromRgb(16,27,42));Foreground=Brushes.White;FontFamily=new("맑은 고딕");FontSize=14;
        ShowActivated=false;ShowInTaskbar=true;ResizeMode=ResizeMode.CanResize;
        Resources[typeof(Button)]=host.FindResource("UiButton");Resources[typeof(TextBox)]=host.FindResource("UiInput");
        Next.Style=(Style)host.FindResource("UiPrimary");
        heading.FontSize=22;heading.FontWeight=FontWeights.Bold;status.Foreground=Brushes.LightSkyBlue;
        exclusion.Foreground=Brushes.LightSalmon;progress.Foreground=Brushes.LightSteelBlue;
        var root=new DockPanel {Margin=new(20)};Content=root;
        var header=new StackPanel {Margin=new Thickness(0,0,0,14)};header.Children.Add(progress);DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var footer=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};
        foreach(var button in new[]{cancel,Previous,retry,Next}) {
            button.MinWidth=75;button.Height=40;button.FontSize=14;footer.Children.Add(button);
        }
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();body.Children.Add(heading);body.Children.Add(description);
        foreach(var page in pages)body.Children.Add(page);body.Children.Add(status);body.Children.Add(exclusion);
        root.Children.Add(new ScrollViewer {Name="WizardBody",Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        Label(pages[0],"사용할 프로필");pages[0].Children.Add(ProfileChoice);Label(pages[0],"게임 프로필 이름");pages[0].Children.Add(ProfileName);
        Label(pages[0],"원문 언어");pages[0].Children.Add(SourceLanguageChoice);
        ActionButton(pages[0],"새 게임 프로필 만들기","WizardCreateProfile",async token=> {
            if(await host.CreateWizardProfileAsync(token)) {
                nameEdited=languageEdited=false;await RefreshAsync();
                Controller.Report("새 프로필을 만들었습니다. 이 화면에서 이름과 원문 언어를 설정한 뒤 ‘저장 후 다음’을 누르세요.");
            }
        });
        profileHint.FontSize=12;profileHint.Foreground=Brushes.LightSteelBlue;pages[0].Children.Add(profileHint);
        Label(pages[1],"연결할 게임 창");
        var gameField=new Grid();gameField.Children.Add(GameChoice);gameField.Children.Add(gamePlaceholder);pages[1].Children.Add(gameField);
        ActionButton(pages[1],"대상 창 직접 클릭","WizardPickGame",async token=> {
            WindowTarget? picked=null;
            await WithHiddenAsync(async ()=>{picked=await host.PickWizardGameAsync(token);return picked is not null;});
            if(picked is not null) {
                UpdateGameCandidates(candidates.Any(w=>SameWindow(w,picked))?candidates:[..candidates,picked],picked);
                ApplyReadiness(await host.WizardReadinessAsync(Controller.Step,token));
            }
            await RefreshAsync(true);
        });
        ActionButton(pages[2],"대사 영역 지정","WizardPickArea",async token=> {
            string previous=host.WizardAreaActionStatus;
            bool saved=await WithHiddenAsync(()=>host.SelectWizardAreaAsync(token));
            await RefreshAsync();
            if(!saved) ReportAreaActionFailure(previous);
        });
        ActionButton(pages[2],"게임 창 전체를 인식 영역으로 사용","WizardWholeGame",async token=> {
            string previous=host.WizardAreaActionStatus;
            bool saved=await host.WholeWizardWindowAsync(token);
            await RefreshAsync();
            if(!saved) ReportAreaActionFailure(previous);
        });
        pages[3].Children.Add(service);
        apiSettings = ActionButton(pages[3], "기본 프로필에서 API 관리", "WizardApiSettings", async token => {
            await host.OpenWizardApiSettingsAsync(token);
            await RefreshAsync();
        });
        installModel = ActionButton(pages[3],"로컬 AI 모델 설치","WizardInstallModel",async token=> {
            if(host.WizardSelectedProvider.Provider!=TranslationProvider.HyMtLocal)return;
            await host.InstallWizardModelAsync(token);token.ThrowIfCancellationRequested();await RefreshAsync();
        });
        ActionButton(pages[4],"인식 방식·트리거 설정 보기 (선택)","WizardReviewSettings",token=>{host.ShowWizardMainPage(4);return Task.CompletedTask;});
        ProfileChoice.SelectionChanged+=(_,_)=> {
            if(rendering)return;
            nameEdited=languageEdited=false;RefreshProfileDraft();
            Render();
        };
        ProfileName.TextChanged+=(_,_)=>{if(!rendering)nameEdited=true;};
        SourceLanguageChoice.SelectionChanged+=(_,_)=>{if(!rendering)languageEdited=true;};
        GameChoice.SelectionChanged+=(_,_)=> {
            if(rendering)return;interactionVersion++;
            if(Controller.Step==FirstRunStep.Game && readiness is {} state)ApplyReadiness(state);
            Render();
        };
        Next.Click+=(_,_)=>Queue(NextAsync);retry.Click+=(_,_)=>Queue(async token=> {
            if(Controller.Step==FirstRunStep.FirstSubtitle) {
                // The wizard already polls every three seconds. This explicit
                // refresh must not clear outstanding capture/translation errors,
                // restart OCR or modify game settings.
                await RefreshAsync(true);
                return;
            }
            if(Controller.Step==FirstRunStep.Profile) {
                var selected=ProfileChoice.SelectedItem as Profile;
                if(selected is null) {
                    Controller.ReportError("사용할 게임 프로필을 먼저 선택하세요.");
                    return;
                }
                // Profile switching refreshes the wizard fields. Preserve any
                // uncommitted name/language draft until "저장 후 다음" is pressed.
                var draftName=ProfileName.Text;
                var draftLanguage=SourceLanguageChoice.SelectedItem as string;
                bool preserveName=nameEdited, preserveLanguage=languageEdited;
                if(!await host.ShowWizardBasicInformationAsync(selected,token))return;
                await RefreshAsync(true);
                if(!closed && ProfileChoice.SelectedItem is Profile shown && shown.Id==selected.Id) {
                    rendering=true;
                    try {
                        if(preserveName) {ProfileName.Text=draftName;nameEdited=true;}
                        if(preserveLanguage) {SourceLanguageChoice.SelectedItem=draftLanguage;languageEdited=true;}
                    } finally {rendering=false;}
                }
            } else if(Controller.Step==FirstRunStep.Service) {
                // View the game profile's provider settings without requiring a
                // valid provider/model before the user can repair those settings.
                if(!await host.WizardProviderSettingsAsync(token))return;
            } else {
                await PresentStepAsync(token);
                // Step 5 already calls ReturnToLauncherAsync(), which saves any
                // edits when leaving profile settings. Do not save twice.
                if(Controller.Step is not (FirstRunStep.Game or FirstRunStep.Start or FirstRunStep.FirstSubtitle))
                    if(!await host.SaveWizardSettingsAsync(token))return;
            }
            await RefreshAsync(true);
        },clearError:Controller.Step!=FirstRunStep.FirstSubtitle);
        Previous.Click+=(_,_)=>{if(busy)return;Controller.Back();Render();PresentCurrentStep();};
        cancel.Click+=(_,_)=>Close();
        timer.Tick+=RefreshTick;
        Closing+=(_,_)=> {
            RememberNormalPlacement();
            if(LastNormalPlacement is { } placement)host.RememberWizardPlacement(placement);
        };
        Closed+=(_,_)=>{closed=true;timer.Stop();timer.Tick-=RefreshTick;monitoring=false;lifetime.Cancel();Controller.Dispose();lifetime.Dispose();};
        DesktopWindows.Restore(this,host.WizardPlacement);
        SourceInitialized+=(_,_)=> {
            SetCaptureExclusion(CaptureExclusionPolicy.TryExclude(new WindowInteropHelper(this).Handle));FitDisplay();
            RememberNormalPlacement();
        };
        DpiChanged+=(_,_)=>{FitDisplay();RememberNormalPlacement();};
        LocationChanged+=(_,_)=>{if(new WindowInteropHelper(this).Handle!=IntPtr.Zero){FitDisplay();RememberNormalPlacement();}};
        SizeChanged+=(_,_)=>RememberNormalPlacement();
        Render();
    }
    private void RememberNormalPlacement()
    {
        if(closed || WindowState!=WindowState.Normal || new WindowInteropHelper(this).Handle==IntPtr.Zero)return;
        LastNormalPlacement=DesktopWindows.RememberOverlay(this);
    }
    private void FitDisplay()
    {
        if(closed || fittingDisplay || WindowState==WindowState.Minimized || new WindowInteropHelper(this).Handle==IntPtr.Zero)return;
        fittingDisplay=true;
        try {
            var d=DesktopWindows.CurrentDisplay(this);
            ApplyAvailableSize(new(d.WorkArea.Width/d.DpiScale,d.WorkArea.Height/d.DpiScale));
            if(WindowTargets.Bounds(new WindowInteropHelper(this).Handle) is { } bounds) {
                int x=(int)Math.Clamp((long)bounds.X,d.WorkArea.X,
                    (long)d.WorkArea.X+Math.Max(0,d.WorkArea.Width-(int)Math.Ceiling(Width*d.DpiScale)));
                int y=(int)Math.Clamp((long)bounds.Y,d.WorkArea.Y,
                    (long)d.WorkArea.Y+Math.Max(0,d.WorkArea.Height-(int)Math.Ceiling(Height*d.DpiScale)));
                if(x!=bounds.X || y!=bounds.Y)DesktopWindows.MovePhysical(this,x,y);
            }
        } finally {fittingDisplay=false;}
    }
    internal void ApplyAvailableSize(Size size)
    {
        MinWidth=Math.Min(320,size.Width);MinHeight=Math.Min(350,size.Height);
        MaxWidth=size.Width;MaxHeight=size.Height;Width=Math.Min(Width,MaxWidth);Height=Math.Min(Height,MaxHeight);
    }
    internal void SetCaptureExclusion(bool excluded)
    {
        CaptureExcluded=excluded;exclusion.Text=excluded || CaptureExclusionPolicy.DebugCaptureEnabled
            ? "" : "이 안내 창이 캡처 영역을 가리면 최소화하거나 영역 밖으로 옮겨 주세요.";
    }
    private static void Label(StackPanel panel,string value)=>panel.Children.Add(new TextBlock {Text=value,Foreground=Brushes.LightSteelBlue,Margin=new(0,12,0,4),TextWrapping=TextWrapping.Wrap});
    private Button ActionButton(StackPanel panel,string caption,string name,Func<CancellationToken,Task> action)
    {
        var button=new Button {Content=caption,Name=name,FontSize=14,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,10,0,0)};
        button.Click+=(_,_)=>Queue(action);panel.Children.Add(button);
        return button;
    }
    private void Queue(Func<CancellationToken,Task> action,bool clearError=true) {
        if(closed || busy)return;
        interactionVersion++;
        if(clearError)Controller.ClearError();
        PendingAction=RunAsync(action);
    }
    internal async Task<bool> StartFromMainAsync(bool hideMainForStart)
    {
        if(closed || busy || Controller.Step!=FirstRunStep.Start)return false;
        bool started=false;
        Queue(async token=> {
            started=await host.StartWizardTranslationAsync(Controller,token,hideMainForStart);
            if(started)await RefreshAsync();
        });
        await PendingAction;
        return started;
    }
    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if(closed || busy)return;busy=true;Render();
        try {await action(lifetime.Token);}
        catch(OperationCanceledException) { }
        catch(Exception ex) when(ex is not OutOfMemoryException) {Controller.ReportError(ex is TranslationException or CaptureUnavailableException or InvalidDataException?ex.Message:"작업을 마치지 못했습니다. 설정과 게임 창을 확인하고 다시 시도해 주세요.");}
        finally {busy=false;if(!closed)Render();}
    }
    private async void RefreshTick(object? sender,EventArgs e)
    {
        if(host.WizardClosing){Close();return;}
        await RefreshInBackgroundAsync();
    }
    internal async Task RefreshInBackgroundAsync(bool forceSearch=false)
    {
        if(closed || busy || refreshing)return;
        try {await RefreshAsync(forceSearch);}
        catch(OperationCanceledException) { }
        catch(Exception ex) when(ex is not OutOfMemoryException) {
            if(!closed) {
                string message=Controller.Step switch {
                    FirstRunStep.Profile => "프로필 설정을 확인하지 못했습니다. 이름과 언어를 확인한 뒤 ‘저장 후 다음’을 눌러 주세요.",
                    FirstRunStep.Game => "게임 창과 설정을 확인하지 못했습니다. ‘캡처 설정 보기’에서 확인해 주세요.",
                    FirstRunStep.Area => "인식 영역을 확인하지 못했습니다. ‘영역 설정 보기’에서 확인해 주세요.",
                    FirstRunStep.Service => "번역 서비스 설정을 확인하지 못했습니다. ‘번역 설정 보기’에서 확인해 주세요.",
                    FirstRunStep.Start => "번역 시작 준비를 확인하지 못했습니다. ‘메인 화면 보기’에서 확인해 주세요.",
                    FirstRunStep.FirstSubtitle => "번역 진행 상태를 확인하지 못했습니다. ‘상태 새로고침’을 눌러 주세요.",
                    _ => "게임 창과 설정을 확인하지 못했습니다. ‘다시 확인’을 눌러 주세요."
                };
                if(Controller.Step is FirstRunStep.Start or FirstRunStep.FirstSubtitle)
                    Controller.ReportRefreshIssue(message);
                else Controller.ReportError(message);
                Render();
            }
        }
    }
    internal async Task RefreshServiceSelectionAsync()
    {
        if (closed || Controller.Step != FirstRunStep.Service) return;
        interactionVersion++;
        Controller.ClearError();
        Controller.Report("선택한 번역 서비스의 설정을 확인하고 있습니다.");
        Render();
        await RefreshInBackgroundAsync();
    }
    internal void StartMonitoring()
    {
        if(closed || monitoring)return;monitoring=true;timer.Start();PresentCurrentStep();
    }
    internal void PresentCurrentStep()=>Queue(async token=> {
        await PresentStepAsync(token);await RefreshAsync(true);
    });
    private async Task PresentStepAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await host.PresentWizardStepAsync(Controller.Step,token);
    }
    internal async Task RefreshAsync(bool forceSearch=false)
    {
        if(closed || refreshing)return;refreshing=true;long revision=interactionVersion;
        var token=lifetime.Token;
        try {
            var state=await host.WizardReadinessAsync(Controller.Step,token);token.ThrowIfCancellationRequested();if(closed)return;
            if(state.ProfileId!=host.WizardProfile.Id || revision!=interactionVersion)return;
            if(Controller.ProfileId!=state.ProfileId) {
                RequiresAreaConfirmation=false;gameSearchCompleted=false;lastConnectedTarget=null;
                UpdateGameCandidates([]);
            }
            ApplyReadiness(state);RefreshProfiles();
            if(Controller.Step==FirstRunStep.Game && (forceSearch || DateTimeOffset.UtcNow-lastSearch>=TimeSpan.FromSeconds(3))) {
                if(!gameSearchCompleted)Render();
                lastSearch=DateTimeOffset.UtcNow;var found=await host.FindWizardWindowsAsync(token);token.ThrowIfCancellationRequested();
                if(closed || revision!=interactionVersion || Controller.Step!=FirstRunStep.Game || state.ProfileId!=host.WizardProfile.Id)return;
                var latest=await host.WizardReadinessAsync(FirstRunStep.Game,token);token.ThrowIfCancellationRequested();
                if(closed || revision!=interactionVersion || latest.ProfileId!=host.WizardProfile.Id)return;
                gameSearchCompleted=true;
                if(latest.GameReady && host.ConnectedTarget is {} connected && !found.Any(w=>SameWindow(w,connected)))found=[..found,connected];
                UpdateGameCandidates(found);ApplyReadiness(latest);
            }
            Render();
        } finally {refreshing=false;}
    }
    private static bool SameWindow(WindowTarget? left,WindowTarget? right)=>left?.Handle==right?.Handle
        && left?.ProcessId==right?.ProcessId && left?.StartedTicks==right?.StartedTicks;
    private void UpdateGameCandidates(IReadOnlyList<WindowTarget> found,WindowTarget? preferred=null)
    {
        if(GameChoice.IsDropDownOpen && preferred is null)return;
        var connected=host.ConnectedTarget;
        if(preferred is null && !SameWindow(lastConnectedTarget,connected))preferred=connected;
        var selected=GameChoice.SelectedItem as WindowTarget;
        rendering=true;
        try {
            if(!candidates.SequenceEqual(found)) {candidates=found.ToArray();GameChoice.ItemsSource=candidates;}
            var choice=candidates.FirstOrDefault(w=>preferred is not null && SameWindow(w,preferred))
                ?? candidates.FirstOrDefault(w=>selected is not null && SameWindow(w,selected))
                ?? candidates.FirstOrDefault(w=>connected is not null && SameWindow(w,connected))
                ?? (candidates.Count==1?candidates[0]:null);
            if(!ReferenceEquals(GameChoice.SelectedItem,choice))GameChoice.SelectedItem=choice;
            lastConnectedTarget=connected;
        } finally {rendering=false;}
    }
    private void ApplyReadiness(FirstRunReadiness state)
    {
        if(closed || state.ProfileId!=host.WizardProfile.Id)return;
        if(Controller.Step==FirstRunStep.Game) {
            var selected=GameChoice.SelectedItem as WindowTarget;
            string message=selected is not null && (host.ConnectedTarget is null || !SameWindow(selected,host.ConnectedTarget))
                ? "선택한 게임을 확인하고 ‘선택한 창 연결’을 눌러 주세요.":
                state.GameReady && host.ConnectedTarget is {} connected?$"연결됨: {selected?.Title??connected.Title}":
                host.ConnectedTarget is not null?state.Message:!gameSearchCompleted?"실행 중인 게임 창을 찾고 있습니다.":
                candidates.Count==0?"실행 중인 게임 창을 찾지 못했습니다. 게임을 실행하거나 ‘대상 창 직접 클릭’을 이용하세요.":
                GameChoice.SelectedItem is null?"연결할 게임 창을 선택해 주세요.":"선택한 게임을 확인하고 ‘선택한 창 연결’을 눌러 주세요.";
            state=state with {Message=message};
        }
        readiness=state;Controller.Refresh(state);
    }
    private void RefreshProfiles()
    {
        var profiles=host.WizardProfiles;string key=string.Join('|',profiles.Select(p=>p.Id));
        bool changedProfile=shownProfile!=host.WizardProfile.Id;
        if(key!=profileList || changedProfile) {
            Guid selected=(ProfileChoice.SelectedItem as Profile)?.Id??host.WizardProfile.Id;
            rendering=true;
            try {
                profileList=key;shownProfile=host.WizardProfile.Id;ProfileChoice.ItemsSource=profiles;
                ProfileChoice.SelectedItem=profiles.FirstOrDefault(p=>p.Id==(changedProfile?shownProfile:selected));
                nameEdited=languageEdited=false;
            } finally {rendering=false;}
        }
        RefreshProfileDraft();
    }
    private void RefreshProfileDraft()
    {
        if(ProfileChoice.SelectedItem is not Profile profile)return;
        rendering=true;
        try {
            var draft=host.WizardProfileDraft(profile);
            if(!nameEdited)ProfileName.Text=draft.Name;
            if(!languageEdited)SourceLanguageChoice.SelectedItem=draft.Language;
            bool basic=host.WizardUsesDefault(profile);ProfileName.IsReadOnly=basic;
            profileHint.Text=basic?"기본 프로필의 이름은 변경할 수 없습니다.":"이 화면에서 이름과 원문 언어를 바로 수정할 수 있습니다.";
        } finally {rendering=false;}
    }
    private async Task AdvanceAsync(CancellationToken token)
    {
        var state=await host.WizardReadinessAsync(Controller.Step,token);token.ThrowIfCancellationRequested();
        if(closed || state.ProfileId!=host.WizardProfile.Id)return;
        if(Controller.Step!=FirstRunStep.Profile && Controller.ProfileId!=state.ProfileId) {Controller.Refresh(state);Render();return;}
        readiness=state;Controller.Next(state);
        Render();
        await PresentStepAsync(token);
        await RefreshAsync(true);
    }
    private async Task NextAsync(CancellationToken token)
    {
        if(Controller.Completed){Close();return;}
        switch(Controller.Step) {
            case FirstRunStep.Profile:
                if(!await host.SaveWizardProfileAsync(ProfileChoice.SelectedItem as Profile,nameEdited?ProfileName.Text:null,
                    languageEdited?SourceLanguageChoice.SelectedItem as string:null,token))return;
                nameEdited=languageEdited=false;
                break;
            case FirstRunStep.Game:
                if(GameChoice.SelectedItem is WindowTarget chosen) {
                    if(!await host.ConnectWizardWindowAsync(chosen,candidates,token))return;
                } else if(readiness?.GameReady!=true)return;
                break;
            case FirstRunStep.Service:
                if(!await host.SaveWizardSettingsAsync(token))return;
                break;
            case FirstRunStep.Start:
                await host.StartWizardTranslationAsync(Controller,token);Render();return;
            case FirstRunStep.FirstSubtitle:return;
        }
        await AdvanceAsync(token);
    }
    private async Task<bool> WithHiddenAsync(Func<Task<bool>> action)
    {
        bool visible=IsVisible;if(visible)Hide();
        try {return await action();}
        finally {if(visible && !closed && !host.WizardClosing)Show();}
    }
    // During game selection the placeholder already explains the empty field
    // and in-progress search. Keep actionable readiness messages and all errors.
    internal static bool ShowGameStatus(string message, bool hasError) => hasError
        || message is not ("연결할 게임 창을 선택해 주세요." or "실행 중인 게임 창을 찾고 있습니다.");

    // The area-step description already tells the user how to configure an
    // unconfigured region. Do not hide reconfirmation, success or error notices.
    internal static bool ShowAreaStatus(string message, bool hasError) => hasError
        || message is not ("" or "대사가 나오면 인식 영역을 지정하세요.");

    internal static (bool Canceled, string Message) AreaActionFailure(string status, string previous)
    {
        if(status.StartsWith("영역 편집을 취소했습니다.", StringComparison.Ordinal))
            return (true,"영역 지정을 취소했습니다. 기존 영역을 유지합니다.");
        if(status.StartsWith("설정 저장 실패",StringComparison.Ordinal))
            return (false,"인식 영역을 저장하지 못했습니다. 저장 위치와 권한을 확인하고 다시 시도하세요.");
        return (false,status!=previous && !string.IsNullOrWhiteSpace(status)
            ? status : "인식 영역을 지정하지 못했습니다. 게임 창 상태를 확인하고 다시 시도하세요.");
    }

    private void ReportAreaActionFailure(string previous)
    {
        if(closed || Controller.Step!=FirstRunStep.Area)return;
        var feedback=AreaActionFailure(host.WizardAreaActionStatus,previous);
        if(feedback.Canceled)Controller.Report(feedback.Message);
        else {
            // Avoid reusing stale ready-state information if the operation failed.
            if(host.WizardAreaActionStatus.StartsWith("설정 저장 실패",StringComparison.Ordinal))readiness=null;
            Controller.ReportError(feedback.Message);
        }
        Render();
    }

    // Keep the full engine/capture diagnostic in the main status and tooltip.
    // Only wizard steps 5-6 replace technical GDI terminology with an action.
    internal static string FriendlyWizardStatus(FirstRunStep step,string message,
        bool mockAi=false,bool hasError=false,bool completed=false)
    {
        if((step==FirstRunStep.Start || step==FirstRunStep.FirstSubtitle)
            && message.Contains("GDI 창 캡처",StringComparison.Ordinal))
            return "게임 창을 앞으로 가져와 주세요. 활성화되면 인식을 계속합니다.";
        if(step==FirstRunStep.FirstSubtitle && mockAi && !hasError && !completed)
            return "모의 AI 모드에서는 실제 자막 완료를 확인할 수 없습니다. 모의 AI 없이 다시 실행하세요.";
        return message;
    }

    internal void Render()
    {
        if(closed)return;int step=(int)Controller.Step;
        string[] titles=["게임 프로필과 언어","게임을 실행하고 연결해 주세요","게임의 대사 영역을 지정해 주세요","번역 서비스를 확인해 주세요","번역을 시작할 준비가 되었습니다","첫 한국어 자막을 기다립니다"];
        string[] descriptions=["사용할 게임 프로필을 선택하거나 새로 만드세요. 게임 프로필 이름과 원문 언어를 확인한 뒤 ‘저장 후 다음’을 누르세요.",
            "게임 창을 선택하면 창 위치를 옮겨도 인식 영역이 유지됩니다. 게임을 실행한 뒤 목록에서 선택하세요. 없으면 ‘대상 창 직접 클릭’ 또는 ‘캡처 설정 보기’를 이용하세요.",
            "게임에 대사가 나타나면 인식할 영역을 지정하세요. 게임 창 전체를 사용하거나 기존 영역을 그대로 사용할 수도 있습니다.",
            "사용할 번역 서비스와 필요한 설정을 확인하세요. 이 단계에서는 실제 번역을 요청하지 않습니다.",
            "설정이 완료되었습니다. 필요하면 인식 방식과 입력 트리거를 확인하세요. 아래 ‘번역 시작’을 누른 뒤 게임 창을 앞으로 가져와 주세요.",
            "게임 창을 앞으로 가져와 대사를 표시하세요. 첫 한국어 자막이 실제로 나타나면 자동으로 완료됩니다. 이 안내 창은 최소화해도 됩니다."];
        SetText(progress,$"{step+1} / 6 단계   "+string.Join("  ",Enumerable.Range(0,6).Select(i=>i<=step?"●":"○")));
        SetText(heading,Controller.Completed?"설정이 완료되었습니다!":titles[step]);
        SetText(description,Controller.Completed?"이제 게임을 실행하고 메인 화면의 ‘번역 시작’을 누르면 됩니다. 게임별 설정은 다음에도 그대로 사용할 수 있습니다.":descriptions[step]);
        for(int i=0;i<pages.Length;i++){pages[i].Visibility=i==step && !Controller.Completed?Visibility.Visible:Visibility.Collapsed;pages[i].IsEnabled=!busy;}
        string friendlyStatus=FriendlyWizardStatus(Controller.Step,Controller.Message,
            App.MockAi,Controller.HasError,Controller.Completed);
        SetText(status,busy?"작업 중입니다. 잠시 기다려 주세요.":friendlyStatus);
        status.ToolTip=Controller.Message.Contains("GDI 창 캡처",StringComparison.Ordinal)
            && friendlyStatus!=Controller.Message ? Controller.Message:null;
        bool showStatus=Controller.Step switch {
            FirstRunStep.Game => ShowGameStatus(Controller.Message,Controller.HasError),
            FirstRunStep.Area => ShowAreaStatus(Controller.Message,Controller.HasError),
            _ => true
        };
        status.Visibility=!busy && !showStatus ? Visibility.Collapsed:Visibility.Visible;
        Next.Content=Controller.Completed?"완료":Controller.Step switch {FirstRunStep.Profile=>"저장 후 다음",FirstRunStep.Game=>readiness?.GameReady==true && SameWindow(GameChoice.SelectedItem as WindowTarget,host.ConnectedTarget)?"다음":"선택한 창 연결",FirstRunStep.Service=>"설정 확인 후 다음",FirstRunStep.Start=>"번역 시작",FirstRunStep.FirstSubtitle=>"첫 자막 대기 중",_=>"다음"};
        gamePlaceholder.Visibility=GameChoice.SelectedItem is null?Visibility.Visible:Visibility.Collapsed;
        SetText(gamePlaceholder,!gameSearchCompleted?"실행 중인 게임 창을 찾고 있습니다…":candidates.Count==0?"실행 중인 게임 창이 없습니다.":"게임 창을 선택해 주세요.");
        retry.Content=Controller.Step switch {
            FirstRunStep.Profile => "기본 정보 보기",
            FirstRunStep.Game => "캡처 설정 보기",
            FirstRunStep.Area => "영역 설정 보기",
            FirstRunStep.Service => "번역 설정 보기",
            FirstRunStep.Start => "메인 화면 보기",
            FirstRunStep.FirstSubtitle => "상태 새로고침",
            _ => "다시 확인"
        };
        Next.IsEnabled=!busy && (Controller.Completed || Controller.Step!=FirstRunStep.FirstSubtitle)
            && (Controller.Step!=FirstRunStep.Profile || ProfileChoice.SelectedItem is Profile);
        if(Controller.Step==FirstRunStep.Game)Next.IsEnabled=!busy && (GameChoice.SelectedItem is not null || readiness?.GameReady==true);
        if(Controller.Step==FirstRunStep.Area)Next.IsEnabled=!busy && readiness?.AreaReady==true;
        if(Controller.Step==FirstRunStep.Service)Next.IsEnabled=!busy && readiness?.ServiceReady==true
            && readiness.ProfileId==host.WizardProfile.Id;
        Previous.IsEnabled=!busy && step>0;retry.IsEnabled=!busy;
        retry.Visibility=Visibility.Visible;
        retry.ToolTip=Controller.Step==FirstRunStep.FirstSubtitle
            ? "현재 진행 상태를 확인합니다. OCR이나 번역을 다시 시작하지 않습니다." : null;
        cancel.Content=Controller.Completed?"닫기"
            : Controller.Step==FirstRunStep.FirstSubtitle?"마법사 닫기":"취소";
        if (Controller.Step == FirstRunStep.Service) {
            var provider = host.WizardSelectedProvider;
            bool local = provider.Provider == TranslationProvider.HyMtLocal;
            bool ollama = provider.Provider == TranslationProvider.Ollama;
            apiSettings.Visibility = provider.Credentials.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            installModel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            installModel.IsEnabled = local && readiness is { ServiceReady: false } checkedState
                && checkedState.ProfileId == host.WizardProfile.Id
                && checkedState.CheckedLocalModel == host.WizardSelectedLocalModel;
            string hint = local ? "모델 설치는 다운로드 동의를 받은 뒤 진행합니다."
                : ollama ? "번역 서비스 설정에서 Ollama 서버 주소와 모델을 지정하세요."
                : "API 키는 기본 프로필의 공통 설정에서 관리합니다.";
            SetText(service, $"선택한 번역 서비스: {provider.DisplayName}\n{hint}");
        }
    }
    private static void SetText(TextBlock target,string text) {if(target.Text!=text)target.Text=text;}
}
