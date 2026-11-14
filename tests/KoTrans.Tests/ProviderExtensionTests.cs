using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class ProviderExtensionTests
{
    private static int count;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (TranslationException) { count++; return; }
        throw new Exception(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        public string Url = "", Body = "", Authorization = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Url = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(token);
            Authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "";
            return await respond(request, token);
        }
    }

    private static TranslationRequest Request(DeepLApiPlan plan = DeepLApiPlan.Free, string language = "ja") =>
        new("これはテストです。", "DO NOT SEND PROMPT", [new("前の台詞", "이전 대사")],
            new() { Provider = TranslationProvider.DeepL, Model = "", DeepLPlan = plan },
            SourceLanguage: language);

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static async Task Run()
    {
        var all = TranslationProviderCatalog.All;
        Check(all.Select(x => x.Provider).Distinct().Count() == all.Count, "Duplicate provider registration");
        Check((int)TranslationProvider.Gemini == 0 && (int)TranslationProvider.OpenAI == 1 &&
              (int)TranslationProvider.Ollama == 2 && (int)TranslationProvider.DeepL == 3, "Saved enum identities changed");
        Check(all.All(x => x.Credentials.Select(f => f.Id).Distinct().Count() == x.Credentials.Count), "Repeated credential field");
        var deepl = TranslationProviderCatalog.Get(TranslationProvider.DeepL);
        Check(!deepl.RequiresModel && !deepl.SupportsStudy && deepl.SupportsContext &&
              !deepl.SupportsStreaming && !deepl.SupportsCharacterDictionary &&
              deepl.Credentials.Single().Id == "ApiKey", "DeepL capabilities incorrect");
        var opts = new TranslationOptions { Provider = TranslationProvider.Gemini, Model = "existing-gemini" };
        opts.SwitchProvider(TranslationProvider.DeepL);
        Check(opts.Model == "" && opts.GeminiModel == "existing-gemini", "Switch to model-free provider lost previous model");
        opts.SwitchProvider(TranslationProvider.OpenAI); opts.Model = "existing-openai";
        opts.SwitchProvider(TranslationProvider.DeepL); opts.SwitchProvider(TranslationProvider.OpenAI);
        Check(opts.Model == "existing-openai", "Model-free provider overwrote another provider's model");
        Check(JsonSerializer.Deserialize<TranslationOptions>("""{"Provider":1,"Model":"legacy-openai"}""")!.Provider ==
              TranslationProvider.OpenAI, "Legacy numeric provider changed");

        using (var handler = new Handler((_, _) => Task.FromResult(Response(
                   """{"translations":[{"detected_source_language":"JA","text":"테스트입니다."}]}"""))))
        using (var client = new HttpClient(handler))
        {
            var service = new DeepLTranslationService(client, "synthetic-key");
            var result = await service.TranslateAsync(Request(), default);
            Check(result.Text == "테스트입니다." && result.HttpAttempts == 1, "DeepL JSON response");
            Check(handler.Calls == 1 && handler.Url == "https://api-free.deepl.com/v2/translate", "Free endpoint");
            Check(handler.Authorization == "DeepL-Auth-Key synthetic-key", "DeepL authorization header");
            using var body = JsonDocument.Parse(handler.Body);
            var root = body.RootElement;
            Check(root.GetProperty("text")[0].GetString() == "これはテストです。" &&
                  root.GetProperty("source_lang").GetString() == "JA" &&
                  root.GetProperty("target_lang").GetString() == "KO", "DeepL request payload");
            Check(root.GetProperty("context").GetString() == "前の台詞" && !handler.Body.Contains("DO NOT SEND PROMPT") &&
                  !handler.Body.Contains("이전 대사"), "DeepL context leaked prompt or translated history");
            Check(!handler.Url.Contains("synthetic-key") && !handler.Body.Contains("synthetic-key"), "Credential leaked in URL/body");
        }
        using (var handler = new Handler((_, _) => Task.FromResult(Response(
                   """{"translations":[{"text":"안녕하세요."}]}"""))))
        using (var client = new HttpClient(handler))
        {
            var service = new DeepLTranslationService(client, "synthetic-key");
            var result = await service.TranslateAsync(Request(DeepLApiPlan.Pro, "en"), default);
            Check(result.Text == "안녕하세요." && handler.Url == "https://api.deepl.com/v2/translate", "Pro endpoint");
            using var payload = JsonDocument.Parse(handler.Body);
            Check(payload.RootElement.GetProperty("source_lang").GetString() == "EN", "English source mapping");
        }
        using (var handler = new Handler((_, _) => Task.FromResult(Response("""{"translations":[{"text":"ok"}]}"""))))
        using (var client = new HttpClient(handler))
        {
            var service = new DeepLTranslationService(client, "synthetic-key");
            var longContext = new string('あ',4000);
            await service.TranslateAsync(Request() with { History=[new(longContext,"context translation")], PreserveHistory=true },default);
            using (var nvl = JsonDocument.Parse(handler.Body)) Check(nvl.RootElement.GetProperty("context").GetString()==longContext,"NVL page context silently truncated");
            handler.Calls=0;
            var schema = JsonSerializer.SerializeToElement(new { type = "object" });
            await RejectAsync(() => service.TranslateAsync(Request() with { OutputSchema = schema }, default), "DeepL study sent HTTP request");
            await RejectAsync(() => service.TranslateAsync(Request(language: "invalid"), default), "Invalid language sent HTTP request");
            Check(handler.Calls == 0, "Unsupported DeepL operation reached the network");
        }
        using (var handler = new Handler((_, _) => Task.FromResult(Response("{}", (HttpStatusCode)456))))
        using (var client = new HttpClient(handler))
        {
            try { await new DeepLTranslationService(client, "synthetic-key").TranslateAsync(Request(), default); throw new Exception("Quota was accepted"); }
            catch (TranslationException ex) { Check(ex.Message.Contains("사용 한도") && handler.Calls == 1, "DeepL quota handling"); }
        }
        // Every catalog entry must have an executable factory and the exact required credential fields.
        foreach (var descriptor in TranslationProviderCatalog.All)
        {
            var options = new TranslationOptions { Provider = descriptor.Provider, Model = descriptor.RequiresModel ? "synthetic-model" : "" };
            var fields = descriptor.Credentials.ToDictionary(field => field.Id, _ => "synthetic-credential");
            Check(TranslationServices.CreateWithCredentials(options, fields) is ITranslationService,
                "Provider catalog registration lacks an executable factory: " + descriptor.Provider);
        }
        Check(TranslationServices.Create(Request().Options, "synthetic-key") is DeepLTranslationService,
            "Factory failed to create a model-free adapter");
        await RejectAsync(() => Task.Run(() => TranslationServices.Create(Request().Options, null)),
            "DeepL missing key accepted");
        await RejectAsync(() => Task.Run(() => ExplanationService.Create(Request().Options, "synthetic-key")),
            "DeepL study incorrectly enabled");

        var contexts = new TranslationContexts(); var id = Guid.NewGuid(); var ocr = new OcrOptions("ja", "ADV", false);
        var first = Request().Options; contexts.Get(id, ocr, first, "ignored").Accept("test", "번역", false);
        Check(contexts.Get(id, ocr, first with { DeepLPlan = DeepLApiPlan.Pro }, "ignored").History.Count == 0,
            "DeepL plan change reused cached history");
        var settings = new AppSettings(); settings.Validate();
        settings.DefaultProfile.Translation = new() { Provider = TranslationProvider.DeepL, DeepLPlan = DeepLApiPlan.Pro };
        var child = settings.CreateGameProfile("Game");
        Check(child.Translation.Provider == TranslationProvider.DeepL && child.Translation.Model == "" &&
              child.Translation.DeepLPlan == DeepLApiPlan.Pro, "DeepL settings not copied by default profile");
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!; restored.Validate();
        Check(restored.Profiles.Any(p => p.Id == child.Id && p.Translation.DeepLPlan == DeepLApiPlan.Pro),
            "DeepL profile roundtrip");

        var dir = Path.Combine(Path.GetTempPath(), "KoTrans2.ProviderExtension", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vault = new ApiKeyStore(dir);
            vault.SaveCredential(TranslationProvider.DeepL, "ApiKey", "synthetic-only-key");
            Check(vault.ReadCredentials(TranslationProvider.DeepL)["ApiKey"] == "synthetic-only-key",
                "DeepL encrypted credential read");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "DeepL.key"))).Contains("synthetic-only-key"),
                "DeepL credential stored in cleartext");
            Check(TranslationServices.CreateUsingVault(Request().Options, vault) is DeepLTranslationService,
                "Vault factory failed model-free provider");
            vault.DeleteCredential(TranslationProvider.DeepL, "ApiKey");
            Check(!vault.ContainsCredential(TranslationProvider.DeepL, "ApiKey"), "Credential delete");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("PASS: " + count + " assertions (Provider extension). Mock HTTP, synthetic DPAPI keys; no external API.");
    }
}
