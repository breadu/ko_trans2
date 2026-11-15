using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class LocalTranslationTests
{
    private static int count;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { count++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("GGUF0123456789abcdefghijklmnopqr");
    private static LocalModelSpec Spec => new("fixture-revision", "fixture.gguf", Data.Length,
        Convert.ToHexString(SHA256.HashData(Data)).ToLowerInvariant(), new("https://huggingface.co/fixture/resolve/fixed/fixture.gguf"));
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(response(request, ++Calls)); }
    }
    private static HttpResponseMessage Response(int offset = 0, string tag = "\"fixed\"", byte[]? bytes = null)
    {
        var result = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent) {
            Content = new ByteArrayContent(bytes ?? Data[offset..]) };
        result.Headers.ETag = EntityTagHeaderValue.Parse(tag);
        if (offset > 0) result.Content.Headers.ContentRange = new(offset, Data.Length - 1, Data.Length);
        return result;
    }
    private sealed class InterruptedStream(Action interrupt) : MemoryStream(Data)
    {
        private bool first = true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (first) { first = false; return base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], token); }
            interrupt(); token.ThrowIfCancellationRequested(); throw new IOException("synthetic interrupted transfer");
        }
    }
    private sealed class Runtime : ILocalModelRuntime
    {
        public int Prepares, Translates, Unloads;
        public Task UnloadAsync(CancellationToken token, LocalTranslationModel? selection = null) { token.ThrowIfCancellationRequested(); Unloads++; return Task.CompletedTask; }
        public string Backend => "CPU";
        public string Prompt = "";
        public Task PrepareAsync(string model, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) { token.ThrowIfCancellationRequested(); Prepares++; return Task.CompletedTask; }
        public Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
        { Prompt = prompt; Translates++; return Task.FromResult(new TranslationResult("번역", TimeSpan.Zero, 0)); }
    }
    private sealed class ProgressLog : IProgress<LocalPreparationProgress>
    {
        public List<LocalPreparationProgress> Values = [];
        public void Report(LocalPreparationProgress value) => Values.Add(value);
    }
    public static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.LocalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var info = TranslationProviderCatalog.Get(TranslationProvider.HyMtLocal);
            Check((int)TranslationProvider.HyMtLocal == 4 && !info.RequiresModel && !info.SupportsStudy && info.Credentials.Count == 0, "Local capabilities/identity");
            var options = new TranslationOptions { Provider = TranslationProvider.OpenAI, Model = "preserve-model" };
            options.SwitchProvider(TranslationProvider.HyMtLocal); options.Validate();
            Check(options.Model == "", "Local provider unexpectedly needs a model");
            options.SwitchProvider(TranslationProvider.OpenAI); Check(options.Model == "preserve-model", "Cloud model lost");
            Check(LocalModelInstaller.AllowedDownloadUri(new("https://cas-bridge.xethub.hf.co/fixture")), "Official CDN rejected");
            foreach (var bad in new[] { "http://huggingface.co/x", "https://huggingface.co.evil.invalid/x", "https://user@hf.co/x", "https://hf.co:444/x" })
                Check(!LocalModelInstaller.AllowedDownloadUri(new(bad)), "Unsafe redirect accepted");
            await Reject<ArgumentException>(() => Task.FromResult(new LocalModelInstaller(root, Spec with { FileName = "../escape.gguf" })));

            using (var handler = new Handler((_, _) => Response()))
            using (var client = new HttpClient(handler))
            {
                var installer = new LocalModelInstaller(Path.Combine(root, "first"), Spec, client, () => long.MaxValue);
                var runtime = new Runtime(); var preparation = new LocalTranslationPreparation(installer, runtime);
                Check(!await preparation.EnsureAsync(() => Task.FromResult(false), null, default) && handler.Calls == 0 && runtime.Prepares == 0, "No consent started work");
                int resumed = 0;
                if (await preparation.EnsureAsync(() => Task.FromResult(true), null, default)) resumed++;
                Check(resumed == 1 && runtime.Prepares == 1 && handler.Calls == 1, "Consent did not complete into original continuation");
                await Task.WhenAll(installer.InstallAsync(null, default), installer.InstallAsync(null, default));
                Check(handler.Calls == 1 && await installer.IsInstalledAsync(), "Verified installation downloaded again");
                Check(await preparation.EnsureAsync(() => throw new Exception("Asked twice"), null, default), "Installed model reuse failed");
                await File.WriteAllBytesAsync(installer.ModelPath, new byte[Data.Length]);
                Check(!await installer.IsInstalledAsync(), "Corrupt model trusted metadata");
            }
            using (var handler = new Handler((_, _) => Response()))
            using (var client = new HttpClient(handler)) {
                var installer = new LocalModelInstaller(Path.Combine(root, "parallel"), Spec, client, () => long.MaxValue);
                var progress = new ProgressLog();
                await Task.WhenAll(installer.InstallAsync(progress, default), installer.InstallAsync(progress, default));
                Check(handler.Calls == 1, "Concurrent fresh installation duplicated download");
                Check(progress.Values.Any(v => v.Received == Data.Length && v.Total == Data.Length && v.Percent == 100), "Download progress never completed");
            }
            using (var handler = new Handler((request, call) => {
                if (request.RequestUri!.Host == "huggingface.co") return new(HttpStatusCode.Redirect) { Headers = { Location = new("https://cas-bridge.xethub.hf.co/temporary/" + call) } };
                return call == 2 ? new(HttpStatusCode.Forbidden) : Response();
            }))
            using (var client = new HttpClient(handler)) {
                var installer = new LocalModelInstaller(Path.Combine(root, "expired"), Spec, client, () => long.MaxValue);
                await installer.InstallAsync(null, default);
                Check(handler.Calls == 4, "Expired CDN URL was not refreshed at official origin");
            }
            foreach (string scenario in new[] { "resume", "ignore", "changed", "cancel", "range416" })
            {
                using var cancel = new CancellationTokenSource();
                using var handler = new Handler((request, call) => {
                    if (call == 1) {
                        var first = Response(); first.Content = new StreamContent(new InterruptedStream(() => { if (scenario == "cancel") cancel.Cancel(); }));
                        first.Content.Headers.ContentLength = Data.Length; return first;
                    }
                    if (call == 2) {
                        Check(request.Headers.Range?.Ranges.Single().From == 8 && request.Headers.GetValues("If-Range").Single() == "\"fixed\"", "Resume lost Range/ETag");
                        if (scenario == "ignore") return Response();
                        if (scenario == "range416") return new(HttpStatusCode.RequestedRangeNotSatisfiable);
                        return Response(8, scenario == "changed" ? "\"different\"" : "\"fixed\"");
                    }
                    Check(request.Headers.Range is null, "Changed object did not restart"); return Response();
                });
                using var client = new HttpClient(handler);
                var installer = new LocalModelInstaller(Path.Combine(root, scenario), Spec, client, () => long.MaxValue);
                if (scenario == "cancel") {
                    await Reject<OperationCanceledException>(() => installer.InstallAsync(null, cancel.Token));
                    Check(File.Exists(installer.ModelPath + ".part") && !File.Exists(installer.ModelPath), "Cancelled transfer lost partial file/marked ready");
                    installer = new(installer.DirectoryPath, Spec, client, () => long.MaxValue);
                }
                await installer.InstallAsync(null, default);
                Check((await File.ReadAllBytesAsync(installer.ModelPath)).SequenceEqual(Data), "Repaired/resumed bytes differ: " + scenario);
            }
            using (var handler = new Handler((_, _) => Response(bytes: new byte[Data.Length])))
            using (var client = new HttpClient(handler)) {
                var installer = new LocalModelInstaller(Path.Combine(root, "hash"), Spec, client, () => long.MaxValue);
                await Reject<TranslationException>(() => installer.InstallAsync(null, default));
                Check(!File.Exists(installer.ModelPath) && !File.Exists(installer.ModelPath + ".part"), "Bad checksum installed or retained");
            }
            using (var handler = new Handler((_, _) => Response()))
            using (var client = new HttpClient(handler)) {
                var installer = new LocalModelInstaller(Path.Combine(root, "old-model"), Spec with { Revision = "new", Sha256 = new string('0', 64) }, client, () => long.MaxValue);
                Directory.CreateDirectory(installer.DirectoryPath); await File.WriteAllBytesAsync(installer.ModelPath, Data);
                await Reject<TranslationException>(() => installer.InstallAsync(null, default));
                Check((await File.ReadAllBytesAsync(installer.ModelPath)).SequenceEqual(Data), "Failed replacement damaged existing model");
            }
            using (var handler = new Handler((_, _) => Response()))
            using (var client = new HttpClient(handler)) {
                var installer = new LocalModelInstaller(Path.Combine(root, "space"), Spec, client, () => 0);
                await Reject<TranslationException>(() => installer.InstallAsync(null, default));
                Check(handler.Calls == 0, "Disk check occurred after download");
            }
            using (var handler = new Handler((_, _) => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.invalid/model") } }))
            using (var client = new HttpClient(handler)) {
                var installer = new LocalModelInstaller(Path.Combine(root, "redirect"), Spec, client, () => long.MaxValue);
                await Reject<TranslationException>(() => installer.InstallAsync(null, default));
                Check(handler.Calls == 1, "Untrusted redirect was followed");
            }
            await RemovalAndSilentStart(root);
            var fake = new Runtime(); var service = new HyMtTranslationService(fake);
            await service.TranslateAsync(new("現在の台詞", "NEVER INCLUDE", [new("前の原文", "NEVER TRANSLATION")], new() { Provider = TranslationProvider.HyMtLocal }), default);
            Check(fake.Prompt.StartsWith(HyMtPrompt.Prefix + "[Background Information]\n前の原文") && fake.Prompt.EndsWith("現在の台詞" + HyMtPrompt.Suffix), "Local context/source framing changed");
            Check(!fake.Prompt.Contains("NEVER"), "System/translated history leaked into local prompt");
            await Reject<TranslationException>(() => service.TranslateAsync(new("<|extra_0|>", "", [], new()), default));
            await Worker(root);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} local installation, consent, resume, integrity, prompt and isolated worker assertions. Mock only.");
        await LocalModelSelectionTests.Run();
        await LocalPromptScopeTests.Run();
        await LocalLongTranslationTests.Run();
        CharacterNameHintTests.Run();
    }
    private static async Task RemovalAndSilentStart(string root)
    {
        using var handler = new Handler((_, _) => Response()); using var client = new HttpClient(handler);
        var installer = new LocalModelInstaller(Path.Combine(root, "remove"), Spec, client, () => long.MaxValue);
        var runtime = new Runtime(); var preparation = new LocalTranslationPreparation(installer, runtime);
        Check(!await installer.IsInstalledAsync() && !Directory.Exists(installer.DirectoryPath), "Status query created model data");
        await installer.InstallAsync(null, default);
        using (var lease = new FileStream(installer.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            await Reject<IOException>(() => preparation.DeleteAsync());
            Check(File.Exists(installer.ModelPath), "Removal ignored a live model lease");
        }
        runtime.Unloads = 0;
        var progress = new ProgressLog();
        await preparation.EnsureAsync(() => throw new Exception("Repeated consent"), progress, default);
        Check(progress.Values.Count == 0 && handler.Calls == 1, "Installed cold engine emitted download window progress");
        foreach (var suffix in new[] { ".part", ".resume.json", ".resume.json.tmp", ".verified.json.tmp" })
            await File.WriteAllTextAsync(installer.ModelPath + suffix, "partial metadata");
        await preparation.DeleteAsync();
        Check(runtime.Unloads == 1 && !Directory.Exists(installer.DirectoryPath), "Removal retained managed files or forgot runtime");
        Check(!await installer.IsInstalledAsync() && !Directory.Exists(installer.DirectoryPath), "Status query recreated deleted data");
        await preparation.EnsureAsync(() => Task.FromResult(true), null, default);
        Check(await installer.IsInstalledAsync() && handler.Calls == 2, "Reinstall after remove failed");
        await Task.WhenAll(installer.IsInstalledAsync(), installer.DeleteAsync());
        Check(!await installer.IsInstalledAsync(), "Concurrent status query blocked/recreated removed data");
        await installer.InstallAsync(null, default);
        string other = Path.Combine(installer.DirectoryPath, "user-note.txt"); await File.WriteAllTextAsync(other, "keep");
        await preparation.DeleteAsync();
        Check(File.Exists(other) && !File.Exists(installer.ModelPath) && Directory.GetFiles(installer.DirectoryPath).Length == 1, "Removal deleted an unmanaged file");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Reject<OperationCanceledException>(() => installer.DeleteAsync(cancelled.Token));
        var contexts = new TranslationContexts(); var id = Guid.NewGuid(); var ocr = new OcrOptions("en", "ADV", false);
        var local = new TranslationOptions { Provider = TranslationProvider.HyMtLocal };
        var cloud = new TranslationOptions { Provider = TranslationProvider.Gemini };
        var oldLocal = contexts.Get(id, ocr, local, ""); var oldCloud = contexts.Get(id, ocr, cloud, "");
        oldLocal.Accept("old source", "old translation", false); contexts.ResetProvider(TranslationProvider.HyMtLocal);
        Check(contexts.Get(id, ocr, local, "").History.Count == 0 && ReferenceEquals(oldCloud, contexts.Get(id, ocr, cloud, "")), "Local removal reset another provider or retained local history");
    }
    private static async Task Worker(string root)
    {
        string model = Path.Combine(root, "runtime.gguf"); await File.WriteAllBytesAsync(model, Data);
        var logs = new List<string>(); var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new LocalModelRuntime(Environment.ProcessPath!, line => { lock (logs) logs.Add(line); if (line == "slow-start") slow.TrySetResult(); });
        await runtime.PrepareAsync(model, default);
        Check(runtime.Backend == "CPU" && logs.Any(l => l.Contains("vulkan")), "Failed GPU did not fall back to CPU");
        var first = await runtime.TranslateAsync("first", default); var second = await runtime.TranslateAsync("second", default);
        Check(first.Text == second.Text, "Worker/model was loaded again for each line");
        foreach (var (prompt, expected) in new[] { ("INPUT_LIMIT", LocalTranslationLimit.Input), ("OUTPUT_LIMIT", LocalTranslationLimit.Output) }) {
            try { await runtime.TranslateAsync(prompt, default); throw new Exception("Worker limit was accepted as a translation"); }
            catch (LocalTranslationLimitException ex) { Check(ex.Limit == expected, "Worker limit kind was lost"); }
        }
        Check((await runtime.TranslateAsync("after-limits", default)).Text == first.Text, "Budget recovery reloaded the model");
        try { await runtime.TranslateAsync("INFERENCE_ERROR", default); throw new Exception("Worker inference failure was accepted"); }
        catch (TranslationException ex) { Check(ex is not LocalTranslationLimitException && !ex.Message.Contains("길이") && ex.Message.Contains("다시 시작"),
            "Generic worker failure was misreported as an input length limit"); }
        using var cts = new CancellationTokenSource(); var pending = runtime.TranslateAsync("SLOW", cts.Token);
        await slow.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        await Reject<OperationCanceledException>(() => pending);
        Check((await runtime.TranslateAsync("next", default)).Text == first.Text, "Cancellation desynchronized/reloaded the worker");
        await Reject<IOException>(() => runtime.TranslateAsync("CRASH", default));
        Check((await runtime.TranslateAsync("recovered", default)).Text.StartsWith("모의 번역"), "Owned worker crash did not recover");
        await runtime.UnloadAsync();
        await Reject<TranslationException>(() => runtime.TranslateAsync("unloaded", default));
        await runtime.PrepareAsync(model, default);
        Check((await runtime.TranslateAsync("reinstalled", default)).Text.StartsWith("모의 번역"), "Runtime cannot prepare again after unload");
        await runtime.DisposeAsync();
        string failed = Path.Combine(root, "fail-all.gguf"); await File.WriteAllBytesAsync(failed, Data);
        await using var unusable = new LocalModelRuntime(Environment.ProcessPath!);
        await Reject<TranslationException>(() => unusable.PrepareAsync(failed, default));
        string delayed = Path.Combine(root, "slow-load.gguf"); await File.WriteAllBytesAsync(delayed, Data);
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slowRuntime = new LocalModelRuntime(Environment.ProcessPath!, line => { if (line == "loading") loading.TrySetResult(); });
        using var stop = new CancellationTokenSource(); var preparing = slowRuntime.PrepareAsync(delayed, stop.Token);
        await loading.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Reject<OperationCanceledException>(() => preparing);
    }
}
