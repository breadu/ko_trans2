using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public sealed record ProviderModel(string Id);

// Explicit metadata lookup only. No generation, key logging, automatic selection or retries.
public static class ProviderModelList
{
    public static Task<IReadOnlyList<ProviderModel>> LoadOllamaAsync(string address, CancellationToken token) =>
        LoadOllamaAsync(address, TranslationServices.SharedClient, token);

    internal static async Task<IReadOnlyList<ProviderModel>> LoadOllamaAsync(string address, HttpClient client, CancellationToken token)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https") || string.IsNullOrEmpty(server.Host) ||
            !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
            throw new TranslationException("Ollama 서버 주소가 올바르지 않습니다. http://localhost:11434 형식으로 입력하세요.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, server.AbsoluteUri.TrimEnd('/') + "/api/tags");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"Ollama 서버가 모델 목록 요청을 거부했습니다 (HTTP {(int)response.StatusCode}). 주소와 서버 상태를 확인하세요.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > 1_048_576) throw new TranslationException("Ollama 모델 목록 응답 크기 제한을 초과했습니다.");
                buffer.Write(bytes, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (!json.RootElement.TryGetProperty("models", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            var models = new List<ProviderModel>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                var id = name.GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl)) throw new JsonException();
                if (ids.Add(id)) models.Add(new ProviderModel(id));
                if (models.Count > 5000) throw new TranslationException("Ollama 모델 목록 항목 수 제한을 초과했습니다.");
            }
            return models;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TranslationException("Ollama 서버 응답 시간이 초과되었습니다. 주소와 서버 상태를 확인하세요.");
        }
        catch (HttpRequestException)
        {
            throw new TranslationException("Ollama 서버에 연결할 수 없습니다. 실행 상태와 주소를 확인하세요.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new TranslationException("Ollama 모델 목록 응답 형식을 읽을 수 없습니다.");
        }
    }

    public static Task<IReadOnlyList<ProviderModel>> LoadAsync(TranslationProvider provider, ApiKeyStore vault, CancellationToken token) =>
        LoadAsync(provider, vault.Read(provider), TranslationServices.SharedClient, token);
    internal static async Task<IReadOnlyList<ProviderModel>> LoadAsync(TranslationProvider provider, string? key, HttpClient client, CancellationToken token)
    {
        if (provider is not (TranslationProvider.Gemini or TranslationProvider.OpenAI)) throw new TranslationException("이 제공자는 공식 모델 목록 조회를 지원하지 않습니다.");
        if (string.IsNullOrWhiteSpace(key)) throw new TranslationException("기본 프로필의 공통 설정에서 API 키를 먼저 등록하세요.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var models = new HashSet<string>(StringComparer.Ordinal); var tokens = new HashSet<string>(StringComparer.Ordinal); string? next = null;
        try {
            for (int page = 0; page < 10; page++) {
                string url = provider == TranslationProvider.Gemini ? "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000"
                    + (next is null ? "" : "&pageToken=" + Uri.EscapeDataString(next)) : "https://api.openai.com/v1/models";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (provider == TranslationProvider.Gemini) request.Headers.Add("x-goog-api-key", key);
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new TranslationException(response.StatusCode switch {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "모델 목록 조회가 거부되었습니다. 등록한 API 키와 조회 권한을 확인하세요.",
                    HttpStatusCode.TooManyRequests => "모델 목록 조회 한도에 도달했습니다. 잠시 후 다시 조회하세요.",
                    _ => $"공식 모델 목록 서버 오류 (HTTP {(int)response.StatusCode}). 잠시 후 다시 조회하세요." });
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) != 0) {
                    if (buffer.Length + read > 1_048_576) throw new TranslationException("모델 목록 응답 크기 제한을 초과했습니다.");
                    buffer.Write(bytes, 0, read);
                }
                using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var root = json.RootElement;
                if (!root.TryGetProperty(provider == TranslationProvider.Gemini ? "models" : "data", out var items) || items.ValueKind != JsonValueKind.Array) throw new JsonException();
                foreach (var item in items.EnumerateArray()) {
                    if (provider == TranslationProvider.Gemini) {
                        if (!item.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array) throw new JsonException();
                        if (methods.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.String)) throw new JsonException();
                        if (!methods.EnumerateArray().Any(m => m.GetString() == "generateContent")) continue;
                    }
                    var id = item.GetProperty(provider == TranslationProvider.Gemini ? "name" : "id").GetString();
                    if (id is null || id.Length is < 1 or > 256 || id.Any(char.IsControl)) throw new JsonException();
                    if (provider == TranslationProvider.Gemini && id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
                    if (id.Length == 0) throw new JsonException();
                    models.Add(id); if (models.Count > 5000) throw new TranslationException("모델 목록 항목 수 제한을 초과했습니다.");
                }
                if (provider != TranslationProvider.Gemini || !root.TryGetProperty("nextPageToken", out var cursor))
                    return models.Order(StringComparer.Ordinal).Select(id => new ProviderModel(id)).ToArray();
                next = cursor.GetString();
                if (string.IsNullOrEmpty(next)) return models.Order(StringComparer.Ordinal).Select(id => new ProviderModel(id)).ToArray();
                if (next.Length > 4096 || !tokens.Add(next)) throw new JsonException();
            }
            throw new TranslationException("모델 목록 페이지 제한을 초과했습니다. 공식 모델 문서를 확인하세요.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TranslationException("모델 목록 조회가 15초 안에 완료되지 않았습니다. 다시 조회해 주세요."); }
        catch (HttpRequestException) { throw new TranslationException("공식 모델 목록 서버에 연결할 수 없습니다. 네트워크 상태를 확인하세요."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { throw new TranslationException("공식 모델 목록 응답 형식을 읽을 수 없습니다. 공식 문서에서 모델 ID를 확인하세요."); }
    }
}
