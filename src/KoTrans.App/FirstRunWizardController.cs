namespace KoTrans.App;

internal enum FirstRunStep { Profile, Game, Area, Service, Start, FirstSubtitle }
internal sealed record FirstRunReadiness(Guid ProfileId, bool ProfileReady, bool GameReady, bool AreaReady,
    bool ServiceReady, bool Running, string Configuration, string Message, LocalTranslationModel? CheckedLocalModel = null);

// Transient guidance only. Profile/SettingsStore remain the source of truth.
// No captures, model initialization, credentials, network calls or persistent wizard flags.
internal sealed class FirstRunWizardController : IDisposable
{
    internal FirstRunStep Step { get; private set; }
    internal bool Completed { get; private set; }
    internal bool Waiting { get; private set; }
    internal Guid ProfileId { get; private set; }
    internal string Message { get; private set; } = "사용할 게임 프로필과 원문 언어를 확인해 주세요.";
    private object? translationSession;
    private long minimumVersion = -1;
    private string? configuration;
    private string? error;
    private readonly System.Collections.Generic.Dictionary<PipelineStatusArea, (string Message, bool IsError)> pipelineIssues = new();
    private bool refreshError;
    private string? CurrentError => error ?? pipelineIssues.Values.Where(v => v.IsError).Select(v => v.Message).LastOrDefault()
        ?? pipelineIssues.Values.Select(v => v.Message).LastOrDefault();
    internal bool HasError => CurrentError is not null;
    private bool disposed, armed, displayedWhileStarting;
    internal void Refresh(FirstRunReadiness state)
    {
        if(disposed)return;
        // A successful readiness query resolves a failure of that query only.
        // It must not erase a separate capture/OCR/translation failure.
        if(refreshError)ClearError();
        if(ProfileId!=state.ProfileId) {
            bool changed=ProfileId!=Guid.Empty;ProfileId=state.ProfileId;
            ResetTranslation();ClearError();Step=FirstRunStep.Profile;configuration=state.Configuration;
            Message=changed ? "프로필이 변경되었습니다. 현재 게임의 설정을 확인해 주세요." : state.Message;
        }
        if(configuration!=state.Configuration) {
            configuration=state.Configuration;ResetTranslation();ClearError();
            if(Step>FirstRunStep.Start)Step=FirstRunStep.Start;
        }
        // Once the first real Korean subtitle appeared, stopping capture or
        // moving away from the game must not revoke that completed milestone.
        // Explicit navigation and configuration/profile changes still reset it.
        if(Completed) {Message="설정이 완료되었습니다!";return;}
        var firstMissing=!state.ProfileReady?FirstRunStep.Profile:!state.GameReady?FirstRunStep.Game:
            !state.AreaReady?FirstRunStep.Area:!state.ServiceReady?FirstRunStep.Service:FirstRunStep.Start;
        if(Step>firstMissing && firstMissing<FirstRunStep.Start) {Step=firstMissing;ResetTranslation();}
        if(Waiting && !state.Running && !armed) {ResetTranslation();ClearError();Step=FirstRunStep.Start;Message="번역이 중지되었습니다. 다시 시작해 주세요.";}
        else if(!Completed)Message=CurrentError??state.Message;
    }
    internal bool Next(FirstRunReadiness state)
    {
        if(disposed)return false;
        Refresh(state);
        bool ready=Step switch {
            FirstRunStep.Profile=>state.ProfileReady,FirstRunStep.Game=>state.GameReady,
            FirstRunStep.Area=>state.AreaReady,FirstRunStep.Service=>state.ServiceReady,_=>false};
        if(ready)Step++;
        return ready;
    }
    internal void Back()
    {
        if(disposed || Step==FirstRunStep.Profile)return;
        ResetTranslation();ClearError();Step--;
    }
    internal void Arm(Guid profile)
    {
        if(disposed || Step!=FirstRunStep.Start || profile!=ProfileId)return;
        ResetTranslation();ClearError();armed=true;
    }
    internal void Bind(object session,Guid profile,long afterVersion=-1)
    {
        if(disposed || !armed || profile!=ProfileId)return;
        translationSession=session;minimumVersion=afterVersion;
    }
    internal void Started(bool success,string? effectiveConfiguration=null)
    {
        if(disposed || !armed)return;
        armed=false;
        if(!success || translationSession is null) {ResetTranslation();ReportError("시작하지 못했습니다. 안내된 설정을 확인하고 다시 시도해 주세요.");return;}
        if(effectiveConfiguration is not null)configuration=effectiveConfiguration;
        Step=FirstRunStep.FirstSubtitle;Waiting=true;Completed=displayedWhileStarting;
        Message=Completed?"설정이 완료되었습니다!":CurrentError??"대사가 나올 때까지 게임을 진행해 주세요.";
    }
    internal void SubtitleApplied(object session,Guid profile,PipelineUpdate update,bool displayed,bool mock)
    {
        if(disposed || (!Waiting && !armed) || !ReferenceEquals(session,translationSession) || profile!=ProfileId
            || mock || !displayed || update.Version<=minimumVersion || update.State!=PipelineState.Watching
            || update.OcrRequests<1 || string.IsNullOrWhiteSpace(update.Original) || string.IsNullOrWhiteSpace(update.Translation))return;
        if(armed)displayedWhileStarting=true;
        else {Completed=true;Message="설정이 완료되었습니다!";}
    }
    internal void Report(string message) {if(!disposed && !Completed)Message=CurrentError??message;}
    internal void ReportError(string message) {
        if(!disposed && !Completed) {Message=error=message;refreshError=false;}
    }
    internal void ReportRefreshIssue(string message) {
        if(!disposed && !Completed && !HasError) {
            Message=error=message;refreshError=true;
        }
    }
    internal void ReportPipelineIssue(string message,PipelineStatusArea area,bool isError=true) {
        if(!disposed && !Completed) {
            if(refreshError)error=null;
            refreshError=false;
            area=area==PipelineStatusArea.None?PipelineStatusArea.Input:area;
            // A capture wait must not replace an unresolved AI error. Preserve
            // independent areas until each reports its own recovery.
            if(isError || !pipelineIssues.TryGetValue(area,out var previous) || !previous.IsError)
                pipelineIssues[area]=(message,isError);
            Message=CurrentError??message;
        }
    }
    internal void RecoverPipelineAreas(PipelineStatusArea recovered) {
        // A new OCR pass does not clear an unresolved AI translation failure.
        if(disposed || Completed)return;
        foreach(var area in pipelineIssues.Keys.ToArray())
            if((area & recovered)!=0)pipelineIssues.Remove(area);
        if(CurrentError is {} remaining)Message=remaining;
    }
    internal void ClearError() {error=null;pipelineIssues.Clear();refreshError=false;}
    private void ResetTranslation() {translationSession=null;Waiting=false;Completed=false;armed=false;displayedWhileStarting=false;minimumVersion=-1;}
    public void Dispose() {disposed=true;ResetTranslation();}
}
