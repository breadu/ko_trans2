using System.Text.Json;
using KoTrans.Core;
using KoTrans.Ocr;
using KoTrans.Infrastructure;

internal static class OcrEngineTests
{
    internal static async Task Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        Check(JsonSerializer.Deserialize<Profile>("{}")!.OcrEngineId == "paddle", "Old profile default changed");
        Check(JsonSerializer.Deserialize<OcrOptions>("{\"Language\":\"ja\",\"ReadingMode\":\"ADV\",\"Vertical\":false}")!.EngineId=="paddle","Old QA options without engine did not default to Paddle");
        var settings = new AppSettings(); settings.Profiles[0].OcrEngineId = "future-engine"; settings.Validate();
        Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.Profiles[0].OcrEngineId == "future-engine", "Unknown ID lost");
        var created = new List<Fake>();
        var descriptor = new OcrEngineDescriptor("fake-third", "Third engine", "test-v1", ["en"], false, false, OcrCapabilities.Basic, false, true, "Test only");
        var registration = new OcrEngineRegistration(descriptor, _ => { var f = new Fake(); created.Add(f); return f; }, (_, _) => new(true, "Ready"));
        var factory = new OcrServiceFactory(new(OcrEngineCatalog.Default.Engines.Select(d => OcrEngineCatalog.Default.Get(d.Id)).Append(registration)), "no-models");
        var options = new OcrOptions("en", "ADV", false, EngineId: "fake-third");
        try { factory.Create("future-engine"); throw new Exception("Unknown engine silently fell back"); } catch (OcrInitializationException) { count++; }
        try { factory.Validate(options with { Language = "ja" }); throw new Exception("Language accepted"); } catch (OcrInitializationException) { count++; }
        await using var manager = new OcrServiceManager(factory); var profile = Guid.NewGuid();
        var first = manager.Select(profile, options);
        Check(ReferenceEquals(first, manager.Select(profile, options)), "Same profile/engine reloaded");
        Check(first.Capabilities == OcrCapabilities.Basic, "Registration capabilities ignored");
        var frame = new ImageFrame(4, 4, 16, new byte[64]);
        var pending = first.RecognizeAsync(frame, options, default);
        var nextProfile = Guid.NewGuid(); var next = manager.Select(nextProfile, options);
        Check(!ReferenceEquals(first, next) && created[0].Canceled && !created[0].Disposed, "In-flight session disposed early");
        created[0].Finish.TrySetResult(new([], TimeSpan.Zero));
        try { await pending; throw new Exception("Late result published"); } catch (OperationCanceledException) { count++; }
        var changingEngine = next.RecognizeAsync(frame, options, default);
        var paddle = manager.Select(nextProfile, options with { EngineId="paddle" });
        Check(paddle.State==OcrState.NotConfigured && created[1].Canceled && !created[1].Disposed,"Same-profile engine switch loaded models or disposed active work");
        created[1].Finish.TrySetResult(new([],TimeSpan.Zero));
        try { await changingEngine; throw new Exception("Old engine result published"); } catch(OperationCanceledException) { count++; }
        await manager.DisposeAsync(); Check(created.All(f => f.Disposed), "Retired sessions leaked");
        var contexts = new TranslationContexts(); var ai = new TranslationOptions();
        Check(!ReferenceEquals(contexts.Get(profile, options, ai, "p"), contexts.Get(profile, options with { EngineId="paddle" }, ai,"p")), "OCR engine contexts shared");
        var model = new OcrModelIdentity("test", "1", "hash", "", "");
        var legacy = JsonSerializer.Deserialize<OcrRunIdentity>(JsonSerializer.Serialize(new OcrRunIdentity(model, model, true, 2)))!;
        Check(legacy.EngineId == "paddle", "Legacy QA engine default wrong");
        Check(JsonSerializer.Deserialize<Profile>("{}")!.OcrModelPresetId == "v6-medium-full", "Old preset default changed");
        Check(!factory.CheckModels(new("ja", "ADV", false, EngineId: "meiki")).Ready, "Removed engine accepted by OCR factory");
        Check(JsonSerializer.Deserialize<OcrOptions>("{\"Language\":\"ja\",\"ReadingMode\":\"ADV\",\"Vertical\":false}")!.ModelPresetId == "default", "Old QA preset default changed");
        var presetCreated = new List<Fake>(); string selection = "first-path";
        OcrModelPresetRegistration Preset(string id) => new(new("fake-third", id, "Test " + id, ["en"], "Test"),
            (_, _) => { var fake = new Fake(); presetCreated.Add(fake); return fake; }, (_, _) => new(true, "Ready"),
            Replay: (_, _, identity, _) => { var fake = new Fake(); fake.Finish.SetResult(new([],TimeSpan.Zero) {Models=identity}); return fake; },
            SelectionKey: (_, _) => selection);
        var presetFactory = new OcrServiceFactory(new([registration with { Presets = [Preset("default"), Preset("test-next")] }]), "synthetic");
        Check(presetFactory.Presets.Presets.Count == 2 && presetFactory.CheckModels(options).Ready && presetCreated.Count == 0, "Readiness loaded a model");
        try { presetFactory.Create(options with { ModelPresetId = "unknown" }); throw new Exception("Unknown preset replaced"); } catch (OcrInitializationException) { count++; }
        var profileSettings = new AppSettings(); profileSettings.Profiles[0].OcrEngineId = "fake-third"; profileSettings.Profiles[0].OcrModelPresetId = "test-next";
        profileSettings.Validate();
        Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(profileSettings))!.Profiles[0].OcrOptions().ModelPresetId == "test-next", "Preset roundtrip failed");
        await using (var presets = new OcrServiceManager(presetFactory)) {
            var original = presets.Select(profile, options);
            Check(ReferenceEquals(original, presets.Select(profile, options)), "Same preset reloaded");
            var late = original.RecognizeAsync(frame, options, default);
            var alternateOptions = options with { ModelPresetId = "test-next" };
            var alternate = presets.Select(profile, alternateOptions);
            Check(!ReferenceEquals(original, alternate) && presetCreated[0].Canceled && !presetCreated[0].Disposed, "Preset switch failed to cancel/drain");
            presetCreated[0].Finish.TrySetResult(new([], TimeSpan.Zero));
            try { await late; throw new Exception("Late preset response published"); } catch (OperationCanceledException) { count++; }
            selection = "changed-path";
            Check(!ReferenceEquals(alternate, presets.Select(profile, alternateOptions)), "Model path change retained session");
            for (int i=0; i<20; i++) presets.Select(profile, i % 2 == 0 ? options : alternateOptions);
        }
        Check(presetCreated.All(f => f.Disposed), "Repeated preset switches leaked sessions");
        var testIdentity=new OcrRunIdentity(model,model,true,2) {EngineId="fake-third",ModelPresetId="test-next",ImplementationVersion="test-v1"};
        await using(var replay=presetFactory.CreateReplay(options with {ModelPresetId="test-next"},testIdentity)) {
            Check((await replay.RecognizeAsync(frame,options with {ModelPresetId="test-next"},default)).Models==testIdentity,"Registered test preset QA identity lost");
        }

        Check(!ReferenceEquals(contexts.Get(profile, options, ai, "p"), contexts.Get(profile, options with { ModelPresetId="test-next" }, ai,"p")), "Model preset contexts shared");
        try { factory.CreateReplay(options with { EngineId="paddle", ModelPresetId="default" }, new(model,model,true,2) { ModelPresetId="another" }); throw new Exception("QA preset mismatch accepted"); }
        catch (InvalidDataException) { count++; }
        Console.WriteLine($"PASS: {count} OCR engine registry/lifetime assertions (no models).");
    }
    private sealed class Fake : IOcrService
    {
        internal bool Canceled, Disposed;
        internal readonly TaskCompletionSource<OcrResult> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        { using var cancel = token.Register(() => Canceled = true); return await Finish.Task; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
