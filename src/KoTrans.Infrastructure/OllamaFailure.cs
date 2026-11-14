using KoTrans.Core;

namespace KoTrans.Infrastructure;

// Fixed codes only: provider error bodies may contain prompts, URLs or local paths.
public enum OllamaFailureKind { None, Unknown, InternalConnectionClosed, RunnerUnavailable, OutOfMemory, ServerCancelled, ModelNotFound, RequestRejected }
public enum OllamaFailureStage { Unknown, Health, Tokenize, Template, Completion }

internal sealed class OllamaServerException(string message, int status, OllamaFailureKind kind, OllamaFailureStage stage)
    : TranslationException(message)
{
    public int Status { get; } = status;
    public OllamaFailureKind Kind { get; } = kind;
    public OllamaFailureStage Stage { get; } = stage;
}

internal static class OllamaFailure
{
    internal static OllamaServerException Classify(int status, string error)
    {
        var stage = InternalStage(error);
        bool reset = Has(error, "wsarecv", "forcibly closed", "connection reset")
            && Has(error, "127.0.0.1", "localhost", "[::1]");
        bool memory = Has(error, "out of memory", "not enough memory", "insufficient memory", "cudaErrorMemoryAllocation");
        var kind = memory ? OllamaFailureKind.OutOfMemory
            : reset ? OllamaFailureKind.InternalConnectionClosed
            : Has(error, "context canceled") ? OllamaFailureKind.ServerCancelled
            : Has(error, "model runner", "runner process") ? OllamaFailureKind.RunnerUnavailable
            : status == 404 ? OllamaFailureKind.ModelNotFound
            : status == 400 ? OllamaFailureKind.RequestRejected : OllamaFailureKind.Unknown;
        string message = kind switch {
            OllamaFailureKind.InternalConnectionClosed => $"Ollama 서버와 모델 실행기 사이의 연결이 끊겼습니다 (HTTP {status}). 실행기 종료나 메모리 부족이 확인된 것은 아닙니다. 번역을 중지한 뒤 다시 시작하세요.",
            OllamaFailureKind.RunnerUnavailable => "Ollama 모델 실행기의 응답을 받지 못했습니다. 서버가 실행기 오류를 반환했지만 종료·메모리 부족 여부는 확인되지 않았습니다. 번역을 중지한 뒤 다시 시작하세요.",
            OllamaFailureKind.OutOfMemory => "Ollama가 메모리 부족 오류를 반환했습니다. GPU VRAM/시스템 RAM과 모델 크기를 확인하세요.",
            OllamaFailureKind.ServerCancelled => "Ollama 서버 내부 작업이 취소되었습니다. 서버 상태와 모델 전환·작업 중단 여부를 확인하세요.",
            OllamaFailureKind.ModelNotFound => "Ollama 서버 주소 또는 모델을 찾을 수 없습니다. 올바른 서버에 해당 모델 ID가 설치되어 있는지 확인하세요.",
            OllamaFailureKind.RequestRejected => "Ollama가 요청 형식을 거부했습니다. 선택한 모델에 맞는 요청 방식과 문장 해설 지원 여부를 확인하세요.",
            _ => $"Ollama 서버 오류 (HTTP {status}). 서버가 처리하지 못했으며 정확한 원인은 Ollama 상태/로그에서 확인해야 합니다."
        };
        return new(message + " 추가 자동 재요청은 하지 않습니다.", status, kind, stage);
    }

    private static bool Has(string error, params string[] values) => values.Any(value => error.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static OllamaFailureStage InternalStage(string error)
    {
        // Go net/http's URL error prefixes identify an internal call, not arbitrary
        // mentions of an endpoint in the model response. Never retain the URL.
        int start = error.StartsWith("Post \"", StringComparison.Ordinal) ? 6
            : error.StartsWith("Get \"", StringComparison.Ordinal) ? 5 : -1;
        if (start < 0) return OllamaFailureStage.Unknown;
        int end = error.IndexOf("\":", start, StringComparison.Ordinal);
        if (end < 0 || !Uri.TryCreate(error[start..end], UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return OllamaFailureStage.Unknown;
        return uri.AbsolutePath switch {
            "/health" => OllamaFailureStage.Health,
            "/tokenize" when start == 6 => OllamaFailureStage.Tokenize,
            "/apply-template" => OllamaFailureStage.Template,
            "/completion" or "/v1/chat/completions" => OllamaFailureStage.Completion,
            _ => OllamaFailureStage.Unknown
        };
    }
}
