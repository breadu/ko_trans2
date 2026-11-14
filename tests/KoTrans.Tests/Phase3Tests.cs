using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class Phase3Tests
{
    private static int count;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); count++; }
    private static async Task Reject(Func<Task> action, string message)
    { try { await action(); } catch (TranslationException) { count++; return; } throw new Exception(message); }
    private static async Task Until(Func<bool> predicate)
    { for (int i = 0; i < 500; i++) { if (predicate()) return; await Task.Delay(10); } throw new Exception("Timed out waiting for test pipeline"); }
    private static TranslationRequest Request() => new("Current sentence", TranslationPrompt.Build([]), [new("Previous line", "이전 대사")], new() { Model = "exact-user-model" });
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private const string OpenAiJson = """{"status":"completed","output":[{"type":"reasoning","summary":[]},{"type":"message","content":[{"type":"output_text","text":"번역"}]}]}""";
    public static async Task Run()
    {
        await Http(); await Storage(); await Pipeline();
        Console.WriteLine($"PASS: {count} assertions (Translation). Mock HTTP only; no external AI calls or real settings.");
    }
    private static async Task Http()
    {
        foreach (var provider in new[] { TranslationProvider.Gemini, TranslationProvider.OpenAI, TranslationProvider.Ollama })
        {
            string response = provider switch
            {
                TranslationProvider.OpenAI => OpenAiJson,
                TranslationProvider.Gemini => """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"thought":true,"text":"hidden"},{"text":"번역"}]}}]}""",
                _ => """{"done":true,"message":{"content":"번역"}}"""
            };
            using var handler = new Handler((_, _) => Task.FromResult(Response(response))); using var client = new HttpClient(handler);
            ITranslationService service = provider switch
            { TranslationProvider.OpenAI => new OpenAiTranslationService(client, "synthetic-key"), TranslationProvider.Gemini => new GeminiTranslationService(client, "synthetic-key"), _ => new OllamaTranslationService(client) };
            var result = await service.TranslateAsync(Request(), default);
            Check(result.Text == "번역" && result.HttpAttempts == 1, provider + " response parsing");
            using var body = JsonDocument.Parse(handler.Bodies.Single());
            Check(handler.Uris.Single().Contains(provider == TranslationProvider.Gemini ? "exact-user-model:generateContent" : provider == TranslationProvider.OpenAI ? "/v1/responses" : "/api/chat"), "Endpoint/model drift");
            if (provider != TranslationProvider.Gemini) Check(body.RootElement.GetProperty("model").GetString() == "exact-user-model", "Model silently changed");
            Check(handler.Bodies[0].Contains("Previous line") && handler.Bodies[0].Contains("Current sentence"), "History missing");
            Check(!handler.Uris[0].Contains("synthetic-key"), "Credential in URL");
            if (provider == TranslationProvider.OpenAI) Check(!body.RootElement.GetProperty("store").GetBoolean(), "Remote storage enabled");
        }
        await Reject(() => Task.Run(() => TranslationServices.Create(new() { Provider = TranslationProvider.Gemini, Model = "m" }, null)), "Missing key accepted");
        await Reject(() => Task.Run(() => TranslationServices.Create(new() { Provider = TranslationProvider.Gemini }, "fake")), "Empty model accepted");
        foreach (var code in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            int calls = 0;
            using var handler = new Handler((_, _) => { var response = Response(++calls == 1 ? "{}" : OpenAiJson, calls == 1 ? code : HttpStatusCode.OK); response.Headers.RetryAfter = new(TimeSpan.Zero); return Task.FromResult(response); });
            using var client = new HttpClient(handler); var service = new OpenAiTranslationService(client, "fake");
            Check((await service.TranslateAsync(Request(), default)).HttpAttempts == 2 && calls == 2, "Transient retry policy");
        }
        foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            using var handler = new Handler((_, _) => { var response = Response("secret source should not escape", code); response.Headers.RetryAfter = new(TimeSpan.Zero); return Task.FromResult(response); });
            using var client = new HttpClient(handler); var service = new OpenAiTranslationService(client, "fake");
            await Reject(async () => await service.TranslateAsync(Request(), default), "API error ignored");
            Check(handler.Bodies.Count == (code is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable ? 2 : 1), "Retry unbounded");
        }
        using (var handler = new Handler((_, _) => throw new HttpRequestException("sensitive detail")))
        using (var client = new HttpClient(handler))
        {
            await Reject(async () => await new OpenAiTranslationService(client, "fake").TranslateAsync(Request(), default), "Network error ignored");
            Check(handler.Bodies.Count == 1, "Ambiguous network request retried");
        }
        using (var handler = new Handler((_, _) => Task.FromResult(Response("{}"))))
        using (var client = new HttpClient(handler))
        { await Reject(async () => await new OpenAiTranslationService(client, "fake").TranslateAsync(Request(), default), "Invalid response accepted"); Check(handler.Bodies.Count == 1, "Parse failure retried"); }
        using (var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(OpenAiJson); }))
        using (var client = new HttpClient(handler))
        {
            using var cts = new CancellationTokenSource(30);
            try { await new OpenAiTranslationService(client, "fake").TranslateAsync(Request(), cts.Token); throw new Exception("Cancel ignored"); }
            catch (OperationCanceledException) { count++; }
            var timeoutRequest = Request() with { Options = new() { Model = "m", TimeoutSeconds = 5 } };
            await Reject(async () => await new OpenAiTranslationService(client, "fake").TranslateAsync(timeoutRequest, default), "Timeout ignored");
        }
    }
    private static async Task Storage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Phase3", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var vault = new ApiKeyStore(dir); vault.Save(TranslationProvider.OpenAI, "synthetic-only-key");
            Check(vault.Read(TranslationProvider.OpenAI) == "synthetic-only-key", "DPAPI roundtrip");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "OpenAI.key"))).Contains("synthetic-only-key"), "Key stored plaintext");
            vault.Save(TranslationProvider.OpenAI, "replacement"); Check(vault.Read(TranslationProvider.OpenAI) == "replacement", "Key replace");
            File.WriteAllBytes(Path.Combine(dir, "OpenAI.key"), [1, 2, 3]);
            await Reject(() => Task.Run(() => vault.Read(TranslationProvider.OpenAI)), "Corrupt key accepted");
            vault.Delete(TranslationProvider.OpenAI); Check(!vault.Contains(TranslationProvider.OpenAI), "Key delete");
            var ini = Path.Combine(dir, "explicit-fixture.ini"); var iniText = "[Settings]\r\nGEMINI_API_KEY=only-a-fixture\r\n"; File.WriteAllText(ini, iniText, Encoding.Unicode);
            Check(vault.ImportSelectedIni(ini) == 1 && vault.Read(TranslationProvider.Gemini) == "only-a-fixture" && File.ReadAllText(ini) == iniText, "Selected INI import changed source");
            var settings = new AppSettings { SchemaVersion = 1 }; settings.Profiles[0].CaptureArea = new(-500, 100, 600, 200); settings.Profiles[0].Vertical = true;
            var original = JsonSerializer.Serialize(settings); var path = Path.Combine(dir, "settings.json"); File.WriteAllText(path, original);
            var store = new SettingsStore(dir); var migrated = await store.LoadAsync();
            Check(migrated.SchemaVersion == 3 && migrated.Profiles[0].CaptureArea.X == -500 && migrated.Profiles[0].Vertical, "Migration lost profile");
            Check(File.ReadAllText(path + ".v1.bak") == original && File.ReadAllText(path) == original, "Migration overwrote source/backup");
            await store.SaveAsync(migrated); Check((await store.LoadAsync()).SchemaVersion == 3, "Migration save failed");
            var dict = Path.Combine(dir, "characters.json"); File.WriteAllText(dict, """[{"name":"A","korean_name":"에이","info":"first","description":"second"}]""");
            var chars = CharacterDictionary.Load(dict); Check(chars[0].Description == "first / second", "Dictionary compatibility");
            await Reject(() => Task.Run(() => TranslationPrompt.Build([new("Name", "이름", new string('x', 24001))])), "Unbounded dictionary prompt");
            var contexts = new TranslationContexts(); var id = Guid.NewGuid(); var opts = new TranslationOptions { Model = "one" }; var ocr = new OcrOptions("en", "ADV", false);
            var a = contexts.Get(id, ocr, opts, "prompt"); a.Accept("same", "번역", false);
            Check(a.TryLast("same", out _) && !a.TryLast("changed", out _), "Recent result cache");
            Check(contexts.Get(Guid.NewGuid(), ocr, opts, "prompt").History.Count == 0 && contexts.Get(id, ocr, opts with { Model = "two" }, "prompt").History.Count == 0, "Profile/model history leak");
            a.Accept("next", "다음", false); Check(!a.TryLast("same", out _), "Same text reused in changed context");
            for (int i = 0; i < 10; i++) a.Accept("line" + i, "text", false);
            Check(a.History.Count == 4, "Unbounded conversation"); contexts.Reset(id); Check(contexts.Get(id, ocr, opts, "prompt").History.Count == 0, "Context reset");
        }
        finally { Directory.Delete(dir, true); }
    }
    private static ImageFrame Frame(byte value, int width = 96, int height = 48)
    { var bytes = new byte[width * height * 4]; Array.Fill(bytes, value); return new(width, height, width * 4, bytes); }
    private static TranslationPipeline Create(IOcrService ocr, ITranslationService ai, Func<ImageFrame> source, TranslationSession? context = null, int settle = 30)
        => new(ocr, ai, _ => Task.FromResult(source()), new("en", "ADV", false), new() { Model = "fake" }, context ?? new(), "prompt", TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(settle));
    private static async Task Pipeline()
    {
        var frame = Frame(30); var ocr = new FakeOcr(); var ai = new FakeAi(); var results = new ConcurrentQueue<PipelineUpdate>();
        await using (var pipeline = Create(ocr, ai, () => Volatile.Read(ref frame)))
        {
            pipeline.Updated += results.Enqueue; pipeline.Start(true);
            await Until(() => results.Any(x => x.Translation == "T30")); await Task.Delay(180);
            Check(ocr.Calls == 1 && ai.Calls == 1, "Unchanged frame repeated OCR/AI");
            Volatile.Write(ref frame, Frame(90)); await Until(() => results.Any(x => x.Translation == "T90"));
            Check(ocr.Calls == 2 && ai.Calls == 2, "New frame not translated");
            await pipeline.RequestManualAsync(); await Until(() => results.Any(x => x.CacheHits == 1)); Check(ai.Calls == 2, "Manual same text called AI");
            await pipeline.RequestManualAsync(true); await Until(() => ai.Calls == 3); await Until(() => results.Count(x => x.Translation == "T90") >= 3);
            Check(ai.Calls == 3, "Explicit retranslation missing");
            pipeline.Stop(); int before = ocr.Calls; Volatile.Write(ref frame, Frame(140)); await Task.Delay(80); Check(ocr.Calls == before, "Stop still captures");
        }
        // Fast typewriter changes, including a short pause that finishes OCR but not the text settle interval.
        frame = Frame(10); ocr = new FakeOcr(); ai = new FakeAi(); results = new();
        await using (var pipeline = Create(ocr, ai, () => Volatile.Read(ref frame), settle: 140))
        {
            pipeline.Updated += results.Enqueue; pipeline.Start(true);
            await Until(() => ocr.Calls > 0);
            foreach (byte value in new byte[] { 40, 70, 100, 130 }) { Volatile.Write(ref frame, Frame(value)); await Task.Delay(35); }
            await Until(() => results.Any(x => x.Translation == "T130")); Check(ai.Calls == 1, "Typewriter paid intermediate text");
        }
        // A provider/native implementation ignoring cancellation must still never publish stale output.
        ocr = new FakeOcr { Delay = 80 }; ai = new FakeAi { Delay = 80 }; results = new();
        await using (var pipeline = Create(ocr, ai, () => Frame(1)))
        {
            pipeline.Updated += results.Enqueue; pipeline.Start(false);
            await pipeline.RequestManualAsync(image: Frame(20)); await Until(() => ocr.Calls == 1);
            await pipeline.RequestManualAsync(image: Frame(60));
            await Until(() => ai.Calls == 1);
            await pipeline.RequestManualAsync(image: Frame(120));
            await Until(() => results.Any(x => x.Translation == "T120"));
            Check(!results.Any(x => x.Translation is "T20" or "T60"), "Stale result published");
            Check(ocr.MaxConcurrent == 1, "Parallel native OCR");
        }
        // Clipboard requires Start and coalesces updates. Manual force bypasses duplicate filter.
        ai = new FakeAi(); ocr = new FakeOcr(); results = new();
        await using (var pipeline = Create(ocr, ai, () => Frame(1)))
        {
            pipeline.Updated += results.Enqueue; pipeline.SubmitClipboard("before activation"); await Task.Delay(30); Check(ai.Calls == 0, "Clipboard sends before start");
            pipeline.Start(false); pipeline.SubmitClipboard("same"); pipeline.SubmitClipboard("same"); await Until(() => results.Any(x => x.Translation == "Tsame"));
            Check(ai.Calls == 1 && ocr.Calls == 0, "Clipboard duplicates or uses OCR");
            pipeline.SubmitClipboard("same", true, true); await Until(() => ai.Calls == 2);
        }
        // ROI move alone does not trigger OCR. A newly drawn object outside ROI does.
        var gate = new RealtimeFrameGate(); var blank = Frame(0, 192, 96); gate.Evaluate(blank); gate.Evaluate(blank);
        gate.Observe(new([new("text", 1, new(20, 20, 12, 8))], TimeSpan.Zero), blank);
        Check(Enumerable.Range(0, 20).All(_ => gate.Evaluate(blank) == KoTrans.Ocr.FrameDecision.Unchanged), "ROI adoption re-recognized unchanged frame");
        var pixels = blank.Pixels.ToArray(); for (int y = 65; y < 80; y++) for (int x = 145; x < 175; x++) for (int c = 0; c < 3; c++) pixels[y * blank.Stride + x * 4 + c] = 255;
        var outside = new ImageFrame(192, 96, blank.Stride, pixels);
        Check(Enumerable.Range(0, 15).Any(_ => gate.Evaluate(outside) == KoTrans.Ocr.FrameDecision.Probe), "Outside ROI missed");
        var resized = Frame(60, 200, 100); gate.Evaluate(resized); Check(gate.Evaluate(resized) == KoTrans.Ocr.FrameDecision.Ready, "Resolution change lost");
        // Restart shares context but not cancellation or change-detector state.
        var session = new TranslationSession(); ai = new FakeAi();
        for (int i = 0; i < 3; i++)
        {
            results = new(); await using var pipeline = Create(new FakeOcr(), ai, () => Frame(60), session);
            pipeline.Updated += results.Enqueue; pipeline.Start(true); await Until(() => results.Any(x => x.Translation == "T60"));
        }
        Check(ai.Calls == 1, "Restart lost cache/context");
        // Error stays recoverable, without retrying a stable failed source forever.
        ai = new FakeAi { Fail = true }; results = new();
        await using (var pipeline = Create(new FakeOcr(), ai, () => Frame(60)))
        {
            pipeline.Updated += results.Enqueue; pipeline.Start(true); await Until(() => results.Any(x => x.State == PipelineState.Error));
            await Task.Delay(120); Check(ai.Calls == 1, "Failure retry loop"); ai.Fail = false;
            await pipeline.RequestManualAsync(); await Until(() => results.Any(x => x.Translation == "T60")); Check(ai.Calls == 2, "Error recovery failed");
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = []; public List<string> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Bodies.Add(await request.Content!.ReadAsStringAsync(token)); Uris.Add(request.RequestUri!.ToString()); return await send(request, token); }
    }
    private sealed class FakeAi : ITranslationService
    {
        public int Calls, Delay; public bool Fail;
        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Interlocked.Increment(ref Calls); if (Delay > 0) await Task.Delay(Delay); if (Fail) throw new TranslationException("Mock failure"); return new("T" + request.Text, TimeSpan.FromMilliseconds(Delay), 0); }
    }
    private sealed class FakeOcr : IOcrService
    {
        public int Calls, Delay, MaxConcurrent; private int concurrent;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref concurrent));
            if (Delay > 0) await Task.Delay(Delay); Interlocked.Decrement(ref concurrent);
            return new([new(frame.Pixels.Span[0].ToString(), 1, new(0, 0, frame.Width, frame.Height))], TimeSpan.Zero);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
