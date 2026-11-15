using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class GameQaTests
{
    private static int count;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException) { count++; return; } throw new Exception("Invalid QA operation accepted"); }
    private static async Task Until(Func<bool> test)
    { var clock = Stopwatch.StartNew(); while (!test()) { if (clock.ElapsedMilliseconds > 4000) throw new TimeoutException("Game QA test"); await Task.Delay(5); } }
    private static ImageFrame Frame(byte value = 17, int width = 12, int height = 8)
    { int stride = width * 4 + 8; return new(width, height, stride, Enumerable.Repeat(value, stride * height).Select((v, i) => (byte)(v + i % 7)).ToArray(), FramePixelFormat.Bgra32); }
    private static OcrResult Result(string text = "日本語 🐈 dialogue", int width = 4) => new([new(text, .95f, new(2, 2, width, 3))], TimeSpan.FromMilliseconds(12))
    { DetectedBoxes = [new(2, 2, width, 3)], DetectionTime = TimeSpan.FromMilliseconds(8), RecognitionTime = TimeSpan.FromMilliseconds(4) };
    internal static async Task Run()
    {
        var clock = Stopwatch.StartNew(); var root = Path.Combine(Path.GetTempPath(), "KoTrans2.GameQa.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await Files(root); await Replay(root); await EngineRecords(root); await Pipeline(); await Roi(); await NvlPrefix(root); }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} assertions (GameQa); {clock.Elapsed.TotalMilliseconds:F0}ms. Synthetic PNG/Mock only. No GUI, capture, input, models or AI API.");
    }
    private static async Task Files(string root)
    {
        var profile = new Profile { Name = "PRIVATE-PROFILE", CharacterDictionaryPath = "PRIVATE-DICTIONARY", Translation = new() { Model = "chosen-model", OllamaAddress = "http://private-host:9999" } };
        var recorder = new GameQaRecorder(); var writer = new GameQaWriter(); var image = Frame(); var options = new OcrOptions("ja", "NVL", true);
        var id = recorder.Begin(image, options, GameQaProfile.From(profile), 7);
        var pending = recorder.Snapshot!; Check(pending.Record.OcrStatus == "pending" && pending.Record.Ocr is null, "In-flight OCR marked completed");
        Check(!Directory.EnumerateFileSystemEntries(root).Any(), "Recorder wrote without user save");
        await Reject(() => writer.SaveAsync(root, pending with { Frame = Frame(width: 13) }));
        var pendingPath = await writer.SaveAsync(root, pending);
        recorder.Complete(id, Result(), "日本語 🐈 dialogue", true);
        var translating = recorder.Snapshot!; recorder.Translated(id, "이 결과의 번역", 9);
        Check(translating.Record.Translation.Status == "pending" && translating.Record.Translation.Text is null, "Frozen snapshot mutated after late AI");
        var path = await writer.SaveAsync(root, recorder.Snapshot!);
        var restored = await GameQaFiles.ReadAsync(Path.Combine(path, "input.png"));
        Check(restored.Record.RequestId == id && restored.Record.Ocr?.Text == "日本語 🐈 dialogue" && restored.Record.Translation.Text == "이 결과의 번역" && restored.Record.Options == options, "Paired result/translation/options lost");
        Check(Enumerable.Range(0, image.Height).All(y => image.Pixels.Span.Slice(y * image.Stride, image.Width * 4).SequenceEqual(restored.Frame.Pixels.Span.Slice(y * restored.Frame.Stride, image.Width * 4))), "PNG changed RGB/alpha or padded stride");
        Check(Directory.GetFiles(path).Select(Path.GetFileName).Order().SequenceEqual(new[] { "input.png", "note.txt", "result.json" }), "Unexpected saved data");
        var pendingRead = await GameQaFiles.ReadAsync(Path.Combine(pendingPath, "input.png"));
        Check(pendingRead.Record.Ocr is null && pendingRead.Record.OcrStatus == "pending" && pendingRead.Record.CompletedUtc is null, "Pending disk snapshot later became completed");
        string json = await File.ReadAllTextAsync(Path.Combine(path, "result.json"));
        Check(!json.Contains("PRIVATE-") && !json.Contains("private-host") && !json.Contains("api_key") && !json.Contains("history") && json.Contains("chosen-model"), "Profile secrets/history leaked or allowlist lost");
        recorder.Translated(Guid.NewGuid(), "wrong translation", 0); Check(recorder.Snapshot!.Record.Translation.Text == "이 결과의 번역", "Unrelated request contaminated translation");
        recorder.Clear(); recorder.Complete(id, Result("late"), "late", true); recorder.Translated(id, "late", 0);
        Check(recorder.Snapshot is null, "Old profile response recreated snapshot");
        var failed = recorder.Begin(Frame(23), options, GameQaProfile.From(profile)); recorder.Finish(failed, "failed");
        Check(recorder.Snapshot!.Record.OcrStatus == "failed" && recorder.Snapshot.Record.Ocr is null, "OCR failure hidden as success");
        var empty = recorder.Begin(image, options, GameQaProfile.From(profile)); recorder.Complete(empty, new([], TimeSpan.Zero), "", false);
        Check(recorder.Snapshot!.Record.OcrStatus == "completed" && recorder.Snapshot.Record.Ocr!.Lines.Count == 0, "Valid empty OCR confused with unfinished OCR");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Reject(() => writer.SaveAsync(root, recorder.Snapshot!, cancel.Token));
        Check(!Directory.EnumerateDirectories(root, ".pending-*").Any(), "Canceled save left complete-looking directory");
        var bgrx = new ImageFrame(image.Width, image.Height, image.Stride, image.Pixels.ToArray());
        var opaqueId = recorder.Begin(bgrx, options, GameQaProfile.From(profile)); recorder.Complete(opaqueId, Result(), "opaque", false);
        var opaquePath = await writer.SaveAsync(root, recorder.Snapshot!);
        var opaque = await GameQaFiles.ReadAsync(Path.Combine(opaquePath, "input.png"));
        Check(Enumerable.Range(0, image.Height).All(y => Enumerable.Range(0, image.Width).All(x => opaque.Frame.Pixels.Span[y * opaque.Frame.Stride + x * 4 + 3] == 255
            && opaque.Frame.Pixels.Span.Slice(y * opaque.Frame.Stride + x * 4, 3).SequenceEqual(image.Pixels.Span.Slice(y * image.Stride + x * 4, 3)))), "GDI BGRX PNG invisible or OCR RGB changed");
        var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "synthetic"); await Reject(() => writer.SaveAsync(blocked, recorder.Snapshot!));
        var retry = await writer.SaveAsync(root, recorder.Snapshot!); Check(Directory.Exists(retry), "Writer could not retry after I/O failure");
        var originalPng = await File.ReadAllBytesAsync(Path.Combine(path, "input.png")); originalPng[^1] ^= 1; await File.WriteAllBytesAsync(Path.Combine(path, "input.png"), originalPng);
        await Reject(() => GameQaFiles.ReadAsync(Path.Combine(path, "input.png")));
        await File.WriteAllTextAsync(Path.Combine(retry, "result.json"), "{"); await Reject(() => GameQaFiles.ReadAsync(Path.Combine(retry, "input.png")));
        await File.WriteAllTextAsync(Path.Combine(retry, "result.json"), new string(' ', GameQaFiles.MaxJsonBytes + 1)); await Reject(() => GameQaFiles.ReadAsync(Path.Combine(retry, "input.png")));
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"Hotkeys\":{\"Explain\":\"Ctrl+Alt+F9\"}}")!; settings.Validate();
        Check(settings.Hotkeys.SaveGameQa == "F8" && settings.Hotkeys.Explain == "Ctrl+Alt+F9", "New default made legacy custom settings unreadable");
        settings.Hotkeys.SaveGameQa = "Control+Alt+F9"; settings.Validate();
        Check(settings.Hotkeys.SaveGameQa == "F8", "Legacy Ctrl capture key was not upgraded");
        var store = new SettingsStore(Path.Combine(root, "settings")); settings.Hotkeys.SaveGameQa = "Shift+F8"; await store.SaveAsync(settings);
        Check((await store.LoadAsync()).Hotkeys.SaveGameQa == "Shift+F8", "Custom QA hotkey not persisted");
        var checkout = Path.Combine(root, "checkout"); Directory.CreateDirectory(checkout); File.WriteAllText(Path.Combine(checkout, "KoTrans.slnx"), "synthetic");
        Check(GameQaFiles.FindDirectory(Path.Combine(checkout, "bin"), root) == Path.Combine(checkout, "work", "game-qa") && GameQaFiles.FindDirectory(Path.Combine(root, "installed"), root) == Path.Combine(root, "KoTrans2", "work", "game-qa"), "QA root depends on CWD or read-only install folder");
    }
    private static async Task EngineRecords(string root)
    {
        var recorder=new GameQaRecorder();var options=new OcrOptions("ja","NVL",false,EngineId:"synthetic-engine");
        var id=recorder.Begin(Frame(),options,GameQaProfile.From(new()));
        var model = new OcrModelIdentity("synthetic", "1", new string('a',64), "", new string('b',64));
        var identity = new OcrRunIdentity(model,model,false,2) {EngineId="synthetic-engine",AdditionalModels=[model]};
        var result=Result() with { Models=identity,Characters=[new("字",.9f,new(2,2,3,3),0,false)] };
        recorder.Complete(id,result,result.Text,false);
        string path=await new GameQaWriter().SaveAsync(root,recorder.Snapshot!);
        var restored=await GameQaFiles.ReadAsync(Path.Combine(path,"input.png"));
        Check(restored.Record.Options.EngineId=="synthetic-engine" && restored.Record.Ocr!.Models!.EngineId=="synthetic-engine"
            && restored.Record.Ocr.Models.AdditionalModels.Single()==identity.AdditionalModels.Single(),"Additional model identity lost in QA");
        Check(restored.Record.Ocr!.Characters.Single().Text=="字","Character boxes lost in QA");
        await using var same=new ReplayOcr(identity);
        await GameQaReplay.RecognizeAsync(restored,same,false,default);count++;
        await using var wrong=new ReplayOcr(identity with {EngineId="paddle"});
        await Reject(()=>GameQaReplay.RecognizeAsync(restored,wrong,false,default));
        await Reject(()=>Task.FromResult(GameQaReplay.Prepare(restored,root)));
        await Reject(()=>Task.Run(()=>GameQaFiles.Validate(restored.Record with {Options=options with {EngineId="paddle"}})));
        var presetRecord=restored.Record with {Options=options with {ModelPresetId="test-preset"},Ocr=result with {Models=result.Models! with {ModelPresetId="test-preset"}}};
        var presetPath=await new GameQaWriter().SaveAsync(root,restored with {Record=presetRecord});
        var presetSaved=await GameQaFiles.ReadAsync(Path.Combine(presetPath,"input.png"));
        Check(presetSaved.Record.Options.ModelPresetId=="test-preset" && presetSaved.Record.Ocr!.Models!.ModelPresetId=="test-preset","QA preset not persisted");
        await Reject(()=>Task.Run(()=>GameQaFiles.Validate(presetRecord with {Options=options})));
        await Reject(()=>GameQaReplay.RecognizeAsync(presetSaved,same,false,default));
        var node=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new OcrRunIdentity(result.Models!.Detector,result.Models.Recognizer,false,2)))!.AsObject();
        node.Remove("ModelPresetId");node.Remove("EngineId");node.Remove("ImplementationVersion");node.Remove("AdditionalModels");
        Check(node.Deserialize<OcrRunIdentity>() is {EngineId:"paddle",ModelPresetId:"default"},"Legacy QA without engine did not restore Paddle");
    }
    private sealed class ReplayOcr(OcrRunIdentity identity) : IOcrService
    {
        public ImageFrame? Input; public OcrOptions? Options;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => throw new Exception("No real models");
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Input = frame; Options = options; return Task.FromResult(Result() with { Models = identity }); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task Replay(string root)
    {
        string models = Path.Combine(root, "models");
        ModelSpec Make(string folder)
        {
            var dir = Path.Combine(models, folder); Directory.CreateDirectory(dir);
            var model = new byte[] { 1, 2, 3 }; File.WriteAllBytes(Path.Combine(dir, "model.onnx"), model); File.WriteAllText(Path.Combine(dir, "dictionary.txt"), "a\nあ");
            var spec = new ModelSpec { Name = "synthetic-" + folder, Kind = folder == "det" ? "detection" : "recognition", Decoder = folder == "det" ? "DB" : "CTC", Languages = ["ja"],
                Sha256 = Convert.ToHexString(SHA256.HashData(model)), DictionarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "dictionary.txt")))) };
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(spec)); return spec;
        }
        var det = Make("det"); var rec = Make("ja"); var identity = new OcrRunIdentity(OcrIdentity.From(det), OcrIdentity.From(rec), false, 2) {FlushDenormalsToZero=true};
        var recorder = new GameQaRecorder(); var id = recorder.Begin(Frame(), new("ja", "NVL", true, false), GameQaProfile.From(new()));
        recorder.Complete(id, Result() with { Models = identity }, "日本語", false); var snapshot = recorder.Snapshot!;
        Check(snapshot.Record.AlgorithmVersion == OcrAlgorithmRevision.Current, "QA omitted algorithm revision");
        var v24 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v24-shared-dialogue-region-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v24-shared-dialogue-region-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v24, models)));
        Check(GameQaReplay.Prepare(v24, models, useCurrentAlgorithm: true) is not null, "Explicit v24 comparison rejected");
        var v23 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v23-dialogue-baseline-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v23-dialogue-baseline-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v23, models)));
        Check(GameQaReplay.Prepare(v23, models, useCurrentAlgorithm: true) is not null, "Explicit v23 comparison rejected");
        var v22 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v22-dialogue-region-first-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v22-dialogue-region-first-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v22, models)));
        Check(GameQaReplay.Prepare(v22, models, useCurrentAlgorithm: true) is not null, "Explicit v22 comparison rejected");
        var v21 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v21-nested-pixel-evidence-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v21-nested-pixel-evidence-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v21, models)));
        Check(GameQaReplay.Prepare(v21, models, useCurrentAlgorithm: true) is not null, "Explicit v21 comparison rejected");
        var v20 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v20-nested-fragment-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v20-nested-fragment-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v20, models)));
        Check(GameQaReplay.Prepare(v20, models, useCurrentAlgorithm: true) is not null, "Explicit v20 comparison rejected");
        var v19 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v19-touching-row-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v19-touching-row-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v19, models)));
        Check(GameQaReplay.Prepare(v19, models, useCurrentAlgorithm: true) is not null, "Explicit v19 comparison rejected");
        var v18 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v18-dialogue-ink-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v18-dialogue-ink-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v18, models)));
        Check(GameQaReplay.Prepare(v18, models, useCurrentAlgorithm: true) is not null, "Explicit v18 comparison rejected");
        var v17 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v17-confirmed-row-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v17-confirmed-row-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v17, models)));
        Check(GameQaReplay.Prepare(v17, models, useCurrentAlgorithm: true) is not null, "Explicit v17 comparison rejected");
        var v16 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v16-dialogue-row-recovery-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v16-dialogue-row-recovery-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v16, models)));
        Check(GameQaReplay.Prepare(v16, models, useCurrentAlgorithm: true) is not null, "Explicit v16 comparison rejected");
        var v15 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v15-overlapping-row-20261115",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v15-overlapping-row-20261115" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v15, models)));
        Check(GameQaReplay.Prepare(v15, models, useCurrentAlgorithm: true) is not null, "Explicit v15 comparison rejected");
        var v14 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v14-recognized-ruby-20261114",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v14-recognized-ruby-20261114" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v14, models)));
        Check(GameQaReplay.Prepare(v14, models, useCurrentAlgorithm: true) is not null, "Explicit v14 comparison rejected");
        var v13 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v13-static-label-recovery-20261114",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v13-static-label-recovery-20261114" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v13, models)));
        Check(GameQaReplay.Prepare(v13, models, useCurrentAlgorithm: true) is not null, "Explicit v13 comparison rejected");
        var v12 = snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v12-terminal-marker-tracking-20261106",
            Ocr = snapshot.Record.Ocr! with { Models = identity with { ImplementationVersion = "ocr-layout-v12-terminal-marker-tracking-20261106" } } } };
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(v12, models)));
        Check(GameQaReplay.Prepare(v12, models, useCurrentAlgorithm: true) is not null, "Explicit previous layout comparison rejected");
        var v5=snapshot with {Record=snapshot.Record with {AlgorithmVersion="ocr-layout-v5-20261030",Ocr=snapshot.Record.Ocr! with {Models=identity with {ImplementationVersion="ocr-layout-v5-20261030"}}}};
        var v6=snapshot with {Record=snapshot.Record with {AlgorithmVersion="ocr-layout-v6-20261031",Ocr=snapshot.Record.Ocr! with {Models=identity with {ImplementationVersion="ocr-layout-v6-20261031",FlushDenormalsToZero=false}}}};
        await Reject(()=>Task.FromResult(GameQaReplay.Prepare(v6,models)));
        Check(GameQaReplay.Prepare(v6,models,useCurrentAlgorithm:true) is not null,"Explicit v6 numeric-policy comparison rejected");
        var v9=snapshot with {Record=snapshot.Record with {AlgorithmVersion="ocr-layout-v9-terminal-stability-20261106",Ocr=snapshot.Record.Ocr! with {Models=identity with {ImplementationVersion="ocr-layout-v9-terminal-stability-20261106"}}}};
        await Reject(()=>Task.FromResult(GameQaReplay.Prepare(v9,models)));
        Check(GameQaReplay.Prepare(v9,models,useCurrentAlgorithm:true) is not null,"Explicit comparison from the previous NVL stability revision rejected");
        await using(var wrongPolicy=new ReplayOcr(identity with {FlushDenormalsToZero=false}))
            await Reject(()=>GameQaReplay.RecognizeAsync(snapshot,wrongPolicy,false,default));
        await using(var wrongThreads=new ReplayOcr(identity with {Threads=4}))
            await Reject(()=>GameQaReplay.RecognizeAsync(snapshot,wrongThreads,false,default));
        await Reject(()=>Task.FromResult(GameQaReplay.Prepare(v5,models)));
        Check(GameQaReplay.Prepare(v5,models,useCurrentAlgorithm:true) is not null,"Explicit v5 comparison rejected");
        await using(var current=new ReplayOcr(identity)) {
            await Reject(()=>GameQaReplay.RecognizeAsync(v5,current,false,default));
            await GameQaReplay.RecognizeAsync(v5,current,false,default,useCurrentAlgorithm:true);count++;
        }
        await using(var changedModel=new ReplayOcr(identity with {Recognizer=identity.Recognizer with {Sha256="different"}}))
            await Reject(()=>GameQaReplay.RecognizeAsync(v5,changedModel,false,default,useCurrentAlgorithm:true));
        Check(GameQaReplay.Prepare(snapshot with { Record = snapshot.Record with { AlgorithmVersion = null } }, models) is not null, "Legacy QA without version rejected");
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(snapshot with { Record = snapshot.Record with { AlgorithmVersion = "future" } }, models)));
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v3-20261030" } }, models)));
        Check(GameQaReplay.Prepare(snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v3-20261030" } }, models, useCurrentAlgorithm:true) is not null,"Explicit current-algorithm comparison rejected");
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v4-20261030" } }, models)));
        Check(GameQaReplay.Prepare(snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v4-20261030" } }, models, useCurrentAlgorithm:true) is not null,"Explicit comparison of pre-padding QA rejected");
        Check(GameQaReplay.Prepare(snapshot with { Record = snapshot.Record with { AlgorithmVersion = "ocr-layout-v2-20261030" } }, models, useCurrentAlgorithm:true) is not null,"Known legacy full-frame comparison rejected");
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(snapshot with {Record=snapshot.Record with {AlgorithmVersion="future"}},models,useCurrentAlgorithm:true)));
        var focusedOptions=snapshot.Record.Options with {SearchArea=new(1,1,8,5),DetectionLongSide=1280,KeepLocalParagraph=true};
        var focused=snapshot with {Record=snapshot.Record with {Options=focusedOptions}};
        var focusedPath=await new GameQaWriter().SaveAsync(root,focused);
        var focusedRead=await GameQaFiles.ReadAsync(Path.Combine(focusedPath,"input.png"));
        Check(focusedRead.Record.Options==focusedOptions && focusedRead.Frame.Width==12,"Focused options/full input did not round-trip");
        await using(var localMock=new ReplayOcr(identity)) {
            await GameQaReplay.RecognizeAsync(focusedRead,localMock,false,CancellationToken.None);
            Check(localMock.Options==focusedOptions,"Offline replay lost local detector resolution/paragraph contract");
        }
        await Reject(()=>Task.FromResult(GameQaReplay.Prepare(focused with {Record=focused.Record with {AlgorithmVersion="ocr-layout-v2-20261030"}},models)));
        var boundary=snapshot with {Record=snapshot.Record with {Ocr=snapshot.Record.Ocr! with {DetectionPasses=2,DetectorPadding=new(0,0,0,32)}}};
        var boundaryPath=await new GameQaWriter().SaveAsync(root,boundary);
        var boundaryRead=await GameQaFiles.ReadAsync(Path.Combine(boundaryPath,"input.png"));
        Check(boundaryRead.Record.Ocr!.DetectionPasses==2 && boundaryRead.Record.Ocr.DetectorPadding==new OcrPadding(0,0,0,32),"Virtual detector margin missing from QA record");
        var singlePass=boundary with {Record=boundary.Record with {Ocr=boundary.Record.Ocr! with {DetectionPasses=1,DetectorPadding=new(16,16,16,16)}}};
        var singlePath=await new GameQaWriter().SaveAsync(root,singlePass);
        Check((await GameQaFiles.ReadAsync(Path.Combine(singlePath,"input.png"))).Record.Ocr!.DetectionPasses==1,"Current single-pass padded OCR rejected by QA");
        await Reject(()=>Task.Run(()=>GameQaFiles.Validate(boundary.Record with {Ocr=boundary.Record.Ocr! with {DetectionPasses=3}})));
        await Reject(()=>Task.Run(()=>GameQaFiles.Validate(boundary.Record with {Ocr=boundary.Record.Ocr! with {DetectorPadding=new(-1,0,0,32)}})));
        var execution = GameQaReplay.Prepare(snapshot, models);
        Check(!execution.AllowUpscale && execution.Threads == 2, "Replay silently changed execution configuration");
        await using var mock = new ReplayOcr(identity); await GameQaReplay.RecognizeAsync(snapshot, mock, false, CancellationToken.None);
        Check(ReferenceEquals(mock.Input, snapshot.Frame) && mock.Options == snapshot.Record.Options, "Replay changed saved image/language/layout");
        det.Threshold = .4f; File.WriteAllText(Path.Combine(models, "det", "manifest.json"), JsonSerializer.Serialize(det));
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(snapshot, models)));
        Check(GameQaReplay.Prepare(snapshot, models, models).DetectorRoot == models, "Explicit detector comparison rejected");
        await using var wrong = new ReplayOcr(identity with { Recognizer = identity.Recognizer with { Sha256 = "wrong" } });
        await Reject(() => GameQaReplay.RecognizeAsync(snapshot, wrong, true, CancellationToken.None));
        var pending = recorder.Begin(Frame(), new("ja", "ADV", false), GameQaProfile.From(new()));
        await Reject(() => Task.FromResult(GameQaReplay.Prepare(recorder.Snapshot!, models)));
        Check(pending != id, "Request identity reused");
    }
    private sealed class BlockingOcr : IOcrService
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) { await Release.Task; token.ThrowIfCancellationRequested(); return Result(frame.Pixels.Span[0].ToString()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class LateAi : ITranslationService
    {
        public readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { if (++Calls == 1) { First.TrySetResult(); await Release.Task; } return new("translation:" + request.Text, TimeSpan.Zero, 0); }
    }
    private static async Task Pipeline()
    {
        var recorder = new GameQaRecorder(); var ocr = new BlockingOcr(); var ai = new LateAi();
        await using var pipeline = new TranslationPipeline(ocr, ai, _ => throw new Exception("No desktop capture"), new("en", "ADV", false), new(), new(), "synthetic",
            settle: TimeSpan.Zero, gameQa: recorder, qaProfile: GameQaProfile.From(new()));
        pipeline.Start(false); await pipeline.RequestManualAsync(image: Frame(21)); await Until(() => recorder.Snapshot is not null);
        Check(recorder.Snapshot!.Record.OcrStatus == "pending" && recorder.Snapshot.Frame.Pixels.Span[0] == 21, "Pipeline recorded result before OCR finished");
        ocr.Release.TrySetResult(); await ai.First.Task.WaitAsync(TimeSpan.FromSeconds(3)); var first = recorder.Snapshot!;
        Check(first.Record.OcrStatus == "completed" && first.Record.Ocr!.Text == "21" && first.Record.Translation.Status == "pending", "Pipeline OCR/AI states conflated");
        await pipeline.RequestManualAsync(image: Frame(31)); ai.Release.TrySetResult(); await Until(() => recorder.Snapshot?.Record.Translation.Status == "completed");
        Check(recorder.Snapshot!.Record.Ocr!.Text == "31" && recorder.Snapshot.Record.Translation.Text == "translation:31" && recorder.Snapshot.Record.RequestId != first.Record.RequestId, "Late response paired with wrong frame");
        pipeline.SubmitClipboard("clipboard-only", true); await Until(() => ai.Calls == 3);
        Check(recorder.Snapshot is null, "Clipboard reused unrelated screenshot");
        await using var failed = new TranslationPipeline(ocr, new FailedAi(), _ => throw new Exception("No capture"), new("en", "ADV", false), new(), new(), "synthetic",
            settle: TimeSpan.Zero, gameQa: recorder, qaProfile: GameQaProfile.From(new()));
        failed.Start(false); await failed.RequestManualAsync(image: Frame(41)); await Until(() => recorder.Snapshot?.Record.Translation.Status == "failed");
        Check(recorder.Snapshot!.Record.OcrStatus == "completed" && recorder.Snapshot.Record.Ocr!.Text == "41" && recorder.Snapshot.Record.Translation.Text is null, "AI failure discarded valid OCR or retained another translation");
        var blocked = new BlockingOcr();
        await using var canceled = new TranslationPipeline(blocked, new ImmediateAi(), _ => throw new Exception("No capture"), new("en", "ADV", false), new(), new(), "synthetic",
            settle: TimeSpan.Zero, gameQa: recorder, qaProfile: GameQaProfile.From(new()));
        canceled.Start(false); await canceled.RequestManualAsync(image: Frame(51)); await Until(() => recorder.Snapshot?.Frame.Pixels.Span[0] == 51);
        canceled.Stop(); blocked.Release.TrySetResult(); await canceled.DisposeAsync();
        Check(recorder.Snapshot!.Record.OcrStatus == "canceled" && recorder.Snapshot.Record.Ocr is null, "Canceled in-flight OCR exported a completed result");
    }
    private sealed class FailedAi : ITranslationService
    { public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) => Task.FromException<TranslationResult>(new TranslationException("synthetic error")); }
    private sealed class RegionOcr : IOcrService
    {
        public OcrCapabilities Capabilities => OcrCapabilities.Paddle with { LocalParagraph = false };
        public int Calls; public bool Clip;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        { var box = frame.Width == 800 ? new PixelRect(100, 400, 500, 30) : new PixelRect(Clip ? 0 : 60, 60, 500, 30); return Task.FromResult(new OcrResult([new("text" + Interlocked.Increment(ref Calls), 1, box)], TimeSpan.FromMilliseconds(5)) { DetectedBoxes = [box] }); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ImmediateAi : ITranslationService
    { public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) => Task.FromResult(new TranslationResult("linked:" + request.Text, TimeSpan.Zero, 0)); }
    private static async Task Roi()
    {
        var frame = new ImageFrame(800, 600, 3200, new byte[3200 * 600]); var recorder = new GameQaRecorder(); var ocr = new RegionOcr();
        await using var pipeline = new TranslationPipeline(ocr, new ImmediateAi(), _ => Task.FromResult(Volatile.Read(ref frame)), new("en", "NVL", false), new(), new(), "synthetic",
            TimeSpan.FromMilliseconds(10), TimeSpan.Zero, recorder, GameQaProfile.From(new()));
        pipeline.Start(true); await Until(() => recorder.Snapshot?.Record.Translation.Text == "linked:text1");
        ImageFrame Change(byte value)
        { var bytes = frame.Pixels.ToArray(); for (int y = 400; y < 430; y++) for (int x = 100; x < 600; x++) for (int c = 0; c < 3; c++) bytes[y * 3200 + x * 4 + c] = value; return new(800, 600, 3200, bytes); }
        Volatile.Write(ref frame, Change(90)); await Until(() => recorder.Snapshot?.Record.Translation.Text == "linked:text2");
        var crop = recorder.Snapshot!;
        Check(crop.Frame.Width == 620 && crop.Record.Input.Crop == new PixelRect(40, 340, 620, 150) && crop.Record.Ocr!.Lines[0].Bounds == new PixelRect(60, 60, 500, 30), "QA saved full frame with cropped OCR or restored coordinates");
        Check(crop.Frame.Pixels.Span[60 * crop.Frame.Stride + 60 * 4] == 90, "QA saved a different ROI buffer");
        ocr.Clip = true; Volatile.Write(ref frame, Change(160)); await Until(() => recorder.Snapshot?.Record.Translation.Text == "linked:text4");
        var full = recorder.Snapshot!;
        Check(full.Frame.Width == 800 && full.Record.Input.Crop is null && full.Record.Ocr!.Lines[0].Bounds.X == 100 && full.Record.Ocr.InferenceTime.TotalMilliseconds == 5, "Fallback mixed crop/full coordinates or combined attempt timing");
    }
    private static async Task NvlPrefix(string root)
    {
        var recorder=new GameQaRecorder();var options=new OcrOptions("ja","NVL",false,SearchArea:new(0,4,12,4),KeepLocalParagraph:true);
        var prefix=new OcrLine("前。",1,new(1,0,8,3));var active=new OcrLine("次。",1,new(1,5,8,3));
        var id=recorder.Begin(Frame(),options,GameQaProfile.From(new()));
        recorder.Complete(id,new([active],TimeSpan.Zero),"前。次。",true,options,[prefix]);
        var path=await new GameQaWriter().SaveAsync(root,recorder.Snapshot!);
        var restored=await GameQaFiles.ReadAsync(Path.Combine(path,"input.png"));
        Check(restored.Record.NvlPrefix.SequenceEqual(new[]{prefix}) && restored.Record.Ocr!.Text=="次。" && restored.Record.PipelineText=="前。次。","Native NVL result/reused prefix lost on disk");
        var merged=NvlOcrTracker.Merge(new(options.SearchArea!.Value,restored.Record.NvlPrefix),restored.Record.Ocr!,restored.Frame,options);
        Check(NvlText.Canonical(merged.Text,"ja")==restored.Record.PipelineText,"NVL offline pipeline composition differs");
        await Reject(()=>Task.Run(()=>GameQaFiles.Validate(restored.Record with {NvlPrefix=[prefix with {Bounds=new(-1,0,8,3)}]})));
        await Reject(()=>Task.Run(()=>GameQaFiles.Validate(restored.Record with {Options=options with {SearchArea=null}})));
        await Reject(()=>Task.Run(()=>GameQaFiles.Validate(restored.Record with {Options=options with {ReadingMode="ADV"}})));
    }
}
