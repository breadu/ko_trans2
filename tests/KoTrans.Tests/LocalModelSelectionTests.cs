using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class LocalModelSelectionTests
{
    private static int assertions;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { assertions++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Download(byte[] data) : HttpMessageHandler
    {
        internal int Calls; internal bool Cancel;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; if (Cancel) throw new OperationCanceledException(token); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }); }
    }
    public static async Task Run()
    {
        const LocalTranslationModel standard = LocalTranslationModel.Standard7B, light = LocalTranslationModel.Lightweight18B;
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.ModelSelection", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            Check(JsonSerializer.Deserialize<TranslationOptions>("{\"Provider\":4}")!.LocalModel == standard && new TranslationOptions().LocalModel == standard, "Legacy/new settings must retain 7B");
            var settings = new AppSettings(); settings.Validate(); var game = settings.CreateGameProfile("light fixture"); game.Translation.LocalModel = light;
            game.Translation.Provider = TranslationProvider.HyMtLocal; game.Translation.OpenAIModel = "preserved-other-model";
            var store = new SettingsStore(root); await store.SaveAsync(settings); var restored = await store.LoadAsync();
            Check(restored.Profiles.Single(p => p.Id == game.Id).Translation is { LocalModel: light, OpenAIModel: "preserved-other-model" }
                && restored.DefaultProfile.Translation.LocalModel == standard, "Profile/other-provider settings lost in roundtrip");
            Check((int)TranslationProvider.HyMtLocal == 4 && LocalModelSpec.HyMt2.Bytes == 4624648896 && LocalModelSpec.HyMt2Light.Bytes == 1133080448, "Frozen model/provider contract changed");
            var history = new[] { new ConversationTurn("前の原文", "PREVIOUS_TRANSLATION_MUST_NOT_LEAK") };
            string oldPrompt = HyMtPrompt.Build("現在の台詞", history), newPrompt = HyMtPrompt.Build("現在の台詞", history, light);
            Check(oldPrompt.StartsWith("<|startoftext|>[Background Information]\n前の原文") && oldPrompt.EndsWith("現在の台詞<|extra_0|>"), "7B framing changed");
            Check(newPrompt.StartsWith("<｜hy_begin▁of▁sentence｜><｜hy_User｜>[Background Information]\n前の原文") && newPrompt.EndsWith("現在の台詞<｜hy_Assistant｜>")
                && !newPrompt.Contains("PREVIOUS_TRANSLATION_MUST_NOT_LEAK") && !newPrompt.Contains("<|"), "1.8B official framing/history isolation failed");
            await Reject<TranslationException>(() => Task.FromResult(HyMtPrompt.Build("<｜hy_Assistant｜>", [], light)));
            Check(LocalTranslationModels.Get(standard).RequiredCpuMemory == 6UL * 1024 * 1024 * 1024
                && LocalTranslationModels.Get(light).RequiredCpuMemory == 3UL * 1024 * 1024 * 1024, "Measured model memory policy changed");
            var contexts = new TranslationContexts(); var id = Guid.NewGuid(); var ocr = new OcrOptions("ja", "ADV", false);
            var options = new TranslationOptions { Provider = TranslationProvider.HyMtLocal };
            var stdContext = contexts.Get(id, ocr, options, ""); stdContext.Accept("source", "standard translation", false);
            var lightOptions = options with { LocalModel = light }; var lightContext = contexts.Get(id, ocr, lightOptions, "");
            Check(!ReferenceEquals(stdContext, lightContext) && lightContext.History.Count == 0 && !lightContext.TryLast("source", out _), "7B/1.8B history or cache mixed");
            var cloud = options with { Provider = TranslationProvider.Gemini, Model = "mock" }; var cloudContext = contexts.Get(id, ocr, cloud, "");
            Check(ReferenceEquals(cloudContext, contexts.Get(id, ocr, cloud with { LocalModel = light }, "")), "Inactive local option reset another provider");
            contexts.ResetLocalModel(light);
            Check(ReferenceEquals(stdContext, contexts.Get(id, ocr, options, "")) && ReferenceEquals(cloudContext, contexts.Get(id, ocr, cloud, ""))
                && !ReferenceEquals(lightContext, contexts.Get(id, ocr, lightOptions, "")), "Model deletion reset another model/provider context");
            byte[] bytes = "GGUF temporary model fixture"u8.ToArray();
            LocalModelSpec Spec(string name) => new("fixture", name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new("https://huggingface.co/fixture/model"));
            using var handler = new Download(bytes); using var http = new HttpClient(handler);
            var std = new LocalModelInstaller(Path.Combine(root, "models"), Spec(LocalModelSpec.HyMt2.FileName), http, () => long.MaxValue);
            var tiny = new LocalModelInstaller(Path.Combine(std.DirectoryPath, "1.8B"), Spec(LocalModelSpec.HyMt2Light.FileName), http, () => long.MaxValue);
            await std.InstallAsync(null, default); await tiny.InstallAsync(null, default);
            Check(handler.Calls == 2 && await std.IsInstalledAsync() && await tiny.IsInstalledAsync(), "Models cannot coexist");
            await tiny.DeleteAsync(); Check(await std.IsInstalledAsync() && !await tiny.IsInstalledAsync(), "Light deletion damaged 7B");
            handler.Cancel = true; await Reject<OperationCanceledException>(() => tiny.InstallAsync(null, default));
            Check(await std.IsInstalledAsync() && (await File.ReadAllBytesAsync(std.ModelPath)).SequenceEqual(bytes), "Failed light installation damaged 7B");
            handler.Cancel = false; await tiny.InstallAsync(null, default); await std.DeleteAsync();
            Check(await tiny.IsInstalledAsync() && !await std.IsInstalledAsync(), "7B deletion removed nested light model");
            int calls = handler.Calls; await tiny.InstallAsync(null, default); Check(handler.Calls == calls, "Installed light model redownloaded");
            await std.InstallAsync(null, default);
            await using (var insufficient = new LocalModelRuntime(Environment.ProcessPath!, availableMemory: () => 2UL * 1024 * 1024 * 1024))
                await Reject<TranslationException>(() => insufficient.PrepareAsync(tiny.ModelPath, default, light));
            // Existing isolated fake Worker: Vulkan fails, CPU emits synthetic Korean/PID.
            await using var runtime = new LocalModelRuntime(Environment.ProcessPath!, availableMemory: () => 4UL * 1024 * 1024 * 1024);
            await Reject<TranslationException>(() => runtime.PrepareAsync(std.ModelPath, default, standard));
            await runtime.PrepareAsync(tiny.ModelPath, default, light);
            var service = new HyMtTranslationService(runtime, light); var request = new TranslationRequest("Hello.", "", [], lightOptions);
            var first = await service.TranslateAsync(request, default);
            await Reject<TranslationException>(() => new HyMtTranslationService(runtime).TranslateAsync(request, default));
            await runtime.UnloadAsync(default, standard);
            Check((await service.TranslateAsync(request, default)).Text == first.Text, "Other-model unload retired the active light Worker");
            await Reject<IOException>(() => Task.Run(() => { using var write = new FileStream(tiny.ModelPath, FileMode.Open, FileAccess.Write); }));
            await runtime.UnloadAsync(default, light);
            using (var write = new FileStream(tiny.ModelPath, FileMode.Open, FileAccess.Write)) Check(write.CanWrite, "Selected model lease survived unload");
            await using var switching = new LocalModelRuntime(Environment.ProcessPath!, availableMemory: () => ulong.MaxValue);
            await switching.PrepareAsync(std.ModelPath, default); var before = await switching.TranslateAsync("first", default);
            await switching.PrepareAsync(tiny.ModelPath, default, light); var after = await switching.TranslateAsync("second", default, light);
            Check(before.Text != after.Text, "Model switch reused the previous Worker");
            using (var write = new FileStream(std.ModelPath, FileMode.Open, FileAccess.Write)) Check(write.CanWrite, "Previous model lease not released");
            await switching.PrepareAsync(std.ModelPath, default); var back = await switching.TranslateAsync("third", default);
            Check(back.Text != after.Text && await std.IsInstalledAsync() && await tiny.IsInstalledAsync(), "Switch back damaged/replaced model files");
            await Reject<FileNotFoundException>(() => switching.PrepareAsync(Path.Combine(root, "missing.gguf"), default, light));
            await Reject<TranslationException>(() => switching.TranslateAsync("stale old service", default));
        } finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS: {assertions} local model compatibility/preservation/context/cache/memory/lease/Worker-switch assertions. Temporary files and Mock only.");
    }
}
