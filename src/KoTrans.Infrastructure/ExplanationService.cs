using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// Reuses the provider transport, bounded HTTP reader, safe errors, retry and timeout policy. No translation history.
public sealed class ExplanationService(ITranslationService transport) : IExplanationService, ITranslationDiagnostics
{
    public int HttpAttempts => (transport as ITranslationDiagnostics)?.HttpAttempts ?? 0;
    private static void RequireStudyProvider(TranslationOptions options)
    {
        if (!TranslationProviderCatalog.Get(options.Provider).SupportsStudy)
            throw new TranslationException("선택한 번역 제공자는 문장 해설·학습 분석을 지원하지 않습니다. 학습에는 AI 제공자를 선택하세요.");
    }
    public static IExplanationService Create(TranslationOptions options, string? key)
    {
        RequireStudyProvider(options);
        return new ExplanationService(TranslationServices.Create(options, key));
    }
    public static IExplanationService CreateUsingVault(TranslationOptions options, ApiKeyStore vault)
    {
        RequireStudyProvider(options);
        return new ExplanationService(TranslationServices.CreateUsingVault(options, vault));
    }
    public async Task<ExplanationResult> ExplainAsync(ExplanationRequest request, CancellationToken token)
    {
        var text = ExplanationPrompt.ValidateInput(request.Text);
        var ollama = request.Options.Provider == TranslationProvider.Ollama;
        var prompt = ollama ? ExplanationPrompt.BuildOllama(request.Language) : ExplanationPrompt.Build(request.Language);
        var response = await transport.TranslateAsync(new(text, prompt, [], request.Options, ExplanationPrompt.Schema, SourceLanguage: request.Language), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            var study = StudyJson.Parse(response.Text);
            if (ollama) StudyJson.ValidateSourceLanguage(study, request.Language);
            return new(study, response.Elapsed, response.HttpAttempts);
        }
        catch (TranslationException ex) when (ollama)
        {
            // Classify only an invalid study payload, never network/HTTP errors
            // or cancellation, as an unusable explanation from the model.
            throw new TranslationException($"Ollama · {request.Options.Model}\n현재 모델의 응답이 문장 해설 검사를 통과하지 못했습니다.\n"
                + ex.Message + "\n"
                + "번역 특화 모델은 번역이 가능해도 문장 해설을 지원하지 못할 수 있습니다. "
                + "번역 서비스에서 문장 해설·JSON 지시를 지원하는 대화형 모델을 선택하세요.");
        }
    }
}

// One serial analysis; a newer explicit request cancels waiting/old work. Cache is bounded and memory-only.
public sealed class ExplanationSession : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ExplanationResult> cache = [];
    private readonly List<Task> pending = [];
    private CancellationTokenSource? current;
    private string? activeKey;
    private Task<ExplanationResult?>? activeTask;
    private long version;
    private bool disposed;
    private Task? disposal;
    private int requests, hits, http;
    public int Requests => Volatile.Read(ref requests);
    public int CacheHits => Volatile.Read(ref hits);
    public int HttpAttempts => Volatile.Read(ref http);
    public Task<ExplanationResult?> AnalyzeAsync(Guid profile, ExplanationRequest request, IExplanationService service, bool force = false)
    {
        request = request with { Text = ExplanationPrompt.ValidateInput(request.Text), Options = request.Options with { } };
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { profile, request.Text, request.Language, request.Options.Provider, request.Options.Model, request.Options.OllamaAddress, OllamaFormat = request.Options.Provider == TranslationProvider.Ollama ? request.Options.SettingsForOllama().Format : (OllamaRequestFormat?)null, Version = request.Options.Provider == TranslationProvider.Ollama ? ExplanationPrompt.OllamaVersion : ExplanationPrompt.Version }))));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeKey == key && activeTask is { IsCompleted: false }) return activeTask;
            Cancel(); var serial = version;
            if (!force && cache.TryGetValue(key, out var value)) { Interlocked.Increment(ref hits); return Task.FromResult<ExplanationResult?>(value with { FromCache = true }); }
            var cancellation = new CancellationTokenSource(); current = cancellation; activeKey = key;
            activeTask = RunAsync(serial, key, request, service, cancellation);
            pending.RemoveAll(t => t.IsCompleted); pending.Add(activeTask); return activeTask;
        }
    }
    private async Task<ExplanationResult?> RunAsync(long serial, string key, ExplanationRequest request, IExplanationService service, CancellationTokenSource cancellation)
    {
        bool entered = false; int before = (service as ITranslationDiagnostics)?.HttpAttempts ?? 0;
        try
        {
            await gate.WaitAsync(cancellation.Token).ConfigureAwait(false); entered = true;
            cancellation.Token.ThrowIfCancellationRequested(); Interlocked.Increment(ref requests);
            var result = await service.ExplainAsync(request, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (service is not ITranslationDiagnostics) Interlocked.Add(ref http, result.HttpAttempts);
            lock (sync)
            {
                if (serial != version || disposed) return null;
                if (cache.Count >= 8) cache.Remove(cache.Keys.First()); cache[key] = result; return result;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) when (serial != Interlocked.Read(ref version)) { return null; }
        finally
        {
            if (entered) { if (service is ITranslationDiagnostics diagnostics) Interlocked.Add(ref http, diagnostics.HttpAttempts - before); gate.Release(); }
            lock (sync) { if (current == cancellation) { current = null; activeKey = null; } }
            cancellation.Dispose();
        }
    }
    public void Cancel() { lock (sync) { version++; current?.Cancel(); activeKey = null; } }
    public ValueTask DisposeAsync()
    {
        lock (sync) { if (disposal is not null) return new(disposal); disposed = true; Cancel(); disposal = DrainAsync(pending.ToArray()); return new(disposal); }
    }
    private async Task DrainAsync(Task[] tasks)
    { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { } finally { gate.Dispose(); cache.Clear(); } }
}

public sealed class MockExplanationService : IExplanationService
{
    public async Task<ExplanationResult> ExplainAsync(ExplanationRequest request, CancellationToken token)
    {
        ExplanationPrompt.ValidateInput(request.Text); await Task.Delay(800, token);
        var ja = request.Language == "ja";
        return new(new("[모의 학습 · 외부 API 미사용] " + (ja ? "내일 다시 만나요." : "여정은 한 걸음에서 시작됩니다."),
            ja ? "[모의 예시] また는 ‘다시’, 会いましょう는 만남을 제안하는 표현입니다." : "[모의 예시] begin with는 시작점을 나타냅니다. with 뒤 명사구가 옵니다.",
            [new(ja ? "明日" : "journey", ja ? "あした (아시타, 근사)" : "/ˈdʒɜːrni/ (저니, 근사)", ja ? "今日の次の日。" : "An act of travelling.", ja ? "내일" : "여행, 여정",
                [new(ja ? "明日また会いましょう。" : "Our journey starts today.", ja ? "내일 다시 만나요." : "우리의 여정은 오늘 시작됩니다.")], "모의 데이터로 화면 연결을 확인합니다. 입력에 대한 실제 분석이 아닙니다.")]), TimeSpan.FromMilliseconds(800));
    }
}
