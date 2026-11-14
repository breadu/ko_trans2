using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public static class TranslationServices
{
    internal static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    // A provider adds one catalog entry and one factory registration; callers are unchanged.
    private static readonly IReadOnlyDictionary<TranslationProvider, Func<HttpClient, IReadOnlyDictionary<string, string>, ITranslationService>> Factories =
        new Dictionary<TranslationProvider, Func<HttpClient, IReadOnlyDictionary<string, string>, ITranslationService>>
        {
            [TranslationProvider.Gemini] = (client, fields) => new GeminiTranslationService(client, fields["ApiKey"]),
            [TranslationProvider.OpenAI] = (client, fields) => new OpenAiTranslationService(client, fields["ApiKey"]),
            [TranslationProvider.Ollama] = (client, _) => new OllamaTranslationService(client),
            [TranslationProvider.DeepL] = (client, fields) => new DeepLTranslationService(client, fields["ApiKey"]),
            [TranslationProvider.HyMtLocal] = (_, _) => new HyMtTranslationService(LocalTranslationEnvironment.Runtime)
        };

    // Keep the existing API for existing tests/callers using a single API key.
    public static ITranslationService Create(TranslationOptions options, string? key) =>
        CreateWithCredentials(options, string.IsNullOrWhiteSpace(key)
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["ApiKey"] = key });

    public static ITranslationService CreateUsingVault(TranslationOptions options, ApiKeyStore vault) => CreateUsingVault(options,vault,SharedClient);
    internal static ITranslationService CreateUsingVault(TranslationOptions options, ApiKeyStore vault, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return CreateWithCredentials(options,vault.ReadCredentials(options.Provider),client);
    }

    public static ITranslationService CreateWithCredentials(TranslationOptions options, IReadOnlyDictionary<string, string> credentials) => CreateWithCredentials(options,credentials,SharedClient);
    internal static ITranslationService CreateWithCredentials(TranslationOptions options, IReadOnlyDictionary<string, string> credentials,HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        options.Validate();
        var info = TranslationProviderCatalog.Get(options.Provider);
        if (info.RequiresModel && string.IsNullOrWhiteSpace(options.Model))
            throw new TranslationException("사용할 모델 ID를 입력하세요. 모델을 자동 선택하지 않습니다.");
        foreach (var field in info.Credentials)
            if (!credentials.TryGetValue(field.Id, out var value) || string.IsNullOrWhiteSpace(value))
                throw new TranslationException(info.DisplayName + " " + field.Label + "를 기본 프로필에서 등록하세요.");
        if (!Factories.TryGetValue(options.Provider, out var factory))
            throw new TranslationException("번역 제공자의 실행 구현이 등록되지 않았습니다.");
        return options.Provider == TranslationProvider.HyMtLocal
            ? new HyMtTranslationService(LocalTranslationEnvironment.Runtime, options.LocalModel)
            : factory(client, credentials);
    }
}

public abstract class HttpTranslationService(HttpClient client) : ITranslationService, ITranslationDiagnostics
{
    private int attempts;
    public int HttpAttempts => Volatile.Read(ref attempts);
    protected abstract HttpRequestMessage CreateRequest(TranslationRequest request);
    protected abstract string Parse(JsonElement json);
    protected virtual Task<string> ReadStreamAsync(Stream stream, TranslationRequest request, CancellationToken token) => OpenAiStream.ReadAsync(stream,request.Progress,token);
    protected virtual string TimeoutMessage(TranslationRequest request, bool headers) => "AI 요청 시간이 초과되었습니다. 처리 여부가 불명확하여 자동 재전송하지 않았습니다.";
    protected virtual string ConnectionMessage => "AI 서버 연결이 끊겼습니다. 처리 여부가 불명확하여 자동 재전송하지 않았습니다.";
    protected virtual bool CanRetryPreparationFailure(TranslationRequest request, TranslationException failure) => false;
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
    {
        request.Options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Options.RequestTimeoutSeconds));
        bool headersReceived = false;
        var clock = Stopwatch.StartNew();
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var message = CreateRequest(request);
                Interlocked.Increment(ref attempts);
                headersReceived = false;
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                headersReceived = true;
                timeout.Token.ThrowIfCancellationRequested();
                // Only a definite rejection may be retried, once. Network/timeout/parse failures are never retried.
                if (attempt == 1 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1);
                    if (delay <= TimeSpan.FromSeconds(5))
                    { await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, timeout.Token).ConfigureAwait(false); continue; }
                }
                if (!response.IsSuccessStatusCode) {
                    var failure = await ResponseErrorAsync(response, request, timeout.Token).ConfigureAwait(false);
                    // A cancellation can race with the final buffered error bytes/classification.
                    // Retired requests must stay canceled instead of becoming a visible server error.
                    timeout.Token.ThrowIfCancellationRequested();
                    // A local Ollama tokenization failure is before generation in
                    // /api/chat. Retry only that positively identified failure once,
                    // within the original deadline. Other providers/errors opt out.
                    if (attempt == 1 && CanRetryPreparationFailure(request, failure)) {
                        response.Dispose();
                        await Task.Delay(250, timeout.Token).ConfigureAwait(false);
                        continue;
                    }
                    throw failure;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                {
                    var streamed = await ReadStreamAsync(stream, request, timeout.Token).ConfigureAwait(false);
                    return new(streamed, clock.Elapsed, attempt);
                }
                using var buffer = new MemoryStream(); var chunk = new byte[8192]; int length;
                while ((length = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + length > 2_097_152) throw new TranslationException("AI 응답 크기 제한을 초과했습니다.");
                    buffer.Write(chunk, 0, length);
                }
                using var json = JsonDocument.Parse(buffer.ToArray());
                var text = Parse(json.RootElement).Trim();
                if (text.Length == 0) throw new TranslationException("AI가 번역문을 반환하지 않았습니다. 모델 및 응답 제한을 확인하세요.");
                return new(text, clock.Elapsed, attempt);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TranslationException(TimeoutMessage(request, headersReceived)); }
        catch (HttpRequestException) { token.ThrowIfCancellationRequested(); throw new TranslationException(ConnectionMessage); }
        catch (IOException) { token.ThrowIfCancellationRequested(); throw new TranslationException("AI 응답 수신에 실패했습니다. 자동 재전송하지 않았습니다."); }
        catch (DecoderFallbackException) { token.ThrowIfCancellationRequested(); throw new TranslationException("AI 응답 문자 인코딩을 확인할 수 없습니다. 자동 재전송하지 않았습니다."); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { token.ThrowIfCancellationRequested(); throw new TranslationException("AI 응답 형식을 확인할 수 없습니다. 자동 재전송하지 않았습니다."); }
    }
    protected virtual Task<TranslationException> ResponseErrorAsync(HttpResponseMessage response, TranslationRequest request, CancellationToken token) => Task.FromResult(new TranslationException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API 키 또는 해당 모델 접근 권한을 확인하세요.",
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound => request.OutputSchema is null ? "모델 ID 또는 요청을 제공자가 거부했습니다. 입력한 모델을 확인하세요." : "모델 또는 구조화 JSON 요청을 제공자가 거부했습니다. 해당 출력 형식을 지원하는 모델인지 확인하세요. 모델 변경이나 자동 대체 요청은 하지 않았습니다.",
                    HttpStatusCode.TooManyRequests => "API 요청 한도에 도달했습니다. 잠시 후 수동으로 다시 시도하세요.",
                    HttpStatusCode.ServiceUnavailable => "AI 서비스가 일시적으로 사용할 수 없습니다. 잠시 후 다시 시도하세요.",
                    _ => $"AI 응답 오류 (HTTP {(int)response.StatusCode}). 자동 재전송하지 않았습니다."
                }));
    protected static HttpRequestMessage JsonRequest(string uri, object body) => new(HttpMethod.Post, uri)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    internal static object[] Messages(TranslationRequest request, bool system)
    {
        var messages = new List<object>();
        if (system) messages.Add(new { role = "system", content = request.SystemPrompt });
        // Structured explanations have a separate prompt/input contract.
        if (request.OutputSchema is not null) {
            foreach (var turn in request.History)
            { messages.Add(new { role = "user", content = turn.Source }); messages.Add(new { role = "assistant", content = turn.Translation }); }
            messages.Add(new { role = "user", content = request.Text });
        }
        else messages.Add(new { role = "user", content = TranslationPrompt.Input(request) });
        return messages.ToArray();
    }
}
public sealed class OpenAiTranslationService(HttpClient client, string key) : HttpTranslationService(client)
{
    protected override HttpRequestMessage CreateRequest(TranslationRequest request)
    {
        var body = new Dictionary<string, object> { ["model"] = request.Options.Model, ["instructions"] = request.SystemPrompt, ["input"] = Messages(request, false), ["store"] = false, ["stream"] = request.OutputSchema is null && request.Progress is not null };
        // Short real-time translations need low latency; this exact model officially supports none.
        // Structured study explanations retain the provider default reasoning behavior.
        if (request.OutputSchema is null && request.Options.Model == "gpt-6-luna") {
            body["reasoning"] = new { effort = "none" };
            // This exact model supports explicit prefix caching. Cache the stable
            // instructions/dictionary, not changing OCR lines or sliding history.
            // Keep other models and structured explanations on their existing contract.
            body.Remove("instructions");
            body["input"] = new object[] {
                new { role = "developer", content = new[] { new { type = "input_text", text = request.SystemPrompt,
                    prompt_cache_breakpoint = new { mode = "explicit" } } } },
                new { role = "user", content = TranslationPrompt.Input(request) }
            };
            body["prompt_cache_options"] = new { mode = "explicit", ttl = "30m" };
        }
        if (request.OutputSchema is { } schema) body["text"] = new { format = new { type = "json_schema", name = "ko_trans_study", strict = true, schema } };
        var message = JsonRequest("https://api.openai.com/v1/responses", body);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return message;
    }
    protected override string Parse(JsonElement json)
    {
        if (json.TryGetProperty("status", out var status) && status.GetString() != "completed") throw new TranslationException("OpenAI 응답이 완료되지 않았습니다. 모델 또는 출력 제한을 확인하세요.");
        return string.Join("\n", json.GetProperty("output").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "message")
            .SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.GetProperty("type").GetString() == "output_text").Select(x => x.GetProperty("text").GetString()));
    }
}
public sealed class GeminiTranslationService(HttpClient client, string key) : HttpTranslationService(client)
{
    protected override HttpRequestMessage CreateRequest(TranslationRequest request)
    {
        var contents = new List<object>();
        if (request.OutputSchema is not null) {
            foreach (var turn in request.History)
            { contents.Add(new { role = "user", parts = new[] { new { text = turn.Source } } }); contents.Add(new { role = "model", parts = new[] { new { text = turn.Translation } } }); }
            contents.Add(new { role = "user", parts = new[] { new { text = request.Text } } });
        }
        else contents.Add(new { role = "user", parts = new[] { new { text = TranslationPrompt.Input(request) } } });
        var model = request.Options.Model.StartsWith("models/", StringComparison.Ordinal) ? request.Options.Model[7..] : request.Options.Model;
        var body = new Dictionary<string, object> { ["systemInstruction"] = new { parts = new[] { new { text = request.SystemPrompt } } }, ["contents"] = contents };
        if (request.OutputSchema is not null) body["generationConfig"] = new { responseMimeType = "application/json" };
        // Verified text-translation contract only. Do not infer compatibility from model-name substrings.
        // This pins the documented default; Flash-Lite has no supported fully-off thinking setting.
        else if (model == "gemini-3.5-flash-lite") body["generationConfig"] = new { thinkingConfig = new { thinkingLevel = "minimal" } };
        bool streaming = request.OutputSchema is null && request.Progress is not null;
        var operation = streaming ? "streamGenerateContent?alt=sse" : "generateContent";
        var message = JsonRequest($"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:{operation}", body);
        message.Headers.Add("x-goog-api-key", key); return message;
    }
    protected override Task<string> ReadStreamAsync(Stream stream, TranslationRequest request, CancellationToken token) => GeminiStream.ReadAsync(stream,request.Progress,token);
    protected override string Parse(JsonElement json)
    {
        if (!json.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) throw new TranslationException("Gemini가 번역 후보를 반환하지 않았습니다.");
        var candidate = candidates[0];
        if (candidate.TryGetProperty("finishReason", out var reason) && reason.GetString() != "STOP") throw new TranslationException("Gemini 응답이 중단되었습니다. 모델 또는 응답 제한을 확인하세요.");
        return string.Join("\n", candidate.GetProperty("content").GetProperty("parts").EnumerateArray()
            .Where(x => x.TryGetProperty("text", out _) && !(x.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)).Select(x => x.GetProperty("text").GetString()));
    }
}
public sealed class OllamaTranslationService(HttpClient client) : HttpTranslationService(client)
{
    protected override bool CanRetryPreparationFailure(TranslationRequest request, TranslationException failure) =>
        request.Options.Provider == TranslationProvider.Ollama && request.OutputSchema is null
        && failure is OllamaServerException { Status: 500, Kind: OllamaFailureKind.InternalConnectionClosed, Stage: OllamaFailureStage.Tokenize }
        && Uri.TryCreate(request.Options.OllamaAddress, UriKind.Absolute, out var address)
        && address.Scheme == "http" && address.IsLoopback && address.AbsolutePath is "" or "/";

    protected override HttpRequestMessage CreateRequest(TranslationRequest request)
    {
        var settings = request.Options.SettingsForOllama();
        var adapter = OllamaPromptFormats.Get(settings.Format);
        if (request.OutputSchema is not null && !adapter.SupportsStudy)
            throw new TranslationException("선택한 Ollama 요청 형식은 번역 전용이며 문장 해설·학습 JSON을 지원하지 않습니다. 학습에는 일반 대화형 모델과 Chat 요청 형식을 선택하세요. 모델을 자동 변경하거나 요청을 전송하지 않았습니다.");
        var body = new Dictionary<string, object> { ["model"] = request.Options.Model, ["messages"] = adapter.Messages(request), ["stream"] = false, ["keep_alive"] = $"{settings.KeepAliveMinutes}m" };
        if (request.OutputSchema is { } schema) body["format"] = schema;
        // Real-time translation needs the answer, not a model's default reasoning
        // phase. Structured study requests retain their existing model behavior.
        else body["think"] = false;
        return JsonRequest(request.Options.OllamaAddress.TrimEnd('/') + "/api/chat", body);
    }
    protected override string TimeoutMessage(TranslationRequest request, bool headers) =>
        $"Ollama {request.Options.RequestTimeoutSeconds}초 제한 초과 · {(headers ? "응답 수신 중" : "모델 실행 응답 대기 중")}. 최초 모델 로딩·입력 처리·생성 지연 또는 GPU/메모리 부족 가능성이 있습니다. 서버가 원인을 반환하지 않아 확정할 수 없습니다. 모델 로딩/GPU 배치를 확인하고 필요하면 이 모델의 제한 시간을 늘리세요. 추가 자동 재요청은 하지 않습니다.";
    protected override string ConnectionMessage => "Ollama 서버에 연결하지 못했거나 연결이 끊겼습니다. Ollama 실행 여부·서버 주소·포트를 확인하세요. 모델 미설치 여부는 연결 복구 후 확인할 수 있습니다. 자동 재요청하지 않았습니다.";
    protected override async Task<TranslationException> ResponseErrorAsync(HttpResponseMessage response, TranslationRequest request, CancellationToken token)
    {
        string error = "";
        try {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = new byte[16385]; int count = 0, read;
            while(count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false)) > 0) count += read;
            if(count <= 16384) { using var json = JsonDocument.Parse(bytes.AsMemory(0,count)); if(json.RootElement.TryGetProperty("error",out var value) && value.ValueKind == JsonValueKind.String) error = value.GetString() ?? ""; }
        } catch(JsonException) { } // Classify only; never expose HTTP bodies, paths or source text.
        return OllamaFailure.Classify((int)response.StatusCode, error);
    }
    protected override string Parse(JsonElement json)
    {
        if (!json.GetProperty("done").GetBoolean() || (json.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length"))
            throw new TranslationException("Ollama 응답이 완료되지 않았습니다.");
        return json.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

// DeepL is a text-translation service: no chat instructions, model ID, streaming or study JSON.
// Use fixed HTTPS endpoints based on the user-selected Free/Pro plan.
public sealed class DeepLTranslationService(HttpClient client, string key) : HttpTranslationService(client)
{
    protected override HttpRequestMessage CreateRequest(TranslationRequest request)
    {
        if (request.OutputSchema is not null)
            throw new TranslationException("DeepL은 번역 전용 서비스로 문장 해설·학습 JSON을 지원하지 않습니다. 학습에는 AI 제공자를 선택하세요.");
        var source = request.SourceLanguage switch
        {
            "en" => "EN",
            "ja" => "JA",
            _ => throw new TranslationException("DeepL 번역에는 원문 언어가 필요합니다. 프로필에서 영어 또는 일본어를 선택하세요.")
        };
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new TranslationException("번역할 원문이 없습니다.");
        var uri = request.Options.DeepLPlan == DeepLApiPlan.Free
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        var body = new Dictionary<string, object>
        {
            ["text"] = new[] { request.Text },
            ["source_lang"] = source,
            ["target_lang"] = "KO"
        };
        // DeepL's context field takes source text, not system prompts or translated history.
        var context = string.Join("\n", request.History.Select(turn => turn.Source));
        if (!request.PreserveHistory && context.Length > 3000) context = context[^3000..];
        if (context.Length > 0) body["context"] = context;
        var message = JsonRequest(uri, body);
        message.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
        return message;
    }

    protected override string Parse(JsonElement json)
    {
        var translations = json.GetProperty("translations");
        if (translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() != 1)
            throw new TranslationException("DeepL 번역 응답에 결과가 없습니다.");
        return translations[0].GetProperty("text").GetString() ?? "";
    }

    protected override string TimeoutMessage(TranslationRequest request, bool headers) =>
        "DeepL 번역 요청 시간이 초과되었습니다. 처리 여부가 불명확하여 자동 재전송하지 않았습니다.";
    protected override string ConnectionMessage =>
        "DeepL 번역 서버에 연결하지 못했습니다. 네트워크 연결과 서비스 상태를 확인하세요.";
    protected override Task<TranslationException> ResponseErrorAsync(HttpResponseMessage response, TranslationRequest request, CancellationToken token)
        => Task.FromResult(new TranslationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "DeepL API 키와 Free/Pro 요금제 선택을 확인하세요.",
            HttpStatusCode.BadRequest => "DeepL 요청이 거부되었습니다. 원문 언어와 번역 설정을 확인하세요.",
            HttpStatusCode.TooManyRequests => "DeepL 요청 빈도 제한에 도달했습니다. 잠시 후 수동으로 다시 시도하세요.",
            HttpStatusCode.ServiceUnavailable => "DeepL 서비스를 일시적으로 사용할 수 없습니다.",
            _ when (int)response.StatusCode == 456 => "DeepL API 번역 문자 사용 한도를 초과했습니다. 요금제·사용량을 확인하세요.",
            _ => "DeepL 번역 서버 오류 (HTTP " + (int)response.StatusCode + "). 자동 재전송하지 않았습니다."
        }));
}

// Explicit QA mode only; UI clearly labels all output. Never selected through production settings.
public sealed class MockTranslationService : ITranslationService
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); await Task.Delay(180, token);
        var text = request.Text == "The journey begins with a single step." ? "여정은 한 걸음에서 시작됩니다." : "번역 연결을 확인했습니다.";
        return new("[모의 응답 · 외부 API 미사용] " + text, clock.Elapsed, 0);
    }
}
