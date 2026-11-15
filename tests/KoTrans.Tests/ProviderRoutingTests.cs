using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;
internal static class ProviderRoutingTests
{
    private static int count;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
    internal static async Task Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.ProviderRouting",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {await Routes(dir);await OcrFailures(dir);OverlayMessages();await OllamaOverlayErrors();await PipelineFailureCauses();}finally{Directory.Delete(dir,true);}
        Console.WriteLine($"PASS: {count} provider vault/factory/request + OCR-before-API assertions. Synthetic credentials and Mock HTTP only.");
    }
    private static void OverlayMessages()
    {
        string[] details = [
            "캐릭터 사전 파일을 읽을 수 없습니다. 파일 위치를 확인하세요.",
            "로컬 번역 시간이 초과되었습니다. 원문 영역을 줄인 뒤 다시 시도해 주세요.",
            "로컬 번역이 출력 한도에 도달했습니다.",
            "이 PC에서 로컬 번역을 준비하지 못했습니다. 메모리와 그래픽 드라이버 상태를 확인한 뒤 다시 시도해 주세요.",
            "Hy-MT2 7B의 CPU 실행에는 가용 RAM 6GiB 이상이 필요합니다.",
            "DeepL API 번역 문자 사용 한도를 초과했습니다.",
            "Gemini 응답이 중단되었습니다.",
            "Ollama 서버 오류 (HTTP 500). 원인 불명",
            "Ollama 120초 제한 초과 · 모델 실행 응답 대기 중"
        ];
        foreach (var provider in Enum.GetValues<TranslationProvider>()) {
            foreach (string detail in details)
                Check(TranslationPipeline.TranslationOverlayMessage(provider, new TranslationException(detail)) == detail,
                    "Selected provider replaced a known cause: " + provider);
            foreach (var raw in new Exception[] { new IOException("private/path/model.onnx"), new Exception("private-token; 메모리가 부족합니다."),
                new UnauthorizedAccessException("private/key"), new FileNotFoundException("private/model.onnx") }) {
                string notice = TranslationPipeline.TranslationOverlayMessage(provider, raw);
                Check(!notice.Contains("private") && !notice.Contains("서버") && !notice.Contains("메모리") && !notice.Contains("모델"),
                    "Unclassified error leaked details or guessed a cause: " + provider);
            }
        }
        Check(TranslationPipeline.TranslationOverlayMessage(TranslationProvider.HyMtLocal, new OutOfMemoryException())
            == "로컬 번역에 필요한 메모리가 부족합니다.", "Confirmed out-of-memory cause was lost");
    }
    private static async Task OllamaOverlayErrors()
    {
        var cases = new (HttpStatusCode Code, string Error, Exception? Failure, string Detail)[] {
            (HttpStatusCode.NotFound, "not found", null, "Ollama 서버 주소 또는 모델을 찾을 수 없습니다."),
            (HttpStatusCode.BadRequest, "bad request", null, "Ollama가 요청 형식을 거부했습니다."),
            (HttpStatusCode.InternalServerError, "out of memory", null, "Ollama가 메모리 부족 오류를 반환했습니다."),
            (HttpStatusCode.InternalServerError, "model runner stopped", null, "Ollama 모델 실행기의 응답을 받지 못했습니다."),
            (HttpStatusCode.InternalServerError, "127.0.0.1 connection reset", null, "Ollama 서버와 모델 실행기 사이의 연결"),
            (HttpStatusCode.InternalServerError, "unknown failure", null, "Ollama 서버 오류 (HTTP 500)."),
            (HttpStatusCode.OK, "", new TaskCanceledException(), "Ollama 120초 제한 초과"),
            (HttpStatusCode.OK, "", new HttpRequestException(), "Ollama 서버에 연결하지 못")
        };
        foreach (var item in cases) {
            using var handler = new ErrorHandler(item.Code, item.Error, item.Failure);
            using var client = new HttpClient(handler);
            var service = new OllamaTranslationService(client);
            var options = new TranslationOptions { Provider = TranslationProvider.Ollama, Model = "synthetic-chat" };
            TranslationException? failure = null;
            try { await service.TranslateAsync(new("synthetic source", "prompt", [], options), default); }
            catch (TranslationException ex) { failure = ex; }
            Check(failure is not null && failure.Message.StartsWith(item.Detail, StringComparison.Ordinal), "Adapter detail changed: " + item.Detail);
            string detail = failure!.Message;
            Check(TranslationPipeline.TranslationOverlayMessage(TranslationProvider.Ollama, failure) == detail,
                "Ollama overlay lost the adapter classification: " + item.Detail);
            Check(failure.Message == detail && handler.Calls == 1 && service.HttpAttempts == 1,
                "Overlay classification modified detailed diagnostics or repeated a request");
        }
    }
    private sealed class ErrorHandler(HttpStatusCode code, string error, Exception? failure) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Calls++;
            return failure is not null ? Task.FromException<HttpResponseMessage>(failure)
                : Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(JsonSerializer.Serialize(new { error }), Encoding.UTF8, "application/json") });
        }
    }
    private static async Task Routes(string dir)
    {
        var vault=new ApiKeyStore(dir);foreach(var descriptor in TranslationProviderCatalog.All)foreach(var field in descriptor.Credentials)vault.SaveCredential(descriptor.Provider,field.Id,"synthetic-"+descriptor.Provider+"-key");
        var options=new TranslationOptions{Provider=TranslationProvider.Gemini,Model="gemini-3.5-flash-lite",OpenAIModel="gpt-6-luna",OllamaModel="synthetic-chat",DeepLPlan=DeepLApiPlan.Pro};
        foreach(var provider in new[]{TranslationProvider.DeepL,TranslationProvider.OpenAI,TranslationProvider.Gemini,TranslationProvider.Ollama,TranslationProvider.DeepL,TranslationProvider.OpenAI}) {
            options.SwitchProvider(provider);using var handler=new Handler();using var client=new HttpClient(handler);
            var service=TranslationServices.CreateUsingVault(options,vault,client);
            var result=await service.TranslateAsync(new("source text","system prompt",[new("older source","older translation")],options,SourceLanguage:"en"),default);
            Check(result.Text=="mock translation" && handler.Calls==1,"Factory transport did not complete: "+provider);
            using var body=JsonDocument.Parse(handler.Body);var root=body.RootElement;
            string expectedModel=provider switch{TranslationProvider.OpenAI=>"gpt-6-luna",TranslationProvider.Gemini=>"gemini-3.5-flash-lite",TranslationProvider.Ollama=>"synthetic-chat",_=>""};
            Check(options.Model==expectedModel,"Provider switch lost selected model: "+provider);
            Check(handler.Url.Contains(provider switch{TranslationProvider.OpenAI=>"api.openai.com/v1/responses",TranslationProvider.Gemini=>"gemini-3.5-flash-lite:generateContent",TranslationProvider.Ollama=>"localhost:11434/api/chat",_=>"api.deepl.com/v2/translate"}),"Factory selected wrong endpoint: "+provider);
            Check(handler.Auth==(provider switch{TranslationProvider.OpenAI=>"Bearer synthetic-OpenAI-key",TranslationProvider.Gemini=>"synthetic-Gemini-key",TranslationProvider.DeepL=>"DeepL-Auth-Key synthetic-DeepL-key",_=>""}),"Vault credentials crossed provider boundaries: "+provider);
            if(provider==TranslationProvider.OpenAI)Check(root.GetProperty("model").GetString()=="gpt-6-luna" && root.GetProperty("reasoning").GetProperty("effort").GetString()=="none" && !root.GetProperty("store").GetBoolean(),"OpenAI request regression");
            if(provider==TranslationProvider.Gemini)Check(handler.Body.Contains("older source")&&!handler.Body.Contains("older translation"),"Gemini source-only context regression");
            if(provider==TranslationProvider.Ollama)Check(root.GetProperty("model").GetString()=="synthetic-chat"&&root.TryGetProperty("messages",out _),"Ollama request regression");
            if(provider==TranslationProvider.DeepL)Check(!root.TryGetProperty("model",out _)&&root.GetProperty("source_lang").GetString()=="EN"&&root.GetProperty("target_lang").GetString()=="KO","DeepL request regression");
        }
        Check(File.Exists(Path.Combine(dir,"OpenAI.key"))&&vault.Read(TranslationProvider.OpenAI)=="synthetic-OpenAI-key","Legacy OpenAI key file compatibility lost");
        foreach(var provider in new[]{TranslationProvider.OpenAI,TranslationProvider.Gemini}) {
            options.SwitchProvider(provider);using var handler=new Handler();using var client=new HttpClient(handler);var partial=new List<string>();
            var result=await TranslationServices.CreateUsingVault(options,vault,client).TranslateAsync(new("source","prompt",[],options,Progress:partial.Add,SourceLanguage:"en"),default);
            Check(result.Text=="mock translation"&&partial.Count>0&&handler.Calls==1,"Factory streaming failed: "+provider);
        }
        vault.Delete(TranslationProvider.DeepL);options.SwitchProvider(TranslationProvider.OpenAI);
        using(var handler=new Handler())using(var client=new HttpClient(handler)) {
            var service=TranslationServices.CreateUsingVault(options,vault,client);
            Check((await service.TranslateAsync(new("source","prompt",[],options),default)).Text=="mock translation","Deleting DeepL credentials disabled OpenAI");
        }
        // No-key provider must not decrypt even a broken unrelated key file.
        File.WriteAllBytes(Path.Combine(dir,"OpenAI.key"),[1,2,3]);options.SwitchProvider(TranslationProvider.Ollama);
        using(var handler=new Handler())using(var client=new HttpClient(handler))
            Check((await TranslationServices.CreateUsingVault(options,vault,client).TranslateAsync(new("source","prompt",[],options),default)).Text=="mock translation","Ollama accessed unrelated credentials");
    }
    private static async Task OcrFailures(string dir)
    {
        foreach(var provider in TranslationProviderCatalog.All.Select(d=>d.Provider)) {
            var options=new TranslationOptions{Provider=provider,Model=TranslationProviderCatalog.Get(provider).RequiresModel?"synthetic-model":""};
            var error=new TaskCompletionSource<PipelineUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);var ai=new NeverCalled();
            await using var ocr=new OcrServiceFactory(OcrEngineCatalog.Default,Path.Combine(dir,"missing-models")).Create("paddle");
            await using var pipeline=new TranslationPipeline(ocr,ai,_=>Task.FromResult(new ImageFrame(20,20,80,new byte[1600])),new("en","ADV",false),options,new(),"prompt");
            pipeline.Updated+=u=>{if(u.State==PipelineState.Error)error.TrySetResult(u);};pipeline.Start(false);await pipeline.RequestManualAsync();
            var result=await error.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result.Message.Contains("OCR 모델")&&result.Message.Contains("번역 API로 전송되지")&&!result.Message.Contains(dir),"Missing model failed opaquely or leaked path: "+provider);
            Check(ai.Calls==0 && result.TranslationRequests==0 && result.HttpAttempts==0,"Missing OCR reached translation API: "+provider);
            Check(result.FailureArea == PipelineStatusArea.Ocr && result.OverlayMessage == result.Message,
                "OCR initialization error lost its separate short overlay notice: " + provider);
        }
    }
    private sealed class NeverCalled:ITranslationService
    {public int Calls;public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken token){Calls++;throw new Exception("Unexpected AI request");}}
    private static async Task PipelineFailureCauses()
    {
        var cases = new (PipelineStatusArea Area, Exception Error, bool Known)[] {
            (PipelineStatusArea.Ocr, new OcrInitializationException("선택한 OCR 모델은 세로쓰기를 지원하지 않습니다."), true),
            (PipelineStatusArea.Ocr, new OcrInitializationException("OCR 부동소수점 실행 상태를 복구할 수 없습니다."), true),
            (PipelineStatusArea.Ocr, new FileNotFoundException("private/non-model-file"), false),
            (PipelineStatusArea.Ocr, new UnauthorizedAccessException("private/non-model-folder"), false),
            (PipelineStatusArea.Capture, new CaptureUnavailableException("대상 창이 종료되었습니다. 창을 다시 선택하세요."), true),
            (PipelineStatusArea.Capture, new IOException("private/capture-code"), false),
            (PipelineStatusArea.Translation, new TranslationException("캐릭터 사전의 같은 이름에는 하나의 한국어 표기를 지정해 주세요."), true),
            (PipelineStatusArea.Translation, new TranslationException("로컬 번역 시간이 초과되었습니다. 다시 시도해 주세요."), true),
            (PipelineStatusArea.Translation, new IOException("private/worker-pipe"), false)
        };
        var frame = new ImageFrame(120, 50, 480, new byte[24000]);
        foreach (var item in cases) {
            var failure = new TaskCompletionSource<PipelineUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var pipeline = new TranslationPipeline(new FaultOcr(item.Area == PipelineStatusArea.Ocr ? item.Error : null),
                new FaultAi(item.Error), _ => item.Area == PipelineStatusArea.Capture ? Task.FromException<ImageFrame>(item.Error) : Task.FromResult(frame),
                new("en", "ADV", false), new() { Provider = TranslationProvider.HyMtLocal }, new(), "", settle: TimeSpan.Zero);
            pipeline.Updated += update => { if (update.State == PipelineState.Error) failure.TrySetResult(update); };
            pipeline.Start(false); await pipeline.RequestManualAsync();
            var result = await failure.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(result.FailureArea == item.Area && result.OverlayMessage == result.Message, "Subtitle changed the responsible stage or cause");
            Check(item.Known ? result.OverlayMessage == item.Error.Message
                : !result.OverlayMessage!.Contains("private") && !result.OverlayMessage.Contains("모델 파일") && !result.OverlayMessage.Contains("서버"),
                "Pipeline guessed the cause or hid known guidance");
        }
    }
    private sealed class FaultOcr(Exception? error) : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => error is null ? Task.CompletedTask : Task.FromException(error);
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) =>
            error is null ? Task.FromResult(new OcrResult([new("synthetic text", 1, new(10, 10, 90, 25))], TimeSpan.Zero)) : Task.FromException<OcrResult>(error);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FaultAi(Exception error) : ITranslationService
    {
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) => Task.FromException<TranslationResult>(error);
    }
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls;public string Body="",Url="",Auth="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken token){Calls++;Url=message.RequestUri!.ToString();Body=await message.Content!.ReadAsStringAsync(token);
            Auth=message.Headers.Authorization?.ToString()??(message.Headers.TryGetValues("x-goog-api-key",out var keys)?keys.Single():"");string result,media="application/json";
            if(Url.Contains("openai.com")) {
                const string completed="""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"mock translation"}]}]}""";
                using var json=JsonDocument.Parse(Body);
                if(json.RootElement.GetProperty("stream").GetBoolean()){media="text/event-stream";result="data: {\"type\":\"response.output_text.delta\",\"delta\":\"mock \"}\n\ndata: {\"type\":\"response.completed\",\"response\":"+completed+"}\n\n";}else result=completed;
            }else if(Url.Contains("generativelanguage.googleapis.com")) {
                const string completed="""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"mock translation"}]}}]}""";
                if(Url.Contains("streamGenerateContent")){media="text/event-stream";result="data: "+completed+"\n\n";}else result=completed;
            }else if(Url.Contains("localhost"))result="""{"done":true,"message":{"content":"mock translation"}}""";
            else if(Url.Contains("deepl.com"))result="""{"translations":[{"text":"mock translation"}]}""";
            else throw new Exception("Unexpected mock host");
            return new(HttpStatusCode.OK){Content=new StringContent(result,Encoding.UTF8,media)};
        }
    }
}
