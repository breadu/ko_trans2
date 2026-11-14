using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class Phase4Tests
{
    private static int count;
    private static void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; }
    private static void Reject(Action action, string name)
    { try { action(); } catch (Exception e) when (e is TranslationException or InvalidDataException or IOException) { count++; return; } throw new Exception(name); }
    private static async Task RejectAsync(Func<Task> action, string name)
    { try { await action(); } catch (TranslationException) { count++; return; } throw new Exception(name); }
    private const string Json = """{"full_translation":"여정은 한 걸음에서 시작됩니다.","sentence_explanation":"begin with는 시작점을 나타냅니다.","vocabulary":[{"word":"journey","pronunciation":"/ˈdʒɜːrni/","meaning_src":"An act of travelling.","meaning_ko":"여정","examples":[{"en_or_jp":"Our journey starts today.","ko":"여정은 오늘 시작됩니다."}],"tip":"성장 과정도 나타냅니다."}]}""";
    private static ExplanationRequest Request(string text = "The journey begins with a single step.") => new(text, "en", new() { Provider = TranslationProvider.Gemini, Model = "exact-selected-model" });
    public static async Task Run()
    {
        static async Task Timed(string name, Func<Task> test)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { await test(); }
            finally { Console.WriteLine($"Study/{name}: {clock.Elapsed.TotalMilliseconds:F0}ms"); }
        }
        await Timed("Json", () => { Parsing(); return Task.CompletedTask; });
        await Timed("Ollama source vocabulary", () => { SourceVocabulary(); return Task.CompletedTask; });
        await Timed("MockHttp-timeout", Http); await Timed("Sessions", Sessions);
        await Timed("VoiceIndex", () => { Voice(); return Task.CompletedTask; });
        await Timed("Japanese-dictionary", () => { Japanese(); return Task.CompletedTask; });
        await Timed("Lifetime", Lifetime); await Timed("Settings", Settings);
        Console.WriteLine($"PASS: {count} assertions (Study). Mock HTTP, synthetic voice index, local IPA dictionary. No paid AI or user settings.");
    }
    private static void Parsing()
    {
        Check(StudyJson.Parse(Json).Vocabulary[0].Examples[0].Source == "Our journey starts today.", "English schema");
        var ja = StudyJson.Parse("""{"full_translation":"내일 만나요 😀","vocabulary":[{"word":"明日","pronunciation":"あした","meaning_ko":"내일"}]}""");
        Check(ja.FullTranslation!.EndsWith("😀") && ja.Vocabulary[0].Word == "明日", "Unicode Japanese emoji");
        var word = StudyJson.Parse("""{"full_translation":null,"sentence_explanation":null,"vocabulary":[{"word":"look up","meaning_en":"to search","meaning_ko":"찾아보다","examples":[{"en":"Look it up.","ko":"찾아보세요."}]}]}""");
        Check(word.FullTranslation is null && word.Vocabulary[0].MeaningSource == "to search" && word.Vocabulary[0].Examples[0].Source == "Look it up.", "Legacy fields/phrase/null");
        Check(StudyJson.Parse("""{"vocabulary":[{"word":"journey","meaning_ko":"여정"}]}""").Vocabulary[0].Examples.Count == 0, "Optional fields/word");
        Check(StudyJson.Parse("""{"full_translation":"안녕","vocabulary":[]}""").Vocabulary.Count == 0, "Empty vocabulary with useful text");
        foreach (var invalid in new[] { "", "not json", "[]", "{}", "{\"vocabulary\":null}", "{\"vocabulary\":[]}", "{\"full_translation\":false,\"vocabulary\":[]}", "{\"vocabulary\":[null]}", "{\"vocabulary\":[{\"word\":\"x\"}]}", "{\"vocabulary\":[{\"word\":\"x\",\"meaning_ko\":\"a\",\"examples\":{}}]}", "{\"vocabulary\":[],\"vocabulary\":[]}", Json.Replace("journey", new string('x', 201)), new string('x', StudyJson.MaxBytes + 1) })
            Reject(() => StudyJson.Parse(invalid), "Bad JSON accepted");
        Reject(() => ExplanationPrompt.ValidateInput("  "), "Empty input"); Reject(() => ExplanationPrompt.ValidateInput(new string('a', 2001)), "Long input");
        Check(ExplanationPrompt.ValidateInput("hello\r\n世界") == "hello\n世界", "Input normalization");
        Check(ExplanationPrompt.DetectLanguage("明日", "en") == "ja", "Direct Japanese input language");
        Check(ExplanationPrompt.DetectLanguage("journey", "ja") == "en", "Direct English input language");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = []; public List<string> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Bodies.Add(await request.Content!.ReadAsStringAsync(token)); Uris.Add(request.RequestUri!.ToString()); return await handle(request, token); }
    }
    private static void SourceVocabulary()
    {
        var english = StudyJson.Parse(Json);
        StudyJson.ValidateSourceLanguage(english, "en");
        var japanese = StudyJson.Parse("""{"vocabulary":[{"word":"学校","pronunciation":"がっこう (갓코오)","meaning_src":"学ぶ場所。","meaning_ko":"학교","examples":[{"en_or_jp":"学校に行きます。😀","ko":"학교에 갑니다."}],"tip":"한국어 설명은 그대로 유지"}]}""");
        StudyJson.ValidateSourceLanguage(japanese, "ja");
        Check(japanese.Vocabulary[0].Pronunciation.Contains("갓코오") && english.Vocabulary[0].MeaningKorean == "여정", "Korean pronunciation/meaning/tip must remain allowed");
        foreach (var invalid in new[] {
            english with { Vocabulary = [english.Vocabulary[0] with { Word = "군인들" }] },
            japanese with { Vocabulary = [japanese.Vocabulary[0] with { Word = "학교" }] },
            english with { Vocabulary = [english.Vocabulary[0] with { Word = "군인" }] },
            english with { Vocabulary = [english.Vocabulary[0] with { MeaningSource = "한 무리의 군인들" }] },
            english with { Vocabulary = [english.Vocabulary[0] with { Examples = [new("군인들은 정렬했다.", "군인들은 정렬했다.")] }] },
            english with { Vocabulary = [english.Vocabulary[0], english.Vocabulary[0] with { Word = "정렬" }] }
        }) Reject(() => StudyJson.ValidateSourceLanguage(invalid, "en"), "Korean source vocabulary accepted");
        Reject(() => StudyJson.ValidateSourceLanguage(japanese with { Vocabulary = [japanese.Vocabulary[0] with { MeaningSource = "배우는 장소" }] }, "ja"), "Korean definition for Japanese source accepted");
        var legacy = StudyJson.Parse("""{"vocabulary":[{"word":"journey","meaning_en":"여정","examples":[{"en":"여행을 시작했다.","ko":"여행을 시작했다."}]}]}""");
        Reject(() => StudyJson.ValidateSourceLanguage(legacy, "en"), "Legacy field aliases bypassed language validation");
        StudyJson.ValidateSourceLanguage(StudyJson.Parse("""{"vocabulary":[{"word":"look up","meaning_ko":"찾아보다"}]}"""), "en");
        StudyJson.ValidateSourceLanguage(StudyJson.Parse("""{"full_translation":"안녕","sentence_explanation":"인사하는 표현입니다.","vocabulary":[]}"""), "en");
        Reject(() => StudyJson.ValidateSourceLanguage(StudyJson.Parse("""{"full_translation":"안녕","vocabulary":[]}"""), "en"), "Translation-only response presented as study");
        Check(!ExplanationPrompt.Build("en").Contains("FIELD LANGUAGE RULES")
            && ExplanationPrompt.BuildOllama("en").Contains("FIELD LANGUAGE RULES")
            && ExplanationPrompt.Version == "study-v1" && ExplanationPrompt.OllamaVersion != ExplanationPrompt.Version,
            "Ollama prompt/cache changes leaked into other providers");
        Check(!ExplanationPrompt.Schema.GetProperty("properties").GetProperty("vocabulary").GetProperty("items").GetProperty("properties").GetProperty("word").TryGetProperty("pattern", out _),
            "Experimental grammar constraints must not change the shared schema");
    }
    private static HttpResponseMessage Response(TranslationProvider provider, string json = Json) => new(HttpStatusCode.OK) { Content = new StringContent(provider switch
    {
        TranslationProvider.OpenAI => JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = json } } } } }),
        TranslationProvider.Gemini => JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = json } } } } } }),
        _ => JsonSerializer.Serialize(new { done = true, message = new { content = json } })
    }, Encoding.UTF8, "application/json") };
    private static ITranslationService Transport(TranslationProvider provider, HttpClient client) => provider switch
    { TranslationProvider.OpenAI => new OpenAiTranslationService(client, "synthetic-key"), TranslationProvider.Gemini => new GeminiTranslationService(client, "synthetic-key"), _ => new OllamaTranslationService(client) };
    private static async Task Http()
    {
        foreach (var provider in new[] { TranslationProvider.Gemini, TranslationProvider.OpenAI, TranslationProvider.Ollama })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Response(provider))); using var client = new HttpClient(handler);
            var result = await new ExplanationService(Transport(provider, client)).ExplainAsync(Request() with { Options = Request().Options with { Provider = provider } }, default);
            Check(result.Study.Vocabulary[0].Word == "journey", "Provider parse " + provider);
            using var document = JsonDocument.Parse(handler.Bodies.Single()); var root = document.RootElement;
            Check(provider == TranslationProvider.Gemini ? handler.Uris[0].Contains("exact-selected-model") : root.GetProperty("model").GetString() == "exact-selected-model", "Exact model");
            Check(provider switch {
                TranslationProvider.OpenAI => root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean() && root.GetProperty("input").GetArrayLength() == 1,
                TranslationProvider.Gemini => root.GetProperty("generationConfig").GetProperty("responseMimeType").GetString() == "application/json" && root.GetProperty("contents").GetArrayLength() == 1,
                _ => root.GetProperty("format").GetProperty("properties").TryGetProperty("vocabulary", out _) && root.GetProperty("messages").GetArrayLength() == 2
            }, "JSON mode / no translation history");
            Check(!handler.Bodies[0].Contains("synthetic-key") && !handler.Uris[0].Contains("synthetic-key"), "Key in body/URL");
            var prompt = provider switch {
                TranslationProvider.OpenAI => root.GetProperty("instructions").GetString(),
                TranslationProvider.Gemini => root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString(),
                _ => root.GetProperty("messages")[0].GetProperty("content").GetString()
            };
            Check(prompt == (provider == TranslationProvider.Ollama ? ExplanationPrompt.BuildOllama("en") : ExplanationPrompt.Build("en")), "Provider prompt changed unexpectedly");
            if (provider == TranslationProvider.Ollama)
                Check(root.GetProperty("messages")[1].GetProperty("content").GetString() == Request().Text
                    && !prompt!.Contains("Translate the following"), "Ollama explanation received Korean translation or translation-only instructions");
        }
        // Same malformed semantic result: restrict this new guard to Ollama.
        foreach (var provider in new[] { TranslationProvider.Gemini, TranslationProvider.OpenAI, TranslationProvider.Ollama })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Response(provider, Json.Replace("journey", "군인들"))));
            using var client = new HttpClient(handler);
            var service = new ExplanationService(Transport(provider, client));
            var request = Request() with { Options = Request().Options with { Provider = provider } };
            if (provider == TranslationProvider.Ollama) {
                await RejectAsync(async () => await service.ExplainAsync(request, default), "Korean vocabulary displayed as successful Ollama analysis");
                Check(handler.Bodies.Count == 1, "Semantic failure automatically requested another inference");
            } else Check((await service.ExplainAsync(request, default)).Study.Vocabulary[0].Word == "군인들", "Existing other-provider response handling changed");
        }
        using (var handler = new Handler((_, _) => Task.FromResult(Response(TranslationProvider.Ollama, """{"vocabulary":[{"word":"学校","meaning_src":"学ぶ場所。","meaning_ko":"학교"}]}"""))))
        using (var client = new HttpClient(handler))
        {
            var result = await new ExplanationService(Transport(TranslationProvider.Ollama, client)).ExplainAsync(Request("学校に行きます。") with { Language = "ja", Options = new() { Provider = TranslationProvider.Ollama, Model = "user-alias" } }, default);
            Check(result.Study.Vocabulary[0].Word == "学校", "Japanese Ollama source vocabulary regressed");
        }
        using (var handler = new Handler((_, _) => Task.FromResult(Response(TranslationProvider.Ollama, Json.Replace("An act of travelling.", "여행하는 행위")))))
        using (var client = new HttpClient(handler))
            await RejectAsync(async () => await new ExplanationService(Transport(TranslationProvider.Ollama, client)).ExplainAsync(Request() with { Options = new() { Provider = TranslationProvider.Ollama, Model = "user-alias" } }, default), "English heading hid Korean source definition");
        foreach (var invalid in new[] { "{}", "not JSON", "", "{\"full_translation\":\"번역만 반환됨\",\"vocabulary\":[]}", Json.Replace("journey", "여정") })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Response(TranslationProvider.Ollama, invalid)));
            using var client = new HttpClient(handler);
            try {
                await new ExplanationService(Transport(TranslationProvider.Ollama, client)).ExplainAsync(Request() with { Options = new() { Provider = TranslationProvider.Ollama, Model = "arbitrary-user-model" } }, default);
                throw new Exception("Unusable explanation accepted");
            } catch (TranslationException ex) {
                // Empty content may be rejected in the shared provider transport
                // before study parsing. Keep that existing provider diagnostic.
                Check(invalid.Length == 0 || ex.Message.Contains("현재 모델의 응답이 문장 해설 검사를 통과하지 못했습니다")
                    && ex.Message.Contains("Ollama · arbitrary-user-model") && ex.Message.Contains("번역이 가능해도"), "Provider/model/limitation notice was not returned");
                Check(handler.Bodies.Count == 1, "Unusable explanation was retried automatically");
            }
        }
        using (var handler = new Handler((_, _) => throw new HttpRequestException("synthetic failure")))
        using (var client = new HttpClient(handler))
        {
            try { await new ExplanationService(Transport(TranslationProvider.Ollama, client)).ExplainAsync(Request() with { Options = new() { Provider = TranslationProvider.Ollama, Model = "user-alias" } }, default); throw new Exception("Network failure ignored"); }
            catch (TranslationException ex) { Check(!ex.Message.Contains("문장 해설 검사를 통과하지 못했습니다"), "Network error mislabeled as unsupported study model"); }
        }
        Reject(() => ExplanationService.Create(new() { Provider = TranslationProvider.Gemini, Model = "m" }, null), "Missing API key");
        Reject(() => ExplanationService.Create(new() { Provider = TranslationProvider.Gemini }, "fake"), "Missing model");
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            using var handler = new Handler((_, _) => { var response = new HttpResponseMessage(status) { Content = new StringContent("secret") }; response.Headers.RetryAfter = new(TimeSpan.Zero); return Task.FromResult(response); });
            using var client = new HttpClient(handler);
            await RejectAsync(async () => await new ExplanationService(Transport(TranslationProvider.OpenAI, client)).ExplainAsync(Request(), default), "Model/rejection ignored");
            Check(handler.Bodies.Count == (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable ? 2 : 1), "Study retries bounded");
        }
        using (var handler = new Handler((_, _) => throw new HttpRequestException("secret")))
        using (var client = new HttpClient(handler))
        { await RejectAsync(async () => await new ExplanationService(Transport(TranslationProvider.OpenAI, client)).ExplainAsync(Request(), default), "Network ignored"); Check(handler.Bodies.Count == 1, "Ambiguous retry"); }
        using (var handler = new Handler((_, _) => Task.FromResult(Response(TranslationProvider.OpenAI, "{}"))))
        using (var client = new HttpClient(handler))
        { await RejectAsync(async () => await new ExplanationService(Transport(TranslationProvider.OpenAI, client)).ExplainAsync(Request(), default), "Invalid structured response"); Check(handler.Bodies.Count == 1, "Parse retry"); }
        using (var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(TranslationProvider.OpenAI); }))
        using (var client = new HttpClient(handler))
        {
            using var cancel = new CancellationTokenSource(40);
            try { await new ExplanationService(Transport(TranslationProvider.OpenAI, client)).ExplainAsync(Request(), cancel.Token); throw new Exception("Cancel ignored"); } catch (OperationCanceledException) { count++; }
            await RejectAsync(async () => await new ExplanationService(Transport(TranslationProvider.OpenAI, client)).ExplainAsync(Request() with { Options = new() { Model = "m", TimeoutSeconds = 5 } }, default), "Timeout ignored");
        }
    }
    private sealed class DelayedService : IExplanationService
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<ExplanationResult> ExplainAsync(ExplanationRequest request, CancellationToken token)
        { Calls++; if (Calls == 1) { Started.SetResult(); await Release.Task; } return new(StudyJson.Parse(Json), TimeSpan.Zero); }
    }
    private static async Task Sessions()
    {
        int attempts = 0;
        using (var handler = new Handler((_, _) => Task.FromResult(Response(TranslationProvider.Ollama, ++attempts == 1 ? Json.Replace("journey", "여정") : Json))))
        using (var client = new HttpClient(handler))
        await using (var retrySession = new ExplanationSession())
        {
            var request = Request() with { Options = new() { Provider = TranslationProvider.Ollama, Model = "selected-alias" } };
            var retryService = new ExplanationService(Transport(TranslationProvider.Ollama, client));
            var retryProfile = Guid.NewGuid();
            await RejectAsync(async () => await retrySession.AnalyzeAsync(retryProfile, request, retryService), "Invalid vocabulary cached as success");
            Check(attempts == 1 && retrySession.CacheHits == 0, "Invalid vocabulary was retried or cached");
            var corrected = await retrySession.AnalyzeAsync(retryProfile, request, retryService);
            Check(corrected is { FromCache: false } && corrected.Study.Vocabulary[0].Word == "journey" && attempts == 2, "User retry after semantic error could not recover");
            Check((await retrySession.AnalyzeAsync(retryProfile, request, retryService))!.FromCache && attempts == 2, "Correct result no longer reused");
        }
        await using var session = new ExplanationSession(); var service = new DelayedService(); var profile = Guid.NewGuid();
        var first = session.AnalyzeAsync(profile, Request(), service); await service.Started.Task;
        Check(ReferenceEquals(first, session.AnalyzeAsync(profile, Request(), service)), "Repeated click not coalesced");
        var next = session.AnalyzeAsync(profile, Request("New sentence"), service); service.Release.SetResult();
        Check(await first is null && await next is not null && service.Calls == 2, "Late response not discarded");
        Check((await session.AnalyzeAsync(profile, Request("New sentence"), service))!.FromCache && service.Calls == 2, "Cache miss");
        await session.AnalyzeAsync(profile, Request("New sentence"), service, true); Check(service.Calls == 3, "Explicit retry skipped");
        await session.AnalyzeAsync(Guid.NewGuid(), Request("New sentence"), service); Check(service.Calls == 4, "Profile cache collision");
        await session.AnalyzeAsync(profile, Request("New sentence") with { Language = "ja" }, service); Check(service.Calls == 5, "Language cache collision");
        await session.AnalyzeAsync(profile, Request("New sentence") with { Options = new() { Model = "other" } }, service); Check(service.Calls == 6, "Model cache collision");
        var canceled = new DelayedService(); var pending = session.AnalyzeAsync(profile, Request("Cancel me"), canceled); await canceled.Started.Task; session.Cancel(); canceled.Release.SetResult(); Check(await pending is null, "Cancel stale response");
        await session.DisposeAsync(); await session.DisposeAsync();
    }
    private static void Voice()
    {
        var root = Path.Combine(Path.GetTempPath(), "KoTrans2.StudyTest", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        static byte[] Entry(string word, string path) => [..Encoding.Unicode.GetBytes(word), 0, 0, 0, ..Encoding.ASCII.GetBytes(path), 0];
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "audio")); File.WriteAllBytes(Path.Combine(root, "audio", "journey.mp3"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "word.idx"), [..Entry("Journey", "audio\\journey.mp3"), ..Entry("明日", "audio/missing.mp3"), ..Entry("escape", "../outside.mp3"), ..Entry("absolute", "C:\\outside.mp3")]);
            var index = VoiceIndex.Load(root); Check(index.Count == 2 && index.RejectedEntries == 2, "LNVI parse/invalid path");
            using (var file = index.OpenAudio(" journey ")) { Check(file.Length == 3, "Case insensitive lookup"); Reject(() => File.Open(Path.Combine(root, "audio", "journey.mp3"), FileMode.Open, FileAccess.Write).Dispose(), "Audio allowed writer"); }
            using (File.Open(Path.Combine(root, "audio", "journey.mp3"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { count++; }
            Reject(() => index.OpenAudio("absent"), "Missing word"); Reject(() => index.OpenAudio("明日"), "Missing file");
            File.WriteAllBytes(Path.Combine(root, "word.idx"), [1, 2, 3]); Reject(() => VoiceIndex.Load(root), "Corrupt index");
            File.WriteAllBytes(Path.Combine(root, "word.idx"), Entry("bad", "audio/../journey.mp3")); Reject(() => VoiceIndex.Load(root), "Dot traversal");
            File.WriteAllBytes(Path.Combine(root, "word.idx"), Entry("bad", "audio/journey.mp3:stream")); Reject(() => VoiceIndex.Load(root), "Alternate stream");
            // A real Windows junction tests path resolution rather than only string-prefix checks.
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); File.WriteAllBytes(Path.Combine(outside, "x.mp3"), [1]);
            var link = Path.Combine(root, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
                File.WriteAllBytes(Path.Combine(root, "word.idx"), Entry("link", "linked/x.mp3"));
                Reject(() => VoiceIndex.Load(root).OpenAudio("link"), "Symlink accepted");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) == 1314)
            { Console.WriteLine("SKIP: symlink creation requires local privilege; optional pre-created junction fixture is checked separately."); }
            finally { if (Directory.Exists(link)) Directory.Delete(link); }
        }
        finally { Directory.Delete(root, true); }
        var junctionFixture = Environment.GetEnvironmentVariable("KOTRANS_TEST_VOICE_JUNCTION");
        if (!string.IsNullOrEmpty(junctionFixture)) Reject(() => VoiceIndex.Load(junctionFixture).OpenAudio("junction"), "Junction escape accepted");
    }
    private static void Japanese()
    {
        using var service = new JapaneseReadingService();
        var result = service.Read("明日、学校に行きます。");
        Check(result.Tokens.Any(t => t.Surface == "学校" && t.Hiragana == "がっこう"), "Local IPA reading");
        Check(result.Text.Contains("学校（がっこう）") && result.Notice.Length > 0, "Furigana candidate");
        Check(service.Read("【太郎】 明日また会いましょう。").Text.StartsWith("【太郎】 "), "Speaker label changed");
        Check(JapaneseReadingService.ToHiragana("ガッコウ") == "がっこう", "Hiragana conversion");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); try { service.Read("明日", cancel.Token); throw new Exception("Canceled reading"); } catch (OperationCanceledException) { count++; }
    }
    private sealed class FakeOcr : IOcrService
    {
        public bool Disposed; public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static async Task Lifetime()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var ocr = new FakeOcr();
        var frame = new ImageFrame(2, 2, 8, new byte[16]);
        var pipeline = new TranslationPipeline(ocr, new MockTranslationService(), async token => { entered.SetResult(); await release.Task; return frame; }, new("en", "ADV", false), new(), new(), "test");
        pipeline.Start(false); var manual = pipeline.RequestManualAsync(); await entered.Task;
        var disposing = pipeline.DisposeAsync().AsTask(); Check(!disposing.IsCompleted, "Dispose missed manual capture"); release.SetResult();
        await manual; await disposing; await pipeline.DisposeAsync(); Check(!ocr.Disposed, "Shared OCR disposed");
        await pipeline.RequestManualAsync(); Check(!pipeline.IsRunning, "Disposed pipeline restarted");
        var alpha = new byte[16]; alpha[3] = 255; Check(StudyFrame.SamePixels(frame, new(2, 2, 8, alpha)), "RGB comparison alpha"); alpha[0] = 1; Check(!StudyFrame.SamePixels(frame, new(2, 2, 8, alpha)), "Changed source reused");
    }
    private static async Task Settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "KoTrans2.StudyTest", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings(); settings.Hotkeys.ToggleTranslation = "Alt+F11"; settings.Validate(); var old = JsonSerializer.Serialize(settings);
            using var doc = JsonDocument.Parse(old);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(old)!; legacy.AsObject().Remove("VoiceDirectory"); legacy.AsObject().Remove("StudyTopmost");
            foreach (var field in new[] { "Explain", "StudyScrollUp", "StudyScrollDown" }) legacy["Hotkeys"]!.AsObject().Remove(field);
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), legacy.ToJsonString());
            var store = new SettingsStore(root); var loaded = await store.LoadAsync();
            Check(loaded.Hotkeys.Explain == "Shift+F2" && loaded.Hotkeys.StudyScrollUp == "Shift+PageUp" && loaded.VoiceDirectory == "", "Phase3 settings compatibility");
            Check(loaded.Hotkeys.ToggleTranslation == "Alt+F11" && loaded.Hotkeys.StudyScrollDown == "Shift+PageDown", "New defaults destroyed valid Phase3 custom hotkeys");
            loaded.VoiceDirectory = root; loaded.StudyTopmost = false; loaded.Hotkeys.Explain = "Ctrl+Alt+F8"; await store.SaveAsync(loaded);
            Check((await store.LoadAsync()).Hotkeys.Explain == "Ctrl+Alt+F8" && !(await store.LoadAsync()).StudyTopmost, "Study preferences roundtrip");
        }
        finally { Directory.Delete(root, true); }
    }
}
