using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;
internal static class GeminiStreamingTests
{
    private static int checks;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    private static string Chunk(string text,string? finish=null,bool thought=false)=>"data: "+JsonSerializer.Serialize(new{candidates=new[]{new{index=0,content=new{parts=new[]{new{text,thought}}},finishReason=finish}}})+"\n\n";
    private static readonly string Final="data: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"}]}\n\n";
    private static TranslationRequest Request(Action<string>? progress=null)=>new("current line","exact prompt",[new("previous source","previous translation")],new(){Provider=TranslationProvider.Gemini,Model="gemini-3.5-flash-lite"},Progress:progress);
    private sealed class Handler(Func<Stream> stream,string media="text/event-stream"):HttpMessageHandler
    {
        public int Calls;public string Body="",Uri="",Key="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;Body=await request.Content!.ReadAsStringAsync(token);Uri=request.RequestUri!.ToString();Key=string.Join("",request.Headers.GetValues("x-goog-api-key"));var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream())};response.Content.Headers.ContentType=new(media);return response;}
    }
    private static MemoryStream Bytes(string value)=>new(Encoding.UTF8.GetBytes(value));
    internal static async Task Run()
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        await FirstTextBeforeFinish();await Requests();await Guards();await FailedStreamContext();
        Console.WriteLine($"PASS: {checks} Gemini streaming assertions; {clock.ElapsedMilliseconds}ms. Mock HTTP only; first text verified before final release. No real API/performance claim.");
    }
    private static async Task FirstTextBeforeFinish()
    {
        using var gated=new GatedStream(Chunk("",thought:true)+Chunk(" ")+Chunk("\n")+Chunk("숨긴 사고",thought:true)+Chunk("번역🐈日本語"),Chunk(" 완료","STOP"));
        using var handler=new Handler(()=>gated);using var client=new HttpClient(handler);var seen=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress=new List<string>();var task=new GeminiTranslationService(client,"synthetic-key").TranslateAsync(Request(text=>{progress.Add(text);seen.TrySetResult(text);}),default);
        try {
            var first=await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!task.IsCompleted && first.Contains("번역🐈日本語"),"Gemini buffered all output until completion");
            Check(progress.All(p=>!string.IsNullOrWhiteSpace(p)&&!p.Contains("숨긴 사고")),"Blank/thought content reached subtitle");
        } finally {gated.Release.TrySetResult();}
        var result=await task;Check(result.Text=="번역🐈日本語 완료" && result.HttpAttempts==1 && handler.Calls==1,"Stream Unicode/final text/attempt count mismatch");
        using var body=JsonDocument.Parse(handler.Body);
        Check(handler.Uri.EndsWith("/gemini-3.5-flash-lite:streamGenerateContent?alt=sse") && !handler.Uri.Contains("synthetic-key") && handler.Key=="synthetic-key","Endpoint/model/auth contract drift");
        Check(body.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString()=="minimal","Flash-Lite minimal policy missing");
        Check(body.RootElement.GetProperty("contents").GetArrayLength()==1 && handler.Body.Contains("previous source") && !handler.Body.Contains("previous translation") && handler.Body.Contains("current line"),"Source-only translation context was not preserved");
        Check(body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()=="exact prompt" && !body.RootElement.GetProperty("generationConfig").TryGetProperty("maxOutputTokens",out _),"Quality prompt/output allowance modified");
    }
    private static async Task Requests()
    {
        const string json="""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"translation"}]}}]}""";
        using(var handler=new Handler(()=>Bytes(json),"application/json"))using(var client=new HttpClient(handler)) {
            var request=Request();await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(request,default);
            Check(handler.Uri.EndsWith(":generateContent") && request.Options.Model=="gemini-3.5-flash-lite","No-progress request changed model or forced streaming");
        }
        using(var handler=new Handler(()=>Bytes(json),"application/json"))using(var client=new HttpClient(handler)) {
            var request=Request(_=>throw new Exception("Study stream callback")) with{OutputSchema=JsonSerializer.SerializeToElement(new{type="object"})};
            await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(request,default);using var sent=JsonDocument.Parse(handler.Body);
            var config=sent.RootElement.GetProperty("generationConfig");
            Check(handler.Uri.EndsWith(":generateContent") && config.GetProperty("responseMimeType").GetString()=="application/json" && !config.TryGetProperty("thinkingConfig",out _),"Study JSON/reasoning behavior modified");
            Check(JsonElement.DeepEquals(config.GetProperty("responseJsonSchema"), request.OutputSchema!.Value)
                && !config.TryGetProperty("responseSchema", out _) && handler.Calls == 1, "Structured Gemini request lost its exact JSON schema or repeated the request");
        }
        foreach(var model in new[]{"models/gemini-3.5-flash-lite","unknown-model","gemini-3.8-flash","gemini-2.5-pro"}) {
            using var handler=new Handler(()=>Bytes(Chunk("valid","STOP")));using var client=new HttpClient(handler);
            var request=Request(_=>{}) with{Options=new(){Model=model}};await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(request,default);using var sent=JsonDocument.Parse(handler.Body);
            Check(request.Options.Model==model && handler.Uri.Contains(model.Replace("models/","")),"Model normalization replaced selected model");
            Check(sent.RootElement.TryGetProperty("generationConfig",out _)==model.StartsWith("models/"),"Unsupported thinking options attached to another model");
        }
        using(var handler=new Handler(()=>Bytes("\uFEFF"+Chunk("BOM text","STOP"))))using(var client=new HttpClient(handler))
            Check((await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(Request(_=>{}),default)).Text=="BOM text","UTF-8 SSE BOM failed");
        // Multiple alternatives, comments, metadata, and multiline SSE frames.
        string eventData=": keepalive\n\nevent: message\ndata: {\"candidates\":[\n"
            +"data: {\"index\":1,\"content\":{\"parts\":[{\"text\":\"wrong alternative\"}]}},\n"
            +"data: {\"index\":0,\"content\":{\"parts\":[{\"text\":\"correct\"}]}}]}\n\n"
            +"data: {\"usageMetadata\":{\"totalTokenCount\":42}}\n\n"+Final;
        using(var handler=new Handler(()=>Bytes(eventData)))using(var client=new HttpClient(handler))
            Check((await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(Request(_=>{}),default)).Text=="correct","SSE framing/alternative selection failed");
    }
    private static async Task Guards()
    {
        var bodies=new[]{Chunk("partial"),Chunk("partial")+"data: [DONE]\n\n",Chunk("partial")+Final.TrimEnd('\n'),
            "data: {bad json}\n\n",Chunk("partial")+"data: {\"error\":{\"message\":\"private source/key\"}}\n\n",
            Chunk("partial","MAX_TOKENS"),Chunk("partial","SAFETY"),Chunk("partial","CONTINUATION"),
            "data: {\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}\n\n",Chunk("","STOP"),Chunk("thought","STOP",true),new string('x',2_097_153),
            "data: {\"candidates\":[{\"index\":1e100}]}\n\n",
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"thought\":\"true\",\"text\":\"untrusted thought\"}]},\"finishReason\":\"STOP\"}]}\n\n",
            "data: {\"candidates\":\"not an array\"}\n\n","data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":42}]}}]}\n\n"};
        foreach(var body in bodies) {
            using var handler=new Handler(()=>Bytes(body));using var client=new HttpClient(handler);bool rejected=false;
            try{await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(Request(_=>{}),default);}catch(TranslationException ex){rejected=!ex.Message.Contains("private source/key");}
            Check(rejected && handler.Calls==1,"Incomplete/error/large response accepted or auto-retried");
        }
        using(var handler=new Handler(()=>new MemoryStream(new byte[]{0xff,0xfe,0xfd})))using(var client=new HttpClient(handler)) {
            bool rejected=false;try{await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(Request(_=>{}),default);}catch(TranslationException){rejected=true;}
            Check(rejected && handler.Calls==1,"Invalid UTF-8 accepted/retried");
        }
        using(var cancel=new CancellationTokenSource())using(var handler=new Handler(()=>Bytes(Chunk("partial")+Final)))using(var client=new HttpClient(handler)) {
            bool canceled=false;try{await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(Request(_=>cancel.Cancel()),cancel.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled && handler.Calls==1,"Cancel after first delta became completed/retried");
        }
        using(var gate=new GatedStream("",Final))using(var handler=new Handler(()=>gate))using(var client=new HttpClient(handler))using(var cancel=new CancellationTokenSource(30)) {
            bool canceled=false;try{await new GeminiTranslationService(client,"synthetic-key").TranslateAsync(Request(_=>{}),cancel.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled,"Waiting stream did not honor cancellation");
        }
    }
    private static async Task FailedStreamContext()
    {
        using var handler=new Handler(()=>Bytes(Chunk("unfinished translation")));using var client=new HttpClient(handler);var session=new TranslationSession();session.Accept("old","old translation",false);
        var error=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var partial=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline=new TranslationPipeline(new SimpleOcr(),new GeminiTranslationService(client,"synthetic-key"),_=>Task.FromResult(new ImageFrame(80,40,320,new byte[12800])),new("en","ADV",false),new(){Model="gemini-3.5-flash-lite"},session,"exact prompt");
        pipeline.Updated+=u=>{if(u.Translation=="unfinished translation")partial.TrySetResult();if(u.State==PipelineState.Error)error.TrySetResult();};
        pipeline.Start(false);await pipeline.RequestManualAsync();await error.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(partial.Task.IsCompleted && session.History.Count==1 && !session.TryLast("new source",out _),"Unfinished Gemini output entered accepted context/cache");
    }
    private sealed class SimpleOcr:IOcrService
    {
        public OcrState State=>OcrState.Ready;public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token)=>Task.FromResult(new OcrResult([new("new source",1,new(2,2,60,20))],TimeSpan.Zero));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class GatedStream(string prefix,string suffix):Stream
    {
        private readonly byte[] first=Encoding.UTF8.GetBytes(prefix),last=Encoding.UTF8.GetBytes(suffix);private int a,b;
        public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){if(a<first.Length){int n=Math.Min(3,Math.Min(buffer.Length,first.Length-a));first.AsMemory(a,n).CopyTo(buffer);a+=n;return n;}await Release.Task.WaitAsync(token);int count=Math.Min(buffer.Length,last.Length-b);last.AsMemory(b,count).CopyTo(buffer);b+=count;return count;}
        public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
    }
}
