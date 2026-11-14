using KoTrans.Infrastructure;

namespace KoTrans.App;

public partial class MainWindow
{
    private async Task<ImageFrame?> CaptureMarkerObservationAsync(Profile profile,PixelRect? baseRegion,PixelRect sample,CancellationToken token)
    {
        return await (await Dispatcher.InvokeAsync(async ()=> {
            token.ThrowIfCancellationRequested();
            if(closing || Current.Id!=profile.Id || profile.CaptureMode!=CaptureMode.Window)return null;
            var chosen=RequireGameWindow();var client=getWindowBounds(chosen);var scale=getWindowScale(chosen);
            // PrintClient renders the whole client even for a crop. Avoid adding
            // high-frequency full renders to that optional capture mode.
            if(CaptureOriginalWindow(chosen,scale))return null;
            var basis=baseRegion??new PixelRect(0,0,client.Width,client.Height);
            if(sample.X<0 || sample.Y<0 || sample.X+(long)sample.Width>basis.Width || sample.Y+(long)sample.Height>basis.Height)return null;
            var local=sample with {X=sample.X+basis.X,Y=sample.Y+basis.Y};
            var plan=WindowCapturePlan.Create(chosen.Handle,client,local,scale,false);
            if(plan.ScreenArea.Width>512 || plan.ScreenArea.Height>512)return null;
            EnsureCaptureSafe(plan.ScreenArea,plan.Scale is {} mapped ? new CaptureCover(mapped.OutputWindow,mapped.Destination):null);
            var frame=await Task.Run(()=>ScreenCapture.Capture(plan,token),token);
            if(getWindowBounds(chosen)!=client || getWindowScale(chosen)!=scale)return null;
            return frame;
        }));
    }
    private MarkerLearningOcrService? markerLearningSession;
    private Task markerLearningRetired=Task.CompletedTask;
    private string? markerLearningLastStatus;
    private bool markerLearningResetting;
    // Background WPF tests can decline a destructive action without showing a native dialog.
    internal Func<bool>? ConfirmMarkerLearningResetOverride { get; set; }
    internal string MarkerLearningDirectory=>Path.Combine(store.DirectoryPath,"MarkerLearning");
    private void RefreshMarkerLearningControls()
    {
        if(ResetMarkerLearningButton is null)return;
        var profile=Current;
        bool supported=profile.CaptureMode==CaptureMode.Window && GameMarkerGeometry.Supported(profile.OcrOptions());
        ResetMarkerLearningButton.IsEnabled=supported && profile.GameWindow is {ExecutablePath.Length:>0,WindowClass.Length:>0} && !markerLearningResetting;
        // The always-visible explanation lives in XAML; this field is only for
        // actionable diagnostics and live learning progress, not a duplicate caption.
        MarkerLearningStatus.Text=!supported ? "현재 설정에서는 자동 학습을 지원하지 않습니다. 특정 윈도우 · 일본어 가로쓰기 · PaddleOCR에서 사용합니다."
            : markerLearningLastStatus??"";
    }
    private static string MarkerLearningSummary(MarkerLearningStatus status)=>status.StorageError
        ? "문장 끝 표시 자동 학습 · 저장하지 못해 이번 실행에서만 사용합니다."
        : $"문장 끝 표시 자동 학습 · 확정 {status.Confirmed}개 · 본문이 불확실하면 제외하지 않습니다.";
    private void RetireMarkerLearning()
    {
        markerLearningLastStatus=null;
        if(markerLearningSession is not {} session)return;
        markerLearningSession=null;
        markerLearningRetired=Task.WhenAll(markerLearningRetired,session.StopAsync());
        retiring.Add(markerLearningRetired);
    }
    internal async Task<MarkerLearningOcrService?> PrepareMarkerLearningAsync(Profile profile,GameWindowIdentity? game,IOcrService engine,CancellationToken token)
    {
        await markerLearningRetired;
        token.ThrowIfCancellationRequested();
        if(profile.CaptureMode!=CaptureMode.Window || !GameMarkerGeometry.Supported(profile.OcrOptions()))return null;
        if(game is null || game.ExecutablePath.Length==0 || game.WindowClass.Length==0) {
            MarkerLearningStatus.Text=markerLearningLastStatus="게임의 고유 식별 정보를 확인하지 못해 학습하지 않습니다. 대상 창을 다시 선택하세요.";return null;
        }
        var learningStore=new MarkerLearningStore(MarkerLearningDirectory,MarkerLearningStore.Key(profile,game));
        MarkerLearningDocument saved;
        try {saved=await learningStore.LoadAsync(token);}
        catch(InvalidDataException) {
            MarkerLearningStatus.Text=markerLearningLastStatus="학습 정보를 읽지 못해 학습을 보류했습니다. 원본은 보존했습니다. 학습 초기화 후 다시 시작하세요.";return null;
        }
        token.ThrowIfCancellationRequested();
        if(closing || Current.Id!=profile.Id)return null;
        // Learning is automatic for supported inputs, including profiles saved
        // with the retired Off/Observe setting. Keep the existing body guards.
        const MarkerLearningMode mode=MarkerLearningMode.Apply;
        var learner=new GameMarkerLearner(mode,saved.Patterns);
        MarkerLearningOcrService? session=null;
        session=new(engine,mode,learner,learningStore,status=> {
            if(closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)return;
            // Status is typed/count-only. Do not queue an update on every capture.
            string text=MarkerLearningSummary(status);
            Dispatcher.BeginInvoke(()=> {
                if(closing || Current.Id!=profile.Id || !ReferenceEquals(markerLearningSession,session))return;
                markerLearningLastStatus=text;MarkerLearningStatus.Text=text;
                // The pipeline owns session-tagged debug counters. Queued UI
                // updates must not overwrite newer measurements with old values.
            });
        });
        markerLearningLastStatus=MarkerLearningSummary(learner.Status);MarkerLearningStatus.Text=markerLearningLastStatus;
        markerLearningSession=session;return session;
    }
    internal async Task<bool> ResetMarkerLearningAsync()
    {
        if(markerLearningResetting || Current.GameWindow is not {} game || game.ExecutablePath.Length==0 || game.WindowClass.Length==0)return false;
        var profile=Current;var key=MarkerLearningStore.Key(profile,game);
        markerLearningResetting=true;StopTranslation();RefreshMarkerLearningControls();
        try {
            await markerLearningRetired;
            await new MarkerLearningStore(MarkerLearningDirectory,key).ResetAsync();
            if(Current.Id==profile.Id)MarkerLearningStatus.Text=markerLearningLastStatus="이 게임의 학습 정보를 초기화했습니다. 다시 시작하면 새로 관찰합니다.";
            return true;
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException) {
            if(Current.Id==profile.Id)ShowFeedback(FeedbackKind.Error,"학습 정보를 초기화하지 못했어요","폴더 권한을 확인하고 다시 시도하세요.");
            return false;
        } finally {markerLearningResetting=false;RefreshMarkerLearningControls();}
    }
    private async void ResetMarkerLearningClicked(object sender, RoutedEventArgs e)
    {
        if (!ResetMarkerLearningButton.IsEnabled) return;
        bool confirmed = ConfirmMarkerLearningResetOverride?.Invoke()
            ?? (MessageBox.Show(this,
                "현재 게임과 OCR 모델의 문장 끝 표시 학습 정보를 초기화할까요?\n진행 중인 번역은 중지되고, 다음 시작부터 다시 학습합니다.",
                "문장 끝 표시 학습 초기화", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        if (confirmed) await ResetMarkerLearningAsync();
    }
}
