using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

if (args.FirstOrDefault() == "--backend") { await LocalWorkerFixture.Run(args); return; }
if (args.Length == 4 && args[0] == "--local-model") { await LocalModelIntegrationTests.Run(args[1], args[2], args[3]); return; }
if (args.Length == 4 && args[0] == "--local-model-light") { await LocalModelIntegrationTests.Run(args[1], args[2], args[3], LocalTranslationModel.Lightweight18B); return; }
var suite = args.FirstOrDefault() ?? "Foundation";
if (suite == "Translation") await AcceptedSourceVerificationTests.Run();
if (suite == "Translation") await JapaneseAdvNoiseTests.Pipeline();
if (suite == "Translation") await NvlLayoutTests.Run();
if (suite == "Translation") await RepeatedOcrCandidateTests.Run();
if (suite == "Translation") await TranslationContextIsolationTests.Run();
if (suite == "Translation") await AutomaticCaptureLifecycleTests.Run();
if (suite == "Translation") await NvlPerformanceTests.Run();
if (suite == "Image") { ScaledCaptureTests.Run(); await WindowPrintCaptureTests.Run(); }
if (suite is "Settings" or "Foundation") await DebugDiagnosticsTests.Storage();
if (suite == "Translation") { await OcrResultCacheTests.Run(); await DebugDiagnosticsTests.Pipeline(); await CapturePauseTests.Run(); await LearnedMarkerFrameTests.Run(); await GeneralMarkerBlinkTests.Run(); await LearnedMarkerCycleTests.Run(); }
if (suite == "Ocr" && args.Length == 1) {OcrRecognitionCacheTests.Run();await DirectMlTests.Run();await GameMarkerLearningTests.Run();TerminalMarkerTrackingTests.Run();MarkerBodyRecognitionCacheTests.Run();}
if (args.Length > 1 || suite is not ("Foundation" or "Image" or "Settings" or "Ocr" or "Translation" or "Study" or "Stability" or "GameQa"))
{
    Console.Error.WriteLine("Select Foundation, Image, Settings, Ocr, Translation, Study, Stability or GameQa. Wpf and Desktop have separate runners.");
    Environment.ExitCode = 2; return;
}
if(suite == "Ocr") { DialogueInkFilterTests.Run(); DialoguePrefixRecoveryTests.Run(); OverlappingRowRecoveryTests.Run(); JapaneseAdvNoiseTests.Run(); await OcrExecutionPolicyTests.Run(); OcrAlgorithmTests.Run(); RubySuppressionTests.Run(); ContinuationMarkerTests.Run(); NvlOcrTests.Run(); await OcrEngineTests.Run(); await PaddlePresetTests.Run(); return; }
if(suite == "Translation") { await VerifiedFrameReuseTests.Run(); await RepeatedAnimationTests.Run(); await LocalTranslationTests.Run(); await Phase3Tests.Run(); await ProviderExtensionTests.Run(); await ProviderRoutingTests.Run(); await OllamaProtocolTests.Run(); await AuditRegressionTests.TranslationAsync(); await GameFixTests.Run(); await StreamingTests.Run(); await GeminiStreamingTests.Run(); await MuramasaTests.Run(); await AdvRecoveryTests.Run(); await NvlPipelineTests.Run(); await TriggerSessionTests.Run(); await OcrOnlyTests.Run(); await ModelListTests.Run(); return; }
if(suite == "Study") { await Phase4Tests.Run(); WordDictionaryCoreTests.Japanese(); return; }
if(suite == "Stability") { await StabilityTests.Run(); return; }
if(suite == "GameQa") { await GameQaTests.Run(); return; }
var count = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); count++; }
void Reject(Action action) { try { action(); } catch (ArgumentException) { count++; return; } throw new Exception("Invalid input accepted"); }
if (suite is "Foundation" or "Image")
{
    Reject(() => new ImageFrame(0, 20, 0, []));
    Reject(() => new ImageFrame(4, 4, 15, new byte[60]));
    Reject(() => new ImageFrame(4, 4, 16, new byte[63]));
    Reject(() => new ImageFrame(int.MaxValue, int.MaxValue, int.MaxValue, []));
    var frame = new ImageFrame(4, 2, 20, new byte[40]);
    Check(frame.Stride == 20 && frame.Pixels.Length == 40, "Padded stride failed");
    Check(new PixelRect(-1920, -400, 800, 300).IsValid, "Negative desktop coordinates rejected");
    Reject(() => ScreenCapture.Capture(new PixelRect(0, 0, 0, 4)));
    await using var ocr = new UnconfiguredOcrService();
    Check(ocr.State == OcrState.NotConfigured, "OCR falsely reported ready");
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await ocr.RecognizeAsync(frame, new("en", "ADV", false), cts.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { count++; }
}
if (suite is "Foundation" or "Settings")
{
    await PortableUpdateTests.Run();
    await ProfileRestorationTests.Run();
    await WordDictionaryCoreTests.Settings();
    count += AuditRegressionTests.SettingsAndLaunch();
    var dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var store = new SettingsStore(dir); var settings = await store.LoadAsync();
        Check(settings.DefaultProfile.Triggers.MouseButton == "Left" && !settings.DefaultProfile.CharacterDictionaryEnabled,
            "Requested mouse/dictionary defaults incorrect");
        settings.DefaultProfile.Triggers.MouseButton = "None"; settings.DefaultProfile.CharacterDictionaryEnabled = true;
        await store.SaveAsync(settings); var customDefaults = await store.LoadAsync();
        Check(customDefaults.DefaultProfile.Triggers.MouseButton == "None" && customDefaults.DefaultProfile.CharacterDictionaryEnabled,
            "New defaults overwrote saved trigger/dictionary preferences");
        Check(settings.Hotkeys.Explain == "Shift+F2" && settings.Hotkeys.StudyScrollUp == "Shift+PageUp"
            && settings.Hotkeys.StudyScrollDown == "Shift+PageDown", "Fresh study shortcuts incorrect");
        var legacyKeys = new AppSettings { Hotkeys = new() { Explain = "Alt+F12", StudyScrollUp = "Alt+F10", StudyScrollDown = "Alt+F11" } };
        legacyKeys.DefaultProfileId = legacyKeys.Profiles[0].Id;
        await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(legacyKeys));
        var migrated = await store.LoadAsync();
        Check(migrated.Hotkeys.Explain == "Shift+F2" && migrated.Hotkeys.StudyScrollUp == "Shift+PageUp"
            && migrated.Hotkeys.StudyScrollDown == "Shift+PageDown", "Saved legacy study defaults not migrated");
        await store.SaveAsync(migrated); var persisted = (await store.LoadAsync()).Hotkeys;
        Check(persisted.Explain == "Shift+F2" && persisted.StudyScrollUp == "Shift+PageUp"
            && persisted.StudyScrollDown == "Shift+PageDown", "Migrated defaults did not persist");
        var customKeys = new AppSettings { Hotkeys = new() { Explain = "Shift+F3", StudyScrollUp = "Alt+Up", StudyScrollDown = "Alt+Down" } };
        customKeys.Validate();
        Check(customKeys.Hotkeys.Explain == "Shift+F3" && customKeys.Hotkeys.StudyScrollUp == "Alt+Up"
            && customKeys.Hotkeys.StudyScrollDown == "Alt+Down", "Custom study shortcuts overwritten");
        var partialKeys = new AppSettings { Hotkeys = new() { Explain = "Shift+F3", StudyScrollUp = " alt + f10 ", StudyScrollDown = "Alt+F11" } };
        partialKeys.Validate(); partialKeys.Validate();
        Check(partialKeys.Hotkeys.Explain == "Shift+F3" && partialKeys.Hotkeys.StudyScrollUp == "Shift+PageUp"
            && partialKeys.Hotkeys.StudyScrollDown == "Shift+PageDown", "Partial defaults or repeated validation changed custom keys");
        var blockedKeys = new AppSettings { Hotkeys = new() { ShowSettings = "Shift+F2", ToggleTranslation = " shift + prior ",
            Explain = "Alt+F12", StudyScrollUp = "Alt+F10", StudyScrollDown = "Alt+F11", SaveGameQa = "Shift+Next" } };
        blockedKeys.Validate();
        Check(blockedKeys.Hotkeys.Explain == "Alt+F12" && blockedKeys.Hotkeys.StudyScrollUp == "Alt+F10"
            && blockedKeys.Hotkeys.StudyScrollDown == "Alt+F11", "Study migration collided with existing keys or PageUp/PageDown aliases");
        var missingKeys = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Hotkeys\":{\"ToggleTranslation\":\"Alt+F11\"}}")!;
        missingKeys.Validate();
        Check(missingKeys.Hotkeys.ToggleTranslation == "Alt+F11" && missingKeys.Hotkeys.Explain == "Shift+F2"
            && missingKeys.Hotkeys.StudyScrollUp == "Shift+PageUp" && missingKeys.Hotkeys.StudyScrollDown == "Shift+PageDown",
            "Missing legacy study fields destroyed custom translation shortcut");
        var models = settings.Profiles[0].Translation;
        Check(settings.DefaultProfile.ShowTranslation && System.Text.Json.JsonSerializer.Deserialize<Profile>("{}")!.ShowTranslation,
            "Korean translation must default on for new and legacy profiles");
        settings.DefaultProfile.ShowTranslation = false; settings.DefaultProfile.ShowOriginal = true;
        var sourceOnlyGame = settings.CreateGameProfile("source-only");
        sourceOnlyGame.ShowTranslation = true; sourceOnlyGame.ShowOriginal = false;
        await store.SaveAsync(settings); var visibility = await store.LoadAsync();
        Check(!visibility.DefaultProfile.ShowTranslation && visibility.DefaultProfile.ShowOriginal
            && visibility.Profiles.Single(p => p.Id == sourceOnlyGame.Id).ShowTranslation
            && !visibility.Profiles.Single(p => p.Id == sourceOnlyGame.Id).ShowOriginal, "Subtitle visibility did not round-trip independently");
        settings.Profiles.Remove(sourceOnlyGame); settings.DefaultProfile.ShowTranslation = true;
        var invalidVisibility = new AppSettings(); invalidVisibility.Profiles[0].ShowOriginal = invalidVisibility.Profiles[0].ShowTranslation = false;
        try { invalidVisibility.Validate(); throw new Exception("Invisible subtitle profile was accepted"); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("하나 이상"), "Subtitle validation failure is not actionable"); }
        models.SwitchProvider(TranslationProvider.Gemini); models.Model = "gemini-exact";
        models.SwitchProvider(TranslationProvider.OpenAI); models.Model = "openai-exact";
        models.SwitchProvider(TranslationProvider.Ollama); models.Model = "translategemma:12b";
        models.OllamaModels = models.OllamaModels.SetItem(models.Model,new(OllamaRequestFormat.TranslateGemma,150,12));
        await store.SaveAsync(settings); var restoredModels = (await store.LoadAsync()).Profiles[0].Translation;
        Check(restoredModels.Provider == TranslationProvider.Ollama && restoredModels.Model == "translategemma:12b", "Active model did not round-trip");
        Check(restoredModels.SettingsForOllama()==new OllamaModelSettings(OllamaRequestFormat.TranslateGemma,150,12), "Ollama protocol/timeout/retention did not round-trip");
        restoredModels.SwitchProvider(TranslationProvider.Gemini); Check(restoredModels.Model == "gemini-exact", "Gemini model not separately persisted");
        restoredModels.SwitchProvider(TranslationProvider.OpenAI); Check(restoredModels.Model == "openai-exact", "OpenAI model not separately persisted");
        var legacyModels = System.Text.Json.JsonSerializer.Deserialize<TranslationOptions>("{\"Provider\":2,\"Model\":\"legacy-local\"}")!; legacyModels.Validate();
        Check(legacyModels.OllamaModel == "legacy-local" && legacyModels.GeminiModel == "" && legacyModels.OpenAIModel == "", "Legacy active model migrated to another provider");
        var snapshot = models with { }; models.SwitchProvider(TranslationProvider.Gemini);
        Check(snapshot.Provider == TranslationProvider.Ollama && snapshot.Model == "translategemma:12b", "Provider edit mutated an in-flight request snapshot");
        settings.Profiles[0].Name = "일본어 게임"; settings.Profiles[0].CaptureArea = new(-1600, 100, 640, 240);
        await store.SaveAsync(settings);
        var loaded = await new SettingsStore(dir).LoadAsync();
        Check(loaded.Profiles[0].Name == "일본어 게임" && loaded.Profiles[0].CaptureArea.X == -1600, "Round trip failed");
        settings.Profiles[0].Name = "변경"; await store.SaveAsync(settings);
        Check(File.Exists(Path.Combine(dir, "settings.json.bak")), "Atomic replacement backup missing");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { await store.SaveAsync(settings, cts.Token); throw new Exception("Canceled save succeeded"); } catch (OperationCanceledException) { count++; }
        Check((await store.LoadAsync()).Profiles[0].Name == "변경", "Canceled save damaged settings");
        var file = Path.Combine(dir, "settings.json"); await File.WriteAllTextAsync(file, "{corrupted}");
        var broken = new SettingsStore(dir);
        try { await broken.LoadAsync(); throw new Exception("Corruption ignored"); } catch (InvalidDataException) { count++; }
        try { await broken.SaveAsync(settings); throw new Exception("Corrupt original overwritten"); } catch (InvalidOperationException) { count++; }
        Check(await File.ReadAllTextAsync(file) == "{corrupted}", "Original was lost");
        settings.SchemaVersion = 99;
        try { settings.Validate(); throw new Exception("Future schema accepted"); } catch (InvalidDataException) { count++; }
    }
    finally { Directory.Delete(dir, true); }
}
Console.WriteLine($"PASS: {count} assertions ({suite}). No AI requests or user settings accessed.");
