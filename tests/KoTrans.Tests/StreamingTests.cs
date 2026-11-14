using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class StreamingTests
{
    private sealed class Handler(string body):HttpMessageHandler
    {
        public string? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Request=await request.Content!.ReadAsStringAsync(token);return new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"text/event-stream")};}
    }
    public static async Task Run()
    {
        var delta="data: {\"type\":\"response.output_text.delta\",\"delta\":\"번역🐈\"}\n\n";
        var complete="data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"번역🐈 완료\"}]}]}}\n\n";
        var progress=new List<string>();var request=new TranslationRequest("synthetic","synthetic",[],new(){Model="gpt-6-luna"},Progress:progress.Add);
        using(var handler=new Handler(delta+complete))using(var client=new HttpClient(handler))
        {
            var result=await new OpenAiTranslationService(client,"synthetic-key").TranslateAsync(request,default);
            using var sent = JsonDocument.Parse(handler.Request!);
            if(sent.RootElement.GetProperty("model").GetString() != "gpt-6-luna" || sent.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() != "none") throw new Exception("Fast translation reasoning/model contract");
            if(result.Text!="번역🐈 완료" || progress.FirstOrDefault()!="번역🐈" || !JsonDocument.Parse(handler.Request!).RootElement.GetProperty("stream").GetBoolean())throw new Exception("Stream progress/completion contract");
        }
        var blanks = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"\"}\n\n"
            + "data: {\"type\":\"response.output_text.delta\",\"delta\":\" \"}\n\n"
            + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"\\n\"}\n\n";
        progress.Clear();
        using(var handler=new Handler(blanks+delta+complete))using(var client=new HttpClient(handler))
        {
            var result=await new OpenAiTranslationService(client,"synthetic-key").TranslateAsync(request,default);
            if(result.Text!="번역🐈 완료" || progress.Count==0 || progress.Any(string.IsNullOrWhiteSpace))throw new Exception("Empty stream prefix cleared the displayed subtitle");
        }
        using(var handler=new Handler(complete))using(var client=new HttpClient(handler))
        {
            await new OpenAiTranslationService(client,"synthetic-key").TranslateAsync(request with { OutputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) },default);
            using var sent=JsonDocument.Parse(handler.Request!);
            if(sent.RootElement.TryGetProperty("reasoning",out _) || sent.RootElement.GetProperty("stream").GetBoolean()) throw new Exception("Study reasoning/JSON mode changed");
        }
        using(var handler=new Handler(complete))using(var client=new HttpClient(handler))
        {
            await new OpenAiTranslationService(client,"synthetic-key").TranslateAsync(request with { Options=request.Options with { Model="unrecognized-model" } },default);
            using var sent=JsonDocument.Parse(handler.Request!);
            if(sent.RootElement.TryGetProperty("reasoning",out _)) throw new Exception("Unsupported reasoning added to unknown model");
        }
        int rejected=0;
        foreach(var body in new[]{delta,"data: {\"type\":\"error\"}\n\n",new string('x',2_097_153)})
        {
            using var handler=new Handler(body);using var client=new HttpClient(handler);
            try{await new OpenAiTranslationService(client,"synthetic-key").TranslateAsync(request,default);throw new Exception("Incomplete/oversized stream accepted");}catch(TranslationException){rejected++;}
        }
        using(var token=new CancellationTokenSource())using(var handler=new Handler(delta+complete))using(var client=new HttpClient(handler))
        {
            try{await new OpenAiTranslationService(client,"synthetic-key").TranslateAsync(request with{Progress=_=>token.Cancel()},token.Token);throw new Exception("Canceled stream completed");}catch(OperationCanceledException){rejected++;}
        }
        Console.WriteLine($"PASS: stream delta/Unicode/completion + {rejected} error/size/cancel guards; Mock only.");
    }
}
