using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KoTrans.App;

namespace KoTrans.WpfTests;

internal static class Program
{
    private static bool desktop;
    private static int assertions, visibleWindows, nativeWindows;
    private static readonly List<Window> windows = [];
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless child verifies real cross-process ownership/IPC; no Application or HWND.
        if (args.Length >= 2 && args[0] == "--activation-client") {
            KoTrans.App.App.InitializeCapturePolicy(KoTrans.Core.AppLaunchOptions.Parse(args.Skip(2).ToArray(), args[1]));
            using var unexpectedOwner = InstanceActivation.TryOwn(args[1], () => { });
            if (unexpectedOwner is not null) return 2;
            return InstanceActivation.RequestAsync(args[1]).GetAwaiter().GetResult() ? 0 : 1;
        }
        if (args.SequenceEqual(new[] { "--list" }))
        { Console.WriteLine("Background: Wpf (Study, Overlay, Profiles, Settings, stale pipeline UI)\nDesktop: WindowLifecycle (Show/Hide/restore/remote scroll), OverlayDrag (explicit physical mouse test), DictionaryHoverCapture (tooltip A/B real GDI, synthetic frame), StudyResize (secondary-monitor native hit/cursor diagnostic), WindowResize (main frame regression and isolated browser observation), MagpieNotifications (hidden native broadcast reception/lifetime; no shown windows/input), RecordingMode Normal/Debug/Recording/RecordingDebug (native capture affinity and OCR overlap); resize diagnostics move the mouse"); return 0; }
        bool recordingTest = args.Length == 3 && args[0] == "--desktop" && args[1] == "RecordingMode"
            && args[2] is "Normal" or "Debug" or "Recording" or "RecordingDebug";
        desktop = recordingTest || args.Length == 2 && args[0] == "--desktop" && args[1] is "WindowLifecycle" or "OverlayDrag" or "DictionaryHoverCapture" or "StudyResize" or "WindowResize" or "MagpieNotifications";
        if (!desktop && args.Length != 0 && !args.SequenceEqual(new[] { "Wpf" }))
        { Console.Error.WriteLine("Unknown suite. Use Wpf, --desktop RecordingMode Normal/Debug/Recording/RecordingDebug, or explicitly --desktop WindowLifecycle/OverlayDrag/DictionaryHoverCapture/StudyResize/WindowResize/MagpieNotifications."); return 2; }
        Application? application = null;
        var clock = Stopwatch.StartNew();
        var result = 0;
        try
        {
            if (desktop) DesktopTestPolicy.RequireInteractiveDesktop();
            // Do not construct the product App: its OnStartup opens MainWindow and user settings.
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/KoTrans2;component/Styles.xaml", UriKind.Relative) });
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            { if (!desktop && sender is Window { IsVisible: true }) throw new InvalidOperationException("Background test displayed a Window. Move it to Desktop."); }));
            if (desktop) {
                if (recordingTest) Case("RecordingMode/" + args[2], () => RecordingModeTests.Desktop(args[2]));
                else if (args[1] == "OverlayDrag") Case("OverlayDrag", DesktopDrag.Run);
                else if (args[1] == "StudyResize") Case("StudyResize", StudyResizeDesktopTests.Run);
                else if (args[1] == "WindowResize") Case("WindowResize", WindowResizeDesktopTests.Run);
                else if (args[1] == "DictionaryHoverCapture") Case("DictionaryHoverCapture", DictionaryHoverDesktopTests.Run);
                else if (args[1] == "MagpieNotifications") Case("MagpieNotifications", MagpieNotificationDesktopTests.Run);
                else Case("WindowLifecycle", DesktopTests.WindowLifecycle);
            }
            else
            {
                Case("Opacity track position, keyboard steps and profile persistence", OpacitySliderTests.TrackAndKeyboard);
                Case("UI navigation preserves newer intent during delayed saves", UiTransitionReviewTests.NavigationDuringSave);
                Case("Capture UI changes cancel and reject old input work", UiTransitionReviewTests.ChangedCaptureInputRejectsOldWork);
                Case("Late capture completion preserves new UI state", UiTransitionReviewTests.CaptureCompletionKeepsNewUiState);
                Case("First-run wizard session, completion, stale/mock and retry guards",FirstRunWizardTests.Controller);
                Case("First-run review: independent failures and main-start lifetime",FirstRunReviewTests.Run);
                Case("First-run wizard nonmodal UI, existing setup, capture and cancellation",FirstRunWizardTests.Ui);
                Case("First-run wizard placement survives settings reload",FirstRunWizardTests.PlacementRestoration);
                Case("First-run wizard pending discovery, close and durable save failure",FirstRunWizardTests.LifetimeAndSaveFailure);
                Case("First-run area step failed save, actionable error and confirmed retry",FirstRunWizardTests.AreaSaveFailure);
                Case("First-run game step quiet refresh, placeholders, direct pick and stale search",FirstRunWizardTests.GameStepRefreshAndPick);
                Case("First-run step settings navigation and dynamic item visibility",FirstRunWizardTests.StepPresentationAndDynamicScroll);
                Case("Local provider controls and automatic start continuation", LocalTranslationUiTests.Run);
                Case("Debug overlay independence, summaries, pause/report/privacy and lifecycle", DebugOverlayTests.Run);
                Case("Local model settings install/delete, silent start and topmost dialogs", LocalModelManagementUiTests.Run);
                Case("Local model choice, warnings, isolated management and stopped translation", LocalModelChoiceUiTests.Run);
                Case("Local dictionary profile, ADV/NVL delivery, cache isolation and both models", LocalDictionaryPipelineTests.Run);
                Case("Starting overlay precedes preparation and handles ready/failure/cancellation", TranslationStartupUiTests.Run);
                Case("Image file worker and bounds", WpfTests.ImageImport);
                Case("Recording launch policy and immutable process flags", RecordingModeTests.Policy);
                Case("Single instance activation, tray restore and IPC lifetime", InstanceAndSettingsPolishTests.Activation);
                Case("Profile labels, Japanese visibility, global panels and model input captions", InstanceAndSettingsPolishTests.Controls);
                Case("Game launch visibility, no translation and muted default profile color", GameLaunchTests.Controls);
                Case("Game executable contract and existing-window matching", GameLaunchTests.Contract);
                Case("Study rendering and state", WpfTests.Study);
                Case("Study English-only side pronunciation and Japanese furigana", WpfTests.StudyPronunciationByLanguage);
                Case("Ruby final-glyph to translation spacing", SubtitleUxTests.RubyGap);
                Case("Subtitle named colors and live profile preview", SubtitleUxTests.PreviewAndColors);
                Case("Original-only mode keeps automatic OCR and source subtitles", SubtitleUxTests.OriginalOnlyRecognition);
                Case("One-time initial-trigger and stopped subtitle instructions", SubtitleUxTests.Prompts);
                Case("Overlay furigana profile defaults, language and persistence", OverlayFuriganaTests.ProfileControls);
                Case("Overlay furigana ruby layout and dictionary interaction", OverlayFuriganaTests.RenderingAndLookup);
                Case("Overlay furigana cancellation and local-reading fallback", OverlayFuriganaTests.CancellationAndFallback);
                Case("Study Mock service", WpfTests.StudyMock);
                Case("Study settings/provider invalidation reason and recovery", StudyNoticeTests.SettingsInvalidation);
                Case("Study source card visibility across empty notices, errors and recovery", StudyNoticeTests.EmptySourceCard);
                Case("Study close-save, resident reuse and profile persistence", StudyPlacementTests.CloseAndProfiles);
                Case("Study monitors, mixed DPI and minimized/fallback preservation", StudyPlacementTests.MonitorChanges);
                Case("Study native frame gaps and client/caption protection", StudyResizeTests.Borders);
                Case("Study wheel distance, smooth frames and remote pages", StudyScrollTests.WheelAndPages);
                Case("Study scrolling cancellation, content replacement and lifetime", StudyScrollTests.CancellationAndReaderLifecycle);
                Case("Subtitle settings wheel routing, smooth movement and lifetime", SettingsScrollTests.SubtitleWheel);
                Case("All settings sections share smooth scrolling and profile cleanup", SettingsScrollTests.AllSettingsPages);
                Case("English word-only pronunciation and missing data", StudyAudioTests.VocabularyOnly);
                Case("Stale pronunciation lookup and safe MP3 presence", StudyAudioTests.StaleAndDiscovery);
                Case("Translation layout and state", WpfTests.Overlay);
                Case("Source word dictionary, Korean exclusion and equal top spacing", WordDictionaryTests.WordsAndLayout);
                Case("Japanese continuous hover blocks, fractional DPI and no tooltip", WordDictionaryTests.ContinuousHighlight);
                Case("Dictionary stale analysis and safe failure", WordDictionaryTests.StaleAndFailure);
                Case("Dictionary profile persistence and chrome/settings synchronization", WordDictionaryTests.ProfileSynchronization);
                Case("Dictionary popup launch policy, DPI geometry and canceled failures", WordDictionaryTests.PopupPolicyAndFailure);
                Case("Dictionary saved monitor, mixed DPI and disconnected fallback", DictionaryPlacementTests.MonitorRestoration);
                Case("Dictionary close-save, live reuse and profile rebind", DictionaryPlacementTests.CloseAndRebind);
                Case("Dictionary async save failure and independent profile persistence", DictionaryPlacementTests.AsyncSaveAndPersistence);
                Case("Overlay interaction and profile persistence", OverlayTests.Interaction);
                Case("Overlay service, capsule and activity layout", OverlayActivityTests.LayoutAndCommands);
                Case("Magpie notification order, focus guards and overlay lifetime", MagpieOverlayOrderTests.NotificationsAndLifetime);
                Case("Overlay service button restores main in both click-through modes", OverlayServiceTests.RestoreMain);
                Case("Subtitle display cycle, settings synchronization, cached content and missing OCR/translation", SubtitleDisplayModeTests.LayoutAndCache);
                Case("Subtitle display changes preserve unresolved notices and live content", SubtitleDisplayModeTests.NoticePriority);
                Case("Subtitle mode tooltips follow idle/running state without changing commands", SubtitleDisplayModeTests.TooltipSynchronization);
                Case("Overlay busy lifecycle and real stop command", OverlayActivityTests.PipelineLifecycle);
                Case("Overlay empty OCR, faults and clipboard cancellation", OverlayActivityTests.EmptyAndFaultedOcr);
                Case("Overlay close stops OCR and discards late results", OverlayTests.CloseStopsWork);
                Case("Overlay replacement/reopen lifecycle", OverlayStyleTests.Lifecycle);
                Case("Original/translation style and color picker", OverlayStyleTests.Styles);
                Case("Profile controls and restoration", WpfTests.Profiles);
                Case("Catalog OCR selection and third-engine pipeline", OcrEngineUiTests.Selection);
                Case("Compact launcher, mascot and profile/global navigation", LauncherUiTests.LayoutAndNavigation);
                Case("Launcher help path, shell activation and responsive actions", LauncherHelpTests.Run);
                Case("Shared ComboBox styles across main, editors and wizard", ComboBoxStyleTests.SharedAcrossScreens);
                Case("Shared CheckBox style and preserved overlay toggles", CheckBoxStyleTests.SharedStyleAndOverlaySwitches);
                Case("Developer support launcher, optional link and safe browser failure", DeveloperSupportTests.LauncherAndDialog);
                Case("Status-bar version source, ellipsis and reserved narrow column", VersionStatusTests.LayoutAndFormatting);
                Case("Pre-show startup and portrait/tray layout", ResponsiveWindowTests.StartupAndPortrait);
                Case("Launcher minimum height and monitor return", ResponsiveWindowTests.LauncherMinimumAndDisplayReturn);
                Case("Portrait tab selection keeps geometry", ResponsiveWindowTests.StablePortraitTabs);
                Case("Broken pre-show settings preserve original", ResponsiveWindowTests.BrokenStartup);
                Case("Inline toast lifecycle and three-second errors", FeedbackTests.ToastLifecycle);
                Case("Inline toast wide/narrow/portrait geometry", FeedbackTests.ToastLayout);
                Case("Compact overlay toast without reserved whitespace", FeedbackTests.CompactOverlayLayout);
                Case("Durable save button and duplicate/repeat handling", SaveFeedbackTests.DurableAndRepeated);
                Case("Save failure state, toast expiry, retry and newer edits", SaveFeedbackTests.FailureRetryAndAdditionalEdits);
                Case("Internal save has no completion toast", SaveFeedbackTests.SilentSave);
                Case("Single feedback per profile action and unsaved OCR choice", ProfileFeedbackTests.MajorActions);
                Case("Failed switch/delete never report success", ProfileFeedbackTests.FailedTransitions);
                Case("OCR save-needed notice follows saved model snapshot", ProfileFeedbackTests.ModelSaveSnapshot);
                Case("Saving/completion geometry in all responsive sizes", FeedbackRegressionTests.SavingGeometry);
                Case("Out-of-order saves, tray and closing cleanup", FeedbackRegressionTests.StaleSavesAndLifecycle);
                Case("Independent game profile restoration and global access", ProfileTests.Restoration);
                Case("New profile target picker and name save/restart", ProfileTests.SaveCommands);
                Case("Rendered profile rename and default name protection", ProfileNameTests.RenameAndDefaultGuard);
                Case("Trigger spacing and dark disabled buttons", UiPolishTests.SpacingAndDisabledButtons);
                Case("Model and hotkey settings", WpfTests.Settings);
                Case("Tray close/start/open/exit policy", TrayTests.Lifecycle);
                Case("Tray exit cancellation and missing-icon fallback", TrayTests.FailedExitAndFallback);
                Case("Main shortcut captions, original-only mode and tray fallback text", TrayTests.LauncherCaptions);
                Case("Tray translation command, profile captions and failure recovery", ClipboardAndTrayTests.TrayCommand);
                Case("OCR clipboard panel defaults and per-profile persistence", ClipboardAndTrayTests.Settings);
                Case("Accepted OCR clipboard ordering, modes and stale guards", ClipboardAndTrayTests.AcceptedSource);
                Case("Stable OCR export excludes drafts and cancels before AI", ClipboardAndTrayTests.StableAndCanceled);
                Case("F1 defaults and reserved-key diagnostics", TrayTests.HotkeyDefaults);
                Case("Extensible provider controls and protected keys", ProviderUiTests.Controls);
                Case("Keyboard and gamepad direct-capture trigger chips", TriggerEditorTests.Controls);
                Case("Gamepad capture edges, disconnects, masks and cancellation", GamepadCaptureTests.CaptureAndMapping);
                Case("Recognition mode profile controls and saved preferences", RecognitionModeTests.ProfileControls);
                Case("Exclusive screen/trigger/clipboard monitoring and Stop", RecognitionModeTests.RuntimeModes);
                Case("Manual-only and delayed recognition input guards", RecognitionModeTests.ManualAndLateInputs);
                Case("Character dictionary Markdown guide and sample", CharacterDictionaryHelpTests.Guide);
                Case("Capture area display across screen/window/clipboard modes", CaptureAreaDescriptionTests.Cases);
                Case("Capture mode controls and geometric monitor preview", ProfileUsabilityTests.CaptureModesAndMap);
                Case("Original-only and passive overlay visibility options", ProfileUsabilityTests.SubtitleOptions);
                Case("Subtitle checkboxes bottom alignment and validation recovery", SubtitlePolishTests.AlignmentAndErrorRecovery);
                Case("Rejected subtitle toggle warning expires", LaunchFeedbackTests.RejectedVisibilityExpires);
                Case("Start failure explains missing/minimized target", LaunchFeedbackTests.StartFailureExplainsSource);
                Case("Silent save preserves useful current status", LaunchFeedbackTests.SavePreservesUsefulStatus);
                Case("Dark button hover, pressed and focus contrast", SubtitlePolishTests.ButtonInteractionContrast);
                Case("Official model list selection, copy and stale response", ProfileUsabilityTests.ModelPicker);
                Case("Ollama connection, current address, model draft and late response", OllamaDiscoveryUiTests.ConnectionAndDraft);
                Case("Ollama connection disposal, provider switch and superseded responses", OllamaDiscoveryUiTests.PendingConnectionLifetime);
                Case("Global credential visibility, saved masks and editing", ProviderUiTests.CredentialDisplay);
                Case("Initial provider credentials and page/profile isolation", ProfileStatusTests.ProviderStatusAndNavigation);
                Case("Credential navigation and corrected form messages", ProfileStatusTests.CredentialActionsAndCorrectedValidation);
                Case("Late translation result on UI dispatcher", WpfTests.StaleTranslation);
                Case("Status errors recover by operation, warnings and stale sessions", StatusLifecycleTests.Run);
                Case("Successful empty OCR recovers only OCR errors", StatusLifecycleTests.EmptyOcrRecovery);
                Case("Mode-aware guidance and completion status lifetime", StatusLifecycleTests.Messages);
                Case("Safe actionable save failure details", SaveFeedbackTests.SafeDetails);
                Case("OCR presentation lifecycle, manual work, stop, stale events and retries", OcrExecutionLifecycleTests.Run);
                Case("Study OCR remains visible until drain after translation stop", StudyOcrStatusTests.StopWhileRunning);
                Case("Study OCR cancellation/profile/close/restart/retire/error boundaries", StudyOcrStatusTests.Boundaries);
                Case("Animated subtitle retention", WpfTests.AnimatedSubtitle);
                Case("OCR source before translation response", WpfTests.OriginalBeforeTranslation);
                Case("NVL visual original rows and incremental translation blocks", NvlPresentationTests.Rows);
                Case("Empty and post-completion streaming retention", WpfTests.StreamingSubtitleRetention);
                Case("Region draft move/resize/explicit confirmation", RegionEditorTests.GeometryAndControls);
                Case("Region editor synthetic mixed DPI and monitors", RegionEditorTests.DpiAndMonitors);
                Case("Region confirmation save/cancel/profile restoration", RegionEditorTests.SaveAndRestore);
                Case("Scaled/unscaled region selection, persistence and ordinary-game isolation",RegionEditorTests.Scaling);
                Case("Original-game preview coordinates, save/cancel and no-scaler isolation",SourceRegionTests.Run);
                Case("Scaled original capture opt-in, stop/restart, profile persistence and isolation",ScaledOriginalOptionTests.Run);
                Case("Automatic game marker learning, legacy settings, safe fallback and per-game reset",MarkerLearningTests.Run);
                Case("Capture exclusion state policy", WpfTests.CapturePolicy);
                Case("Actual capture occlusion geometry and native state", CaptureTests.Occlusion);
                Case("Capture obstruction and minimized-window recovery", CaptureTests.Recovery);
                Case("Game QA save and pass-through chord state", WpfTests.GameQa);
                Check(visibleWindows == 0 && nativeWindows == 0, "Background created a visible/native Window");
            }
            Console.WriteLine($"PASS: {assertions} assertions ({(desktop ? "Desktop" : "Wpf Background")}); visibleWindows={visibleWindows}, nativeWindows={nativeWindows}; {clock.Elapsed.TotalMilliseconds:F0}ms. No paid AI or user settings.");
        }
        catch (DesktopEnvironmentException ex) { Console.Error.WriteLine("ENVIRONMENT UNAVAILABLE: " + ex.Message); result = 3; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); result = 1; }
        finally
        {
            if (application is not null)
            {
                try { foreach (var window in windows.ToArray()) Close(window); }
                catch (Exception ex) { Console.Error.WriteLine("FAIL cleanup: " + ex.Message); result = 1; }
                application.Shutdown(result);
            }
            if (!Dispatcher.CurrentDispatcher.HasShutdownStarted) Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
        return result;
    }
    private static void Case(string name, Action test)
    {
        var clock = Stopwatch.StartNew();
        try { test(); }
        finally { Console.WriteLine($"Wpf/{name}: {clock.Elapsed.TotalMilliseconds:F0}ms"); }
    }
    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
    internal static T Track<T>(T window) where T : Window
    {
        windows.Add(window);
        if (!desktop) Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Window constructor created a desktop surface");
        window.SourceInitialized += (_, _) => { nativeWindows++; if (!desktop) throw new InvalidOperationException("Background test created a Window HWND."); };
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) { visibleWindows++; if (!desktop) throw new InvalidOperationException("Background test called Show."); } };
        return window;
    }
    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    internal static void Until(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(message); Pump(); Thread.Sleep(1); }
        Pump();
    }
    internal static void Complete(Task task) { Until(() => task.IsCompleted, "Background task did not settle"); task.GetAwaiter().GetResult(); }
    internal static void Layout(Window window, double width, double height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); Pump();
    }
    internal static IEnumerable<T> Elements<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        // Logical children reach content before it has a presentation source/template.
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var nested in Elements<T>(child)) yield return nested;
    }
    internal static T Named<T>(Window window, string name) where T : FrameworkElement => Elements<T>(window).Single(e => e.Name == name);
    internal static Button Button(Window window, string label) => Elements<Button>(window).Single(b => Equals(b.Content, label)
        || System.Windows.Automation.AutomationProperties.GetName(b) == label);
    internal static string Text(Window window) => string.Join("\n", Elements<TextBlock>(window).Select(t => t.Text));
    internal static void Click(Button button) { Check(button.IsEnabled, "Disabled command invoked"); button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); }
    internal static void Close(Window window)
    {
        if (!windows.Contains(window)) return;
        if (window is StudyWindow study) Complete(study.CloseForExitAsync());
        else if (window is TranslationWindow translation) Complete(translation.CloseForExitAsync()); else window.Close();
        Until(() => !Application.Current.Windows.Cast<Window>().Contains(window), "Window/resource cleanup timed out");
        windows.Remove(window);
    }
}
