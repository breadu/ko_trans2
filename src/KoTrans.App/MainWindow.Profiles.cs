using System.Collections.Generic;
using KoTrans.Infrastructure;
namespace KoTrans.App;

public partial class MainWindow
{
    private readonly Func<IReadOnlyList<WindowTarget>> findWindows;
    private readonly Func<WindowTarget, PixelRect> getWindowBounds;
    private readonly Func<WindowTarget, WindowScaleTransform?> getWindowScale;
    private readonly Func<WindowTarget,bool> useOriginalWindowCapture;
    private readonly WindowPrintCapture originalWindowCapture=new();
    private readonly List<TranslationSettingsWindow> profileDialogs = [];
    internal WindowTarget? ConnectedTarget => target;
    private const string ChooseGameWindowMessage = "번역할 게임 창을 선택하세요.";
    internal void ReconnectTarget(IReadOnlyList<WindowTarget>? knownWindows=null)
    {
        RefreshCaptureModeControls();
        target = null;
        if (Current.CaptureMode != CaptureMode.Window) { RefreshCaptureAreaSummary(); UpdateLauncherTargetLabel(); return; }
        if (Current.GameWindow is not { } identity) { TargetLabel.Text = ChooseGameWindowMessage; RefreshCaptureAreaSummary(); UpdateLauncherTargetLabel(); return; }
        target = WindowTargets.Reconnect(identity, knownWindows??findWindows(), out var ambiguous);
        debugTargetState = target is not null ? DebugReason.Connected : ambiguous ? DebugReason.Ambiguous : DebugReason.Missing;
        RefreshDebugContext();
        string saved = $"대상 게임: {identity.Title} · {identity.ProcessName}";
        TargetLabel.Text = target is null
            ? saved + (ambiguous ? "\n같은 게임 창이 여러 개입니다. [대상 창 선택]으로 지정하세요." : "\n연결 대기 · 게임을 실행하세요. 실행 중이라면 대상 창을 다시 선택하세요.")
            : saved + "\n연결됨";
        UpdateLauncherTargetLabel();
        if (target is not null)
            try { var bounds = getWindowBounds(target); if (Current.WindowCaptureRegion is { } region) WindowCaptureBounds.Resolve(bounds, region); }
            catch (InvalidDataException) { TargetLabel.Text = saved + "\n게임 창 크기가 바뀌었습니다. [영역 지정]으로 다시 선택하세요."; }
            catch (CaptureUnavailableException) { TargetLabel.Text = saved + "\n게임 창을 복원한 뒤 영역을 확인하세요."; }
        RefreshCaptureAreaSummary();
    }
    internal void SelectGameWindow(WindowTarget chosen)
    {
        chosen.Identity.Validate();
        // Resolve an optional legacy crop before changing settings. A minimized game must still be selectable/savable.
        var region = Current.WindowCaptureRegion;
        if (Current.GameWindow is { } saved && !saved.Matches(chosen.Identity) && !saved.MatchesStableIdentity(chosen.Identity)
            && !(target?.Handle == chosen.Handle && target.ProcessId == chosen.ProcessId && target.StartedTicks == chosen.StartedTicks)) region = null;
        string? notice = null;
        if (region is null && Current.CaptureArea != new PixelRect(0,0,800,300)) {
            try {
                region = WindowCaptureBounds.FromScreen(getWindowBounds(chosen), Current.CaptureArea);
                if (region is null) notice = "게임 창 안에서 대사 영역을 지정하세요. 기존 화면 영역은 보존했습니다.";
            }
            catch (CaptureUnavailableException) { notice = "창 복원 후 대사 영역을 확인하세요. 대상 창 선택은 저장합니다."; }
        }
        InvalidateCaptureInput("캡처 대상이 변경되어 이전 문장 해설을 비웠습니다.");
        Current.GameWindow = chosen.Identity; Current.WindowCaptureRegion = region; target = chosen;
        Current.CaptureMode = CaptureMode.Window;
        RefreshCaptureModeControls();
        bool wasChanging = changing; changing = true; InputMode.SelectedIndex = CaptureModeToSelectionIndex(CaptureMode.Window); changing = wasChanging;
        TargetLabel.Text = $"대상 게임: {chosen.Title} · {chosen.ProcessName}" + (notice is null ? "" : "\n" + notice);
        UpdateLauncherTargetLabel();
        RefreshCaptureAreaSummary();
    }
    internal WindowTarget RequireGameWindow()
    {
        if (target is not null) {
            try { getWindowBounds(target); return target; }
            // Resolve already validated HWND/process identity before classifying a normal pause.
            // Hidden windows may disappear from enumeration; retain the selected live binding.
            catch (CaptureUnavailableException ex) when (ex.IsExpectedWait) { return target; }
            catch (CaptureUnavailableException) { }
        }
        ReconnectTarget();
        return target ?? throw new CaptureUnavailableException(TargetLabel.Text);
    }
    internal TranslationSettingsWindow CreateTranslationSettings()
    {
        var profile = Current;
        var dialog = new TranslationSettingsWindow(profile, settings.Hotkeys, vault,
            () => settings.CanEditGlobal && settings.ActiveProfileId == profile.Id,
            () => settings.ActiveProfileId == profile.Id);
        ConfigureLocalModelEditor(dialog.Editor, profile.Id, dialog);
        profileDialogs.Add(dialog);
        dialog.Closed += (_, _) => profileDialogs.Remove(dialog);
        return dialog;
    }
    private (IReadOnlyList<CharacterEntry> Characters, string Prompt) PrepareProfileTranslationContext()
    {
        var profile = Current;
        var characters = profile.ShowTranslation && TranslationProviderCatalog.Get(profile.Translation.Provider).SupportsCharacterDictionary ? LoadProfileCharacters() : [];
        return (characters, profile.ShowTranslation ? TranslationPrompt.Build(characters) : "");
    }
    internal TranslationPipeline CreateProfilePipeline(IOcrService engine, ITranslationService? service, Func<CancellationToken, Task<ImageFrame>> capture,
        (IReadOnlyList<CharacterEntry> Characters, string Prompt)? preparedContext = null)
    {
        var profile = Current; var options = profile.OcrOptions(); var aiOptions = profile.Translation with { };
        var (characters, prompt) = preparedContext ?? PrepareProfileTranslationContext();
        var session = profile.ShowTranslation ? contexts.Get(profile.Id, options, aiOptions, prompt) : new TranslationSession();
        TranslationPipeline? active = null;
        active = new(engine, service, capture, options, aiOptions, session, prompt, gameQa: gameQa, qaProfile: GameQaProfile.From(profile), translationEnabled: profile.ShowTranslation,
            onOcrAccepted: (version, text, token) => CopyAcceptedOcrAsync(active!, profile.Id, version, text, token), diagnostics: debugTelemetry, characters: characters);
        if(service is MockTranslationService)mockTranslationPipeline=active;
        return active;
    }
    internal bool WatchPreference => RecognitionInputPlan.For(Current, true).WatchScreen;
    internal IReadOnlyList<CharacterEntry> LoadProfileCharacters() => Current.CharacterDictionaryEnabled
        ? CharacterDictionary.Load(Current.CharacterDictionaryPath) : [];
}
