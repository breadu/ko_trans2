using System.Collections.Concurrent;
using System.IO;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class LocalDictionaryPipelineTests
{
    private sealed class Ocr : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) =>
            Task.FromResult(new OcrResult([new("佐藤が来た。", 1, new(20, 30, 150, 24))], TimeSpan.Zero));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Runtime : ILocalModelRuntime
    {
        public string Backend => "CPU";
        public readonly ConcurrentQueue<string> Prompts = new();
        public Task PrepareAsync(string path, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) =>
            throw new Exception("Background test must not load a model");
        public Task UnloadAsync(CancellationToken token, LocalTranslationModel? selection = null) => Task.CompletedTask;
        public Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
        { Prompts.Enqueue(prompt); return Task.FromResult(new TranslationResult("사토가 왔다.", TimeSpan.Zero, 0)); }
    }
    private sealed class Service(Runtime runtime, LocalTranslationModel model) : ITranslationService
    {
        public readonly ConcurrentQueue<TranslationRequest> Requests = new();
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Requests.Enqueue(request); return new HyMtTranslationService(runtime, model).TranslateAsync(request, token); }
    }
    internal static void Run()
    {
        foreach (var model in Enum.GetValues<LocalTranslationModel>()) foreach (string mode in new[] { "ADV", "NVL" })
        {
            string root = Path.Combine(Path.GetTempPath(), "KoTrans.LocalDictionary", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var settings = new AppSettings(); settings.Validate(); var profile = settings.DefaultProfile;
            profile.Language = "일본어"; profile.ReadingMode = mode; profile.CaptureMode = CaptureMode.Region;
            profile.Translation.SwitchProvider(TranslationProvider.HyMtLocal); profile.Translation.LocalModel = model;
            profile.CharacterDictionaryEnabled = true; profile.CharacterDictionaryPath = Path.Combine(root, "characters.json");
            void WriteDictionary(string name) => File.WriteAllText(profile.CharacterDictionaryPath,
                "[{\"name\":\"佐藤美咲\",\"korean_name\":\"" + name + " 미사키\",\"info\":\"PRIVATE BIOGRAPHY\"}]");
            WriteDictionary("사토");
            var main = Track(new MainWindow(new SettingsStore(root), new ApiKeyStore(root), () => []));
            var runtime = new Runtime(); var service = new Service(runtime, model);
            try
            {
                main.ApplyLoadedSettings(settings);
                void Translate(string? expectedName)
                {
                    int before = runtime.Prompts.Count;
                    var results = new ConcurrentQueue<PipelineUpdate>();
                    var pipeline = main.CreateProfilePipeline(new Ocr(), service, _ => Task.FromResult(new ImageFrame(400, 160, 1600, new byte[256000])));
                    pipeline.Updated += results.Enqueue;
                    try
                    {
                        pipeline.Start(false); Complete(pipeline.RequestTriggeredAsync());
                        Until(() => results.Any(u => u.Translation == "사토가 왔다."), "Local dictionary pipeline never completed");
                        Check(runtime.Prompts.Count == before + 1, "Changed dictionary reused a stale translation cache");
                        string prompt = runtime.Prompts.Last(); var request = service.Requests.Last();
                        Check(request.PreserveHistory == (mode == "NVL"), "Test did not exercise the intended ADV/NVL request path");
                        Check(expectedName is null ? request.Characters?.Count == 0 : request.Characters?.Single().KoreanName == expectedName + " 미사키",
                            "Profile dictionary did not reach the structured translation request");
                        Check(expectedName is null ? !prompt.Contains("Reference the following translations") : prompt.Contains(expectedName),
                            "Local worker prompt lost the active dictionary selection");
                        Check(!prompt.Contains("PRIVATE BIOGRAPHY"), "Profile biography leaked into the local translation prompt");
                        Check(!prompt.Contains("미사키"), "Written surname was expanded to the full reference name");
                    }
                    finally { Complete(pipeline.DisposeAsync().AsTask()); }
                }
                Translate("사토");
                WriteDictionary("사또"); Translate("사또");
                profile.CharacterDictionaryEnabled = false;
                profile.CharacterDictionaryPath = Path.Combine(root, "missing.json"); Translate(null);
            }
            finally { Close(main); Directory.Delete(root, true); }
        }
    }
}
