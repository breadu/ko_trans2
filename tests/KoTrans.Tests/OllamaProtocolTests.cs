using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;
internal static class OllamaProtocolTests
{
    private sealed class Handler(Func<CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls; public string Body="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            Calls++;Body=await request.Content!.ReadAsStringAsync(token); return response(token);
        }
    }
    private sealed class CancelAfterClassification(HttpClient client, CancellationTokenSource caller) : HttpTranslationService(client)
    {
        protected override HttpRequestMessage CreateRequest(TranslationRequest request) => new(HttpMethod.Post,"http://synthetic.test/api/chat") {Content=new StringContent("{}")};
        protected override string Parse(JsonElement json) => throw new Exception("Unexpected success");
        protected override Task<TranslationException> ResponseErrorAsync(HttpResponseMessage response,TranslationRequest request,CancellationToken token) {
            caller.Cancel();return Task.FromResult(new TranslationException("Synthetic HTTP500 after caller cancellation"));
        }
    }
    private static HttpResponseMessage Response(string body,HttpStatusCode status=HttpStatusCode.OK) => new(status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private static TranslationOptions Options(OllamaRequestFormat format,string id="renamed-local-model:custom") {
        var options=new TranslationOptions{Provider=TranslationProvider.Ollama,Model=id};
        options.OllamaModels=options.OllamaModels.SetItem(id,new(format,120));return options;
    }
    private sealed class CountingStudy:IExplanationService {
        public int Calls;
        public Task<ExplanationResult> ExplainAsync(ExplanationRequest request,CancellationToken token){Calls++;return Task.FromResult(new ExplanationResult(new("translation","grammar",[]),TimeSpan.Zero,0));}
    }
    internal static async Task Run() {
        int count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        var request=new TranslationRequest("Hello.","PRIVATE-PROMPT",[new("PRIVATE-HISTORY","old")],Options(OllamaRequestFormat.TranslateGemma),SourceLanguage:"en");
        foreach(string language in new[]{"en","ja"}) {
            using var handler=new Handler(_=>Response("{\"done\":true,\"message\":{\"content\":\"번역\"}}"));using var client=new HttpClient(handler);
            var result=await new OllamaTranslationService(client).TranslateAsync(request with{SourceLanguage=language},default);
            using var json=JsonDocument.Parse(handler.Body);var messages=json.RootElement.GetProperty("messages");var prompt=messages[0].GetProperty("content").GetString()!;
            Check(result.Text=="번역" && messages.GetArrayLength()==1 && messages[0].GetProperty("role").GetString()=="user","Specialized formatter did not produce one user turn");
            Check(prompt.Contains(language=="en"?"English (en)":"Japanese (ja)") && prompt.Contains("Korean (ko)") && prompt.EndsWith("\n\n\nHello.") && !handler.Body.Contains("PRIVATE-"),"Specialized prompt leaked chat instructions/history or lost language separators");
            Check(json.RootElement.GetProperty("model").GetString()==request.Options.Model,"Formatter depended on/changed the model ID");
            Check(json.RootElement.GetProperty("keep_alive").GetString()=="10m","Model retention was not applied to avoid reloads");
        }
        // Exercise ordinary translation (not structured study) with a dictionary and
        // a deliberately poisoned prior answer. Only original context may reach Ollama.
        foreach (var format in Enum.GetValues<OllamaRequestFormat>()) {
            using var handler = new Handler(_ => Response("{\"done\":true,\"message\":{\"content\":\"사토가 왔다.\\n문을 열었다.\"}}"));
            using var client = new HttpClient(handler);
            CharacterEntry[] characters = [new("佐藤美咲", "사토 미사키", "synthetic biography"), new("田中太郎", "다나카 타로", "unused biography")];
            const string source = "佐藤が来た。\n\n扉を開けた。";
            string system = TranslationPrompt.Build(characters) + "\n" + TranslationParagraphs.Instruction(2);
            var scoped = new TranslationRequest(source, system, [new("背景の原文。", "POISONED-OLD-TRANSLATION")], Options(format),
                SourceLanguage: "ja", Characters: characters, ParagraphCount: 2);
            var result = await new OllamaTranslationService(client).TranslateAsync(scoped, default);
            using var json = JsonDocument.Parse(handler.Body); var messages = json.RootElement.GetProperty("messages");
            Check(json.RootElement.TryGetProperty("think", out var thinking) && thinking.ValueKind == JsonValueKind.False,
                "Ollama translation did not disable thinking with a top-level boolean for this request format/model alias");
            Check(handler.Calls == 1 && messages[0].GetProperty("content").GetString()!.Contains("exactly 2 translated paragraphs"),
                "Ollama batch lost paragraph instructions or issued multiple requests");
            Check(result.Text == "사토가 왔다.\n문을 열었다.", "Ollama truncated a legitimate multiline translation");
            Check(!handler.Body.Contains("POISONED-OLD-TRANSLATION") && !messages.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant"),
                "Old generated answers entered the Ollama translation request");
            if (format == OllamaRequestFormat.Chat) {
                string instructions = messages[0].GetProperty("content").GetString()!;
                using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                Check(messages.GetArrayLength() == 2 && instructions.StartsWith(system + "\n") && instructions.Contains("Translate only current_source into Korean"),
                    "Ollama Chat dropped the dictionary or final scope reminder");
                Check(input.RootElement.GetProperty("current_source").GetString() == source
                    && input.RootElement.GetProperty("previous_source_context")[0].GetString() == "背景の原文。", "Ollama lost original context or current-source boundaries");
            } else if (format == OllamaRequestFormat.TranslateGemma) {
                string input = messages[0].GetProperty("content").GetString()!;
                Check(messages.GetArrayLength() == 1 && input.EndsWith("\n\n\n" + source) && !input.Contains("背景の原文") && !input.Contains("synthetic biography"),
                    "TranslateGemma's source-only protocol received unsupported chat context or dictionary instructions");
            } else {
                string input = messages[0].GetProperty("content").GetString()!;
                Check(messages.GetArrayLength() == 1 && messages[0].GetProperty("role").GetString() == "user"
                    && input.EndsWith("\n\n" + source) && input.Contains("背景の原文。\n\n")
                    && input.IndexOf("exactly 2 translated paragraphs", StringComparison.Ordinal) < input.IndexOf("参考上面的信息", StringComparison.Ordinal),
                    "HY-MT did not keep current text separate from original-only context in a single user turn");
                Check(input.Contains("\"佐藤\" translates to \"사토\"") && !input.Contains("사토 미사키") && !input.Contains("다나카")
                    && !input.Contains("biography"), "HY-MT lost surname mapping or included unrelated full names/biographies");
                Check(!new[] { "previous_source_context", "current_name_translations", "source_name", "current_source", "<|", "<｜" }.Any(input.Contains),
                    "HY-MT received chat JSON fields or duplicate native template tokens");
            }
        }
        using(var handler=new Handler(_=>Response("{\"done\":true,\"message\":{\"content\":\"번역\"}}")))using(var client=new HttpClient(handler)) {
            var service=new OllamaTranslationService(client);
            var chat=request with{Options=Options(OllamaRequestFormat.Chat),OutputSchema=JsonSerializer.SerializeToElement(new{type="object"})};
            await service.TranslateAsync(chat,default);using var json=JsonDocument.Parse(handler.Body);
            Check(json.RootElement.GetProperty("messages").GetArrayLength()==4 && json.RootElement.TryGetProperty("format",out _),"Chat adapter lost history/structured output");
            Check(!json.RootElement.TryGetProperty("think",out _),"Translation thinking policy changed structured study requests");
            int before=handler.Calls;
            foreach (var format in new[] { OllamaRequestFormat.TranslateGemma, OllamaRequestFormat.HyMt })
                try{await service.TranslateAsync(request with{Options=Options(format),OutputSchema=chat.OutputSchema},default);throw new Exception("Study accepted");}catch(TranslationException ex){Check(ex.Message.Contains("번역 전용")&&handler.Calls==before,"Unsupported study did not fail before HTTP");}
            try{await service.TranslateAsync(request with{SourceLanguage=null},default);throw new Exception("Missing language accepted");}catch(TranslationException ex){Check(ex.Message.Contains("원문 언어")&&handler.Calls==before,"Missing language transmitted");}
        }
        foreach(var item in new[]{(HttpStatusCode.NotFound,"model not found","모델"),(HttpStatusCode.BadRequest,"unsupported format","요청 형식"),(HttpStatusCode.InternalServerError,"not enough memory PRIVATE-SOURCE","메모리 부족"),(HttpStatusCode.InternalServerError,"model runner has stopped PRIVATE-SOURCE","실행기"),(HttpStatusCode.InternalServerError,"Post http://127.0.0.1:9999/tokenize: wsarecv: An existing connection was forcibly closed by the remote host. PRIVATE-SOURCE","연결이 끊겼"),(HttpStatusCode.InternalServerError,"context canceled PRIVATE-SOURCE","서버 내부 작업이 취소"),(HttpStatusCode.InternalServerError,"PRIVATE-SOURCE","HTTP 500")}) {
            using var handler=new Handler(_=>Response(JsonSerializer.Serialize(new{error=item.Item2}),item.Item1));using var client=new HttpClient(handler);
            try{await new OllamaTranslationService(client).TranslateAsync(request,default);throw new Exception("HTTP failure accepted");}catch(TranslationException ex){Check(ex.Message.Contains(item.Item3)&&!ex.Message.Contains("PRIVATE-")&&handler.Calls==1,"Safe Ollama failure diagnosis/retry contract");}
        }
        await PreparationRecovery(Check);
        await HyMtContextScope(Check);
        using(var handler=new Handler(_=>throw new TaskCanceledException()))using(var client=new HttpClient(handler)) {
            try{await new OllamaTranslationService(client).TranslateAsync(request,default);throw new Exception("Timeout accepted");}catch(TranslationException ex){Check(ex.Message.Contains("120초")&&ex.Message.Contains("확정할 수 없습니다")&&ex.Message.Contains("GPU")&&handler.Calls==1,"Timeout lacks limits/actions or retries");}
        }
        using(var handler=new Handler(_=>throw new HttpRequestException("PRIVATE-SERVER")))using(var client=new HttpClient(handler)) {
            try{await new OllamaTranslationService(client).TranslateAsync(request,default);throw new Exception("Connection failure accepted");}catch(TranslationException ex){Check(ex.Message.Contains("서버")&&ex.Message.Contains("포트")&&!ex.Message.Contains("PRIVATE-"),"Connection diagnosis leaked raw details");}
        }
        int retryCalls=0;
        using(var handler=new Handler(_=>{if(++retryCalls==2)throw new TaskCanceledException();var response=Response("{}",HttpStatusCode.ServiceUnavailable);response.Headers.RetryAfter=new(TimeSpan.Zero);return response;}))using(var client=new HttpClient(handler)) {
            try{await new OllamaTranslationService(client).TranslateAsync(request,default);throw new Exception("Retry timeout accepted");}catch(TranslationException ex){Check(ex.Message.Contains("모델 실행 응답 대기 중")&&handler.Calls==2,"Timeout phase was inherited from a previous rejected HTTP attempt");}
        }
        using(var caller=new CancellationTokenSource())using(var handler=new Handler(_=>Response("{}",HttpStatusCode.InternalServerError)))using(var client=new HttpClient(handler)) {
            try{await new CancelAfterClassification(client,caller).TranslateAsync(request,caller.Token);throw new Exception("Canceled error was accepted");}
            catch(OperationCanceledException){Check(handler.Calls==1,"Canceled request was retried");}
            catch(TranslationException){throw new Exception("Caller cancellation lost to an HTTP500 classification");}
        }
        var options=Options(OllamaRequestFormat.TranslateGemma);var frozen=options with{};
        options.OllamaModels=options.OllamaModels.SetItem(options.Model,new(OllamaRequestFormat.Chat,10));
        Check(frozen.SettingsForOllama().Format==OllamaRequestFormat.TranslateGemma&&frozen.RequestTimeoutSeconds==120&&options.RequestTimeoutSeconds==10,"Mutable per-model settings contaminated a request snapshot");
        var hyMtOptions = Options(OllamaRequestFormat.HyMt);
        hyMtOptions.OllamaModels = hyMtOptions.OllamaModels.SetItem("other-model", new(OllamaRequestFormat.Chat));
        var restored = JsonSerializer.Deserialize<TranslationOptions>(JsonSerializer.Serialize(hyMtOptions))!;
        restored.Validate();
        Check(restored.SettingsForOllama().Format == OllamaRequestFormat.HyMt
            && restored.OllamaModels["other-model"].Format == OllamaRequestFormat.Chat
            && (int)OllamaRequestFormat.Chat == 0 && (int)OllamaRequestFormat.TranslateGemma == 1,
            "HY-MT settings did not round-trip independently or changed existing persisted format values");
        var contexts=new TranslationContexts();var profile=Guid.NewGuid();var ocr=new OcrOptions("en","ADV",false);
        contexts.Get(profile,ocr,options,"prompt").Accept("source","translation",false);
        Check(contexts.Get(profile,ocr,frozen,"prompt").History.Count==0,"Ollama format change reused incompatible history");
        using(var canceled=new CancellationTokenSource())using(var handler=new Handler(t=>{t.ThrowIfCancellationRequested();return Response("{}");}))using(var client=new HttpClient(handler)) {
            canceled.Cancel();try{await new OllamaTranslationService(client).TranslateAsync(request,canceled.Token);throw new Exception("Cancellation accepted");}catch(OperationCanceledException){Check(handler.Calls==0,"Canceled request reached HTTP");}
        }
        await using(var session=new ExplanationSession()) {
            var study=new CountingStudy();
            await session.AnalyzeAsync(profile,new("word","en",options),study);
            await session.AnalyzeAsync(profile,new("word","en",frozen),study);
            Check(study.Calls==2,"Study cache ignored a changed Ollama request protocol");
        }
        Console.WriteLine($"PASS: {count} Ollama protocol/model-profile/error assertions; Mock HTTP only.");
    }

    private static async Task HyMtContextScope(Action<bool, string> check)
    {
        var options = Options(OllamaRequestFormat.HyMt);
        const string source = "「ここで待っていてくれ」";
        var history = new[] { new ConversationTurn("もっと古い文。", "OLD ANSWER"),
            new ConversationTurn("近くの扉が開いた。", "OLD ANSWER"), new ConversationTurn(source + "彼は階段を上った。", "OLD ANSWER") };
        using var handler = new Handler(_ => Response("{\"done\":true,\"message\":{\"content\":\"정상 대사\\n정상 두 번째 줄\"}}"));
        using var client = new HttpClient(handler);
        var request = new TranslationRequest(source, "CLOUD PROMPT", history, options, SourceLanguage: "ja", PreserveHistory: true);
        var service = new OllamaTranslationService(client);
        var result = await service.TranslateAsync(request, default);
        string Content() { using var json = JsonDocument.Parse(handler.Body); return json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!; }
        string input = Content();
        check(input.Contains("近くの扉が開いた。") && !input.Contains("もっと古い") && !input.Contains("階段")
            && input.IndexOf(source, StringComparison.Ordinal) == input.LastIndexOf(source, StringComparison.Ordinal), "HY-MT resent the current NVL block as prior context");
        check(!input.Contains("OLD ANSWER") && !input.Contains("CLOUD PROMPT") && !input.Contains("[Source Text]")
            && !input.Contains("[Background Information]"), "HY-MT reused unsuitable headings or generated answers");
        check(result.Text == "정상 대사\n정상 두 번째 줄" && handler.Calls == 1, "HY-MT dropped legitimate output or issued extra generation requests");
        check(history[2].Source.EndsWith("階段を上った。") && history.Length == 3, "Request context filtering mutated session history");

        await service.TranslateAsync(request with { History = [new(source.Replace("で", "で\n"), "")] }, default);
        check(!Content().Contains("参考上面的信息") && Content().EndsWith(source), "Visual wrapping defeated duplicate-context isolation");
        string longPrior = "OMIT_OLD_PAGE" + string.Concat(Enumerable.Repeat("古い文章。", 200)) + "最近の会話。";
        await service.TranslateAsync(request with { History = [new(longPrior, "")] }, default);
        input = Content();
        int task = input.IndexOf("参考上面的信息", StringComparison.Ordinal);
        check(task <= 514 && input.Contains("最近の会話。") && !input.Contains("OMIT_OLD_PAGE") && input.EndsWith(source), "HY-MT history budget truncated the target or retained a full NVL page");
        string unicode = string.Concat(Enumerable.Repeat("😀e\u0301", 200));
        await service.TranslateAsync(request with { History = [new(unicode, "")] }, default);
        input = Content();
        check(input.StartsWith("😀", StringComparison.Ordinal) || input.StartsWith("e\u0301", StringComparison.Ordinal), "HY-MT split a context text element");
        _ = new UTF8Encoding(false, true).GetBytes(input);
        await service.TranslateAsync(request with { History = [new("<|extra_0|>", ""),new("安全な背景。", "")] }, default);
        check(Content().Contains("安全な背景。") && !Content().Contains("<|"), "Native template tokens entered context");
        await service.TranslateAsync(request with { Text = "一段目。\n\n二段目。", ParagraphCount = 2, History = [] }, default);
        input = Content();
        check(input.Contains("exactly 2 translated paragraphs") && input.EndsWith("：\n\n一段目。\n\n二段目。")
            && input.IndexOf("exactly 2", StringComparison.Ordinal) < input.IndexOf("参考上面的信息", StringComparison.Ordinal), "Paragraph instructions became part of the source segment");
        await service.TranslateAsync(request with { Text = source + "後半。", History = [new(source, "")] }, default);
        check(!Content().Contains("参考上面的信息"), "A full-page request included its own prefix as context");
    }

    private static async Task PreparationRecovery(Action<bool, string> check)
    {
        const string reset = "Post \"http://127.0.0.1:9999/tokenize\": read tcp: wsarecv: An existing connection was forcibly closed by the remote host.";
        var request = new TranslationRequest("synthetic source", "prompt", [], Options(OllamaRequestFormat.HyMt));
        int count = 0;
        using (var handler = new Handler(_ => ++count == 1 ? Response(JsonSerializer.Serialize(new { error = reset }), HttpStatusCode.InternalServerError)
            : Response("{\"done\":true,\"message\":{\"content\":\"번역 완료\"}}")))
        using (var client = new HttpClient(handler)) {
            var service = new OllamaTranslationService(client);
            var result = await service.TranslateAsync(request, default);
            check(result.Text == "번역 완료" && result.HttpAttempts == 2 && service.HttpAttempts == 2 && handler.Calls == 2,
                "Local pre-generation tokenization failure did not recover with exactly one counted retry");
        }
        using (var handler = new Handler(_ => Response(JsonSerializer.Serialize(new { error = reset }), HttpStatusCode.InternalServerError)))
        using (var client = new HttpClient(handler)) {
            try { await new OllamaTranslationService(client).TranslateAsync(request, default); throw new Exception("Persistent failure accepted"); }
            catch (OllamaServerException ex) {
                check(handler.Calls == 2 && ex.Status == 500 && ex.Kind == OllamaFailureKind.InternalConnectionClosed
                    && ex.Stage == OllamaFailureStage.Tokenize && !ex.Message.Contains("9999"), "Persistent failure retry bound/classification/privacy broken");
            }
        }
        foreach (string error in new[] { reset.Replace("/tokenize", "/completion"), reset.Replace("/tokenize", "/apply-template"),
            reset.Replace("/tokenize", "/health"), reset.Replace("Post", "Get"), reset.Replace("127.0.0.1", "example.test"),
            reset.Replace("/tokenize", "/tokenize?private=secret"), "mentioned endpoint " + reset,
            "model runner has unexpectedly stopped, this may be due to resource limitations or an internal error", "out of memory", reset + " out of memory" }) {
            using var handler = new Handler(_ => Response(JsonSerializer.Serialize(new { error }), HttpStatusCode.InternalServerError));
            using var client = new HttpClient(handler);
            try { await new OllamaTranslationService(client).TranslateAsync(request, default); throw new Exception("Failure accepted"); }
            catch (OllamaServerException) { check(handler.Calls == 1, "Uncertain/post-generation/server/memory failure was retried: " + error); }
        }
        foreach (string address in new[] { "http://remote.test:11434", "http://localhost:11434/proxy", "https://localhost:11434" }) {
            using var handler = new Handler(_ => Response(JsonSerializer.Serialize(new { error = reset }), HttpStatusCode.InternalServerError));
            using var client = new HttpClient(handler);
            try { await new OllamaTranslationService(client).TranslateAsync(request with { Options = request.Options with { OllamaAddress = address } }, default); throw new Exception("Failure accepted"); }
            catch (OllamaServerException) { check(handler.Calls == 1, "Recovery escaped direct local HTTP scope"); }
        }
        using (var canceled = new CancellationTokenSource())
        using (var handler = new Handler(_ => { canceled.CancelAfter(30); return Response(JsonSerializer.Serialize(new { error = reset }), HttpStatusCode.InternalServerError); }))
        using (var client = new HttpClient(handler)) {
            try { await new OllamaTranslationService(client).TranslateAsync(request, canceled.Token); throw new Exception("Cancellation lost"); }
            catch (OperationCanceledException) { check(handler.Calls == 1, "Canceled preparation backoff sent another request"); }
        }
        check(OllamaFailure.Classify(500, "model runner stopped; check GPU memory and driver").Kind == OllamaFailureKind.RunnerUnavailable,
            "A generic recommendation to check memory was reported as confirmed OOM");
    }
}
