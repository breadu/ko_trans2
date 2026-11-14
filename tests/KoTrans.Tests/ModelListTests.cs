using System.Net;
using System.Text;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class ModelListTests
{
    internal static async Task Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        using var handler = new Handler(); using var client = new HttpClient(handler);
        var gemini = await ProviderModelList.LoadAsync(TranslationProvider.Gemini, "synthetic-key", client, default);
        Check(gemini.Select(m => m.Id).SequenceEqual(new[] { "a", "b" }) && handler.Calls == 2, "Gemini pagination/filter/sort failed");
        Check(handler.Urls.All(u => !u.Contains("synthetic-key")) && handler.Auth.All(a => a == "synthetic-key"), "Gemini key escaped headers");
        handler.Calls = 0;
        var openai = await ProviderModelList.LoadAsync(TranslationProvider.OpenAI, "synthetic-key", client, default);
        Check(openai.Select(m => m.Id).SequenceEqual(new[] { "chosen-a", "chosen-b" }) && handler.Calls == 1 && handler.Auth.Last() == "Bearer synthetic-key", "OpenAI list/auth failed");
        foreach (var variant in new[] { "empty", "malformed", "wrongType", "huge", "unauthorized", "forbidden", "limited", "server", "network", "timeout" }) {
            handler.Variant = variant; handler.Calls = 0;
            try {
                var result = await ProviderModelList.LoadAsync(TranslationProvider.OpenAI, "synthetic-key", client, default);
                Check(variant == "empty" && result.Count == 0, "Invalid model data was accepted: " + variant);
            } catch (TranslationException ex) { Check(variant != "empty" && !ex.Message.Contains("synthetic-key") && !ex.Message.Contains("secret-response"), "Unsafe metadata failure: " + variant); }
            Check(handler.Calls == 1, "Model list auto-retried a failure");
        }
        handler.Calls = 0;
        try { await ProviderModelList.LoadAsync(TranslationProvider.OpenAI, null, client, default); throw new Exception("Missing key accepted"); }
        catch (TranslationException) { Check(handler.Calls == 0, "Missing key reached network"); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await ProviderModelList.LoadAsync(TranslationProvider.Gemini, "synthetic-key", client, canceled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { count++; }

        using var ollamaHandler = new OllamaHandler(); using var ollamaClient = new HttpClient(ollamaHandler);
        var installed = await ProviderModelList.LoadOllamaAsync("http://localhost:11434", ollamaClient, default);
        Check(installed.Select(m => m.Id).SequenceEqual(new[] { "qwen3:8b", "gemma3:4b", "translategemma:12b" })
            && ollamaHandler.Calls == 1 && ollamaHandler.Url == "http://localhost:11434/api/tags"
            && ollamaHandler.Method == HttpMethod.Get && !ollamaHandler.HadAuthorization,
            "Ollama installed-model lookup changed model IDs, endpoint or authentication");
        foreach (var variant in new[] { "empty", "malformed", "wrongType", "huge", "tooMany", "server", "network", "timeout" }) {
            ollamaHandler.Variant = variant; ollamaHandler.Calls = 0;
            try {
                var result = await ProviderModelList.LoadOllamaAsync("http://localhost:11434", ollamaClient, default);
                Check(variant == "empty" && result.Count == 0, "Invalid Ollama model data was accepted: " + variant);
            } catch (TranslationException ex) {
                Check(variant != "empty" && !ex.Message.Contains("secret-response") && !ex.Message.Contains("qwen3:8b"),
                    "Ollama metadata error exposed server content or swallowed empty list: " + variant);
            }
            Check(ollamaHandler.Calls == 1, "Ollama model lookup retried a failed request");
        }
        ollamaHandler.Calls = 0;
        try { await ProviderModelList.LoadOllamaAsync("not-a-url", ollamaClient, default); throw new Exception("Invalid Ollama URL accepted"); }
        catch (TranslationException) { Check(ollamaHandler.Calls == 0, "Invalid address reached network"); }
        using var ollamaCanceled = new CancellationTokenSource(); ollamaCanceled.Cancel();
        try { await ProviderModelList.LoadOllamaAsync("http://localhost:11434", ollamaClient, ollamaCanceled.Token); throw new Exception("Ollama cancellation ignored"); }
        catch (OperationCanceledException) { count++; }
        Console.WriteLine($"PASS: {count} official model-list Mock HTTP assertions.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string Variant = ""; public int Calls; public List<string> Urls = [], Auth = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Urls.Add(request.RequestUri!.ToString());
            Auth.Add(request.Headers.Authorization?.ToString() ?? request.Headers.GetValues("x-goog-api-key").Single());
            if (Variant == "network") throw new HttpRequestException("secret-response");
            if (Variant == "timeout") throw new TaskCanceledException();
            var status = Variant switch { "unauthorized" => HttpStatusCode.Unauthorized, "forbidden" => HttpStatusCode.Forbidden, "limited" => HttpStatusCode.TooManyRequests, "server" => HttpStatusCode.InternalServerError, _ => HttpStatusCode.OK };
            string body = Variant switch {
                "empty" => "{\"data\":[]}", "malformed" => "broken-secret-response", "wrongType" => "{\"data\":[{\"id\":7}]}", "huge" => new string('x', 1_048_577),
                _ => request.RequestUri.Host == "api.openai.com" ? "{\"data\":[{\"id\":\"chosen-b\"},{\"id\":\"chosen-a\"}]}"
                    : Calls == 1 ? "{\"models\":[{\"name\":\"models/b\",\"supportedGenerationMethods\":[\"generateContent\"]},{\"name\":\"models/embed\",\"supportedGenerationMethods\":[\"embedContent\"]}],\"nextPageToken\":\"page-2\"}"
                    : "{\"models\":[{\"name\":\"models/a\",\"supportedGenerationMethods\":[\"generateContent\"]}]}" };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class OllamaHandler : HttpMessageHandler
    {
        public string Variant = "", Url = "";
        public HttpMethod? Method;
        public bool HadAuthorization;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Url = request.RequestUri!.ToString(); Method = request.Method;
            HadAuthorization = request.Headers.Authorization is not null;
            if (Variant == "network") throw new HttpRequestException("secret-response");
            if (Variant == "timeout") throw new TaskCanceledException();
            var status = Variant == "server" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            string body = Variant switch {
                "empty" => "{\"models\":[]}",
                "malformed" => "secret-response",
                "wrongType" => "{\"models\":[{\"name\":7}]}",
                "huge" => new string('x', 1_048_577),
                "tooMany" => "{\"models\":[" + string.Join(',', Enumerable.Range(0, 5001).Select(i => "{\"name\":\"model-" + i + "\"}")) + "]}",
                _ => "{\"models\":[{\"name\":\"qwen3:8b\"},{\"name\":\"gemma3:4b\"},{\"name\":\"translategemma:12b\"}]}"
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
