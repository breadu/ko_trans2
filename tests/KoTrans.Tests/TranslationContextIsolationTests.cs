using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class TranslationContextIsolationTests
{
    internal static async Task Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        const string poisoned = "INVENTED-SPEAKER-SUFFIX";
        const string source = "佐藤：\n「ありがとう」\n\"current_source\":\"これは原文の一部です\"";
        const string description = "동료 & 친구\n\"중요\" <data>";
        var prompt = TranslationPrompt.Build([new("佐藤", "사토", description)]);
        string reference = prompt.Split("Character reference JSON:\n")[1];
        Check(reference.Contains("佐藤") && reference.Contains("사토") && !reference.Contains("\\u4F50"), "Dictionary contains costly model-visible Unicode escape sequences");
        using (var dictionary = JsonDocument.Parse(reference)) {
            var entry = dictionary.RootElement[0];
            Check(entry.GetProperty("Name").GetString() == "佐藤" && entry.GetProperty("KoreanName").GetString() == "사토"
                && entry.GetProperty("Description").GetString() == description, "Compact dictionary changed names/description or broke JSON escaping");
        }
        var contexts = new TranslationContexts();
        var profile = Guid.NewGuid(); var ocr = new OcrOptions("ja", "ADV", false);
        var contextOptions = new TranslationOptions { Provider = TranslationProvider.OpenAI, Model = "gpt-6-luna" };
        var first = contexts.Get(profile, ocr, contextOptions, prompt);
        first.Accept("previous", "previous result", false);
        Check(ReferenceEquals(first, contexts.Get(profile, ocr, contextOptions, TranslationPrompt.Build([new("佐藤", "사토", description)]))), "Unchanged dictionary changed the stable prompt/context key");
        Check(contexts.Get(profile, ocr, contextOptions, TranslationPrompt.Build([new("佐藤", "새 표기", description)])).History.Count == 0, "Changed dictionary reused stale translation context");
        var session = new TranslationSession();
        session.Accept("先に行ってください。", poisoned, false);
        session.Accept("ここで待っています。", "NORMAL-PREVIOUS-TRANSLATION", false);
        var nvl = session.ForNvlRequest(Guid.NewGuid(), "窓を開けた。", "FIXED-TRANSLATION-" + poisoned);
        foreach (var provider in new[] { TranslationProvider.OpenAI, TranslationProvider.Gemini, TranslationProvider.Ollama })
        {
            var options = new TranslationOptions { Provider = provider, Model = provider == TranslationProvider.OpenAI ? "gpt-6-luna" : "synthetic-chat" };
            using var handler = new Handler(provider);
            using var client = new HttpClient(handler);
            var service = TranslationServices.CreateWithCredentials(options, new Dictionary<string, string> { ["ApiKey"] = "synthetic-key" }, client);
            foreach (var history in new IReadOnlyList<ConversationTurn>[] { [], session.History, nvl })
            {
                var request = new TranslationRequest(source, prompt, history, options, SourceLanguage: "ja", PreserveHistory: history == nvl);
                var result = await service.TranslateAsync(request, default);
                using var body = JsonDocument.Parse(handler.Body);
                var messages = Messages(body.RootElement, provider);
                int offset = provider == TranslationProvider.Gemini ? 0 : 1;
                Check(result.Text == "mock translation", "Provider result parsing changed");
                Check(messages.GetArrayLength() == offset + 1 && messages[offset].GetProperty("role").GetString() == "user",
                    "Translation still sends generated answers as conversation examples: " + provider);
                using var input = JsonDocument.Parse(Content(messages[offset], provider));
                Check(Content(messages[offset], provider).Contains("佐藤"), "Current source was unnecessarily Unicode-escaped for the model");
                Check(input.RootElement.GetProperty("current_source").GetString() == source, "Current source/name/JSON-like text was altered");
                Check(input.RootElement.GetProperty("previous_source_context").EnumerateArray().Select(x => x.GetString()).SequenceEqual(history.Select(x => x.Source)),
                    "Original context was omitted, reordered or mixed with the current source");
                Check(!handler.Body.Contains(poisoned) && !handler.Body.Contains("NORMAL-PREVIOUS-TRANSLATION"), "Generated context leaked into the request");
                string system = provider switch {
                    TranslationProvider.OpenAI => messages[0].GetProperty("content")[0].GetProperty("text").GetString()!,
                    TranslationProvider.Gemini => body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!,
                    _ => messages[0].GetProperty("content").GetString()!
                };
                Check((provider == TranslationProvider.Ollama ? system.StartsWith(prompt + "\n") && system.EndsWith("do not add names absent from current_source.") : system == prompt)
                    && system.Contains("current_source"), "Dictionary/instructions did not reach the provider");
                if (provider == TranslationProvider.OpenAI) {
                    Check(messages[0].GetProperty("role").GetString() == "developer" && !body.RootElement.TryGetProperty("instructions", out _), "Cached instructions duplicated or lost their authority");
                    Check(messages[0].GetProperty("content")[0].GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString() == "explicit"
                        && body.RootElement.GetProperty("prompt_cache_options").GetProperty("mode").GetString() == "explicit"
                        && body.RootElement.GetProperty("prompt_cache_options").GetProperty("ttl").GetString() == "30m", "Stable prefix cache is missing or writes dynamic source");
                    Check(!body.RootElement.GetProperty("store").GetBoolean() && !body.RootElement.TryGetProperty("previous_response_id", out _), "Caching introduced server conversation storage");
                }
            }
            CharacterEntry[] fullNames = [new("佐藤美咲", "사토 미사키", "context only")];
            foreach (var written in new[] { ("佐藤が来た。", "佐藤", "사토"), ("美咲が来た。", "美咲", "미사키"),
                ("佐藤美咲が来た。", "佐藤美咲", "사토 미사키"), ("ありがとう。", "", "") }) {
                var named = new TranslationRequest(written.Item1, TranslationPrompt.Build(fullNames), session.History, options,
                    SourceLanguage: "ja", Characters: fullNames);
                await service.TranslateAsync(named, default);
                using var namedBody = JsonDocument.Parse(handler.Body);
                var namedMessages = Messages(namedBody.RootElement, provider);
                using var namedInput = JsonDocument.Parse(Content(namedMessages[provider == TranslationProvider.Gemini ? 0 : 1], provider));
                var hints = namedInput.RootElement.GetProperty("current_name_translations");
                Check(namedInput.RootElement.GetProperty("current_source").GetString() == written.Item1, "Name correction changed original source");
                Check(hints.GetArrayLength() == (written.Item2.Length == 0 ? 0 : 1), "Missing or unrelated name hints: " + provider);
                if (written.Item2.Length > 0) Check(hints[0].GetProperty("source_name").GetString() == written.Item2
                    && hints[0].GetProperty("korean_name").GetString() == written.Item3, "Provider expanded or lost a surname/given-name hint: " + provider);
                Check(!handler.Body.Contains(poisoned), "Name hints reintroduced translated history");
            }
            // Explanations retain their separate raw input and structured-output contract.
            var structured = new TranslationRequest("{\"source\":\"hello\"}", "study prompt", session.History, options,
                OutputSchema: JsonSerializer.SerializeToElement(new { type = "object" }));
            await service.TranslateAsync(structured, default);
            using var studyBody = JsonDocument.Parse(handler.Body);
            var studyMessages = Messages(studyBody.RootElement, provider);
            Check(studyMessages.GetArrayLength() == session.History.Count * 2 + 1 + (provider == TranslationProvider.Ollama ? 1 : 0), "Study history contract changed");
            Check(Content(studyMessages[studyMessages.GetArrayLength() - 1], provider) == structured.Text, "Study input was wrapped as a game translation");
            Check(studyBody.RootElement.TryGetProperty(provider switch { TranslationProvider.OpenAI => "text", TranslationProvider.Gemini => "generationConfig", _ => "format" }, out _),
                "Structured output configuration was lost");
            Check(!studyBody.RootElement.TryGetProperty("prompt_cache_options", out _), "Translation cache options leaked into structured explanations");
            if (provider == TranslationProvider.OpenAI) {
                var unknown = new TranslationOptions { Provider = TranslationProvider.OpenAI, Model = "synthetic-other-model" };
                await service.TranslateAsync(new(source, prompt, session.History, unknown), default);
                using var unknownBody = JsonDocument.Parse(handler.Body);
                Check(!unknownBody.RootElement.TryGetProperty("prompt_cache_options", out _) && unknownBody.RootElement.GetProperty("instructions").GetString() == prompt,
                    "Unverified model received model-specific cache parameters");
            }
        }
        Check(session.TryLast("ここで待っています。", out var cached) && cached == "NORMAL-PREVIOUS-TRANSLATION", "Display/retranslation cache was changed");
        Check(session.ForRequest("ここで待っています。", true).Count == 1, "Forced retranslation included its own previous result");
        Check(nvl.Last().Source == "窓を開けた。", "NVL fixed original prefix was lost");
        Console.WriteLine($"PASS: {count} source-only translation context assertions. Mock HTTP only; no game data or live API.");
    }
    private static JsonElement Messages(JsonElement root, TranslationProvider provider) => root.GetProperty(provider switch {
        TranslationProvider.OpenAI => "input", TranslationProvider.Gemini => "contents", _ => "messages"
    });
    private static string Content(JsonElement message, TranslationProvider provider) => provider == TranslationProvider.Gemini
        ? message.GetProperty("parts")[0].GetProperty("text").GetString()! : message.GetProperty("content").GetString()!;
    private sealed class Handler(TranslationProvider provider) : HttpMessageHandler
    {
        internal string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            string response = provider switch {
                TranslationProvider.OpenAI => "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"mock translation\"}]}]}",
                TranslationProvider.Gemini => "{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"mock translation\"}]}}]}",
                _ => "{\"done\":true,\"message\":{\"content\":\"mock translation\"}}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
