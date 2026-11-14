using System.Collections.Generic;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

namespace KoTrans.App;

public partial class MainWindow
{
    private readonly ExplanationSession explanations = new();
    private JapaneseReadingService? japanese;
    private CancellationTokenSource? studyCancellation;
    private readonly List<Task> studyTasks = [];
    private long studyVersion;
    private bool studyBusy;
    private string? activeStudySource;
    private ImageFrame? manualRecognizedFrame;
    private string? manualRecognizedText;
    internal StudyWindow EnsureStudy()
    {
        if (study is not null) { study.BindProfile(Current); return study; }
        study = new StudyWindow(settings, profile: Current);
        study.Hidden += () => CancelStudy("학습창을 숨겼습니다. 진행 중인 분석은 취소했습니다.");
        study.PreferencesChanged += async () => { if (!closing) await SaveSettings(errorPrefix: "문장 해설창 위치 저장 실패: "); };
        return study;
    }
    private void QueueStudy(Task task) { studyTasks.RemoveAll(t => t.IsCompleted); studyTasks.Add(task); }
    private void CancelStudy(string message = "프로필·입력 변경으로 학습 요청을 취소했습니다.")
    {
        studyVersion++; studyCancellation?.Cancel(); explanations.Cancel(); studyBusy = false; activeStudySource = null;
        study?.SetBusy(false, message);
        UpdateOcrExecution();
    }
    private void InvalidateStudy(string reason = "프로필 또는 입력 설정이 변경되어 이전 문장 해설을 비웠습니다.")
    {
        CancelStudy(); manualRecognizedFrame = null; manualRecognizedText = null; study?.Clear(); study?.SetSource("");
        study?.ShowNotice(reason + $"\n문장 해설을 다시 보려면 현재 문장 학습 단축키({settings.Hotkeys.Explain})를 누르세요.");
    }
    private void LearnCurrent(object sender, RoutedEventArgs e) => QueueStudy(RunStudyAsync(CaptureStudySourceAsync));
    private void ExplainOverlaySubtitle(TranslationWindow window)
    {
        if (closing || !ready || !ReferenceEquals(overlay, window)) return;
        string source = window.CurrentExplanationSource;
        if (studyBusy) {
            if (source == activeStudySource) return;
            CancelStudy();
        }
        QueueStudy(RunStudyAsync(string.IsNullOrWhiteSpace(source)
            ? CaptureOverlayStudySourceAsync
            : _ => Task.FromResult(source), requestedSource: source));
    }
    private async Task<string> CaptureOverlayStudySourceAsync(CancellationToken token)
    {
        var source = await CaptureStudySourceAsync(token);
        if (string.IsNullOrWhiteSpace(source)) throw new TranslationException("해설할 문장이 없습니다. 게임 대사가 표시된 상태에서 다시 시도하세요.");
        return source;
    }
    private void LearnRecognized(object sender, RoutedEventArgs e)
    {
        var text = OcrText.Text; QueueStudy(RunStudyAsync(_ => Task.FromResult(text)));
    }
    private async Task<string> CaptureStudySourceAsync(CancellationToken token)
    {
        var profile = Current;
        if (profile.CaptureMode == CaptureMode.Clipboard)
        {
            if (!System.Windows.Clipboard.ContainsText()) throw new TranslationException("클립보드에 학습할 텍스트가 없습니다.");
            return System.Windows.Clipboard.GetText();
        }
        ocrFactory.Validate(profile.OcrOptions());
        if (profile.CaptureMode == CaptureMode.Window) target = RequireGameWindow();
        var frame = await CaptureProfileAsync(profile.CaptureMode, profile.CaptureArea, target, profile.CaptureMode == CaptureMode.Window ? WindowRegion(profile, target!) : null, true, token);
        token.ThrowIfCancellationRequested();
        return await RecognizeStudyFrameAsync(profile, frame, token);
    }
    // The captured-frame boundary also permits Background tests without any screen or AI access.
    internal async Task<string> RecognizeStudyFrameAsync(Profile profile, ImageFrame frame, CancellationToken token)
    {
        if (pipeline?.TryGetRecognized(frame, out var cached) == true) return cached;
        if (manualRecognizedFrame is not null && StudyFrame.SamePixels(manualRecognizedFrame, frame)) return manualRecognizedText ?? "";
        var service = CurrentOcrService();
        var options = profile.OcrOptions();
        var qaId = gameQa.Begin(frame, options, GameQaProfile.From(profile));
        var activity = new StudyOcrActivity(service, token, profile.Id, studyVersion);
        studyOcrActivities.Add(activity); UpdateOcrExecution();
        try
        {
            var result = await service.RecognizeAsync(frame, options, token);
            token.ThrowIfCancellationRequested(); gameQa.Complete(qaId, result, result.Text, false);
            manualRecognizedFrame = frame; manualRecognizedText = result.Text; return result.Text;
        }
        catch (OperationCanceledException) { gameQa.Finish(qaId, "canceled"); throw; }
        catch (Exception ex) {
            gameQa.Finish(qaId, "failed");
            if (ex is OcrInitializationException && CanObserveStudyOcr(activity)
                && ReferenceEquals(observedOcr, service)) ocrPresentationError = true;
            throw;
        }
        finally { studyOcrActivities.Remove(activity); UpdateOcrExecution(); }
    }
    private async Task RunStudyAsync(Func<CancellationToken, Task<string>> source, bool force = false, bool localOnly = false, string? requestedSource = null)
    {
        if (!ready || closing || studyBusy) return;
        CancelStudy(); studyBusy = true; activeStudySource = requestedSource; long serial = studyVersion; var profile = Current;
        var options = profile.Translation with { }; var language = profile.Language == "일본어" ? "ja" : "en";
        using var cancellation = new CancellationTokenSource(); studyCancellation = cancellation;
        string? attemptedSource = null;
        try
        {
            // Capture before opening a new study window so it cannot obscure the source.
            study?.SetBusy(true, "학습 원문을 준비합니다…");
            var text = ExplanationPrompt.ValidateInput(await source(cancellation.Token));
            attemptedSource = text;
            language = ExplanationPrompt.DetectLanguage(text, language);
            cancellation.Token.ThrowIfCancellationRequested();
            if (serial != studyVersion || Current.Id != profile.Id) return;
            var window = EnsureStudy(); window.SetSource(text, language); window.Clear(); window.ShowReader();
            window.SetBusy(true, localOnly ? "일본어 읽기 준비 중 · 외부 API 미사용" : "학습 분석 중… 번역 감시는 독립적으로 유지됩니다.");
            JapaneseReading? reading = null;
            if (language == "ja" || localOnly)
            {
                japanese ??= new JapaneseReadingService();
                try { reading = await Task.Run(() => japanese.Read(text, cancellation.Token), cancellation.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                { if (localOnly) throw new TranslationException("로컬 일본어 사전을 읽을 수 없습니다. IpaDic 파일을 확인하세요."); }
            }
            if (localOnly)
            { cancellation.Token.ThrowIfCancellationRequested(); if (serial == studyVersion && reading is not null) { window.RenderReading(reading); window.SetStatus("로컬 읽기 완료 · AI 요청 0회"); } return; }
            cancellation.Token.ThrowIfCancellationRequested();
            if (serial != studyVersion || Current.Id != profile.Id || closing) return;
            var service = App.MockAi ? (IExplanationService)new MockExplanationService() : ExplanationService.CreateUsingVault(options, vault);
            var result = await explanations.AnalyzeAsync(profile.Id, new(text, language, options), service, force);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result is null || serial != studyVersion || Current.Id != profile.Id || closing) return;
            window.Render(result.Study, reading);
            window.SetStatus((App.MockAi ? "[모의 학습] " : "") + (result.FromCache ? "최근 학습 결과 재사용" : "학습 분석 완료") + $" · 학습 {explanations.Requests}회 / HTTP {explanations.HttpAttempts}회 / 재사용 {explanations.CacheHits}회" + (language == "ja" && reading is null ? " · 로컬 사전 읽기 실패" : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (serial != studyVersion || closing) return;
            var message = ex is TranslationException or CaptureUnavailableException or OcrInitializationException ? ex.Message : "학습 요청을 처리하지 못했습니다. 입력·키·모델·캡처 상태를 확인하고 다시 요청하세요.";
            var window = EnsureStudy();
            // A failed OCR/API attempt must not leave a previous sentence's
            // translation and vocabulary behind under a new error message.
            window.SetSource(attemptedSource ?? "", language);
            window.Clear();
            window.ShowReader();
            window.ShowError(message);
        }
        finally
        {
            if (studyCancellation == cancellation) studyCancellation = null;
            if (serial == studyVersion) { studyBusy = false; activeStudySource = null; study?.FinishBusy(); }
        }
    }
}
