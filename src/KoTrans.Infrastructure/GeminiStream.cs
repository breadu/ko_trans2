using System.Text;
using System.Text.Json;
using KoTrans.Core;
namespace KoTrans.Infrastructure;

internal static class GeminiStream
{
    private static bool IsPrimary(JsonElement candidate)
    {
        if(!candidate.TryGetProperty("index",out var index))return true;
        if(index.ValueKind!=JsonValueKind.Number || !index.TryGetInt32(out int value))throw new TranslationException("Gemini 응답 후보 번호를 확인할 수 없습니다. 자동 재요청하지 않았습니다.");
        return value==0;
    }
    internal static async Task<string> ReadAsync(Stream source,Action<string>? progress,CancellationToken token)
    {
        var text=new StringBuilder();long lastProgress=0;
        await foreach(var data in ServerSentEvents.ReadAsync(source,token).ConfigureAwait(false)) {
            if(data=="[DONE]") break;
            using var json=JsonDocument.Parse(data);var root=json.RootElement;
            if(root.TryGetProperty("error",out _))throw new TranslationException("Gemini 번역 스트림이 서버 오류로 중단되었습니다. 자동 재요청하지 않았습니다.");
            if(root.TryGetProperty("promptFeedback",out var feedback) && feedback.TryGetProperty("blockReason",out var block)
                && block.GetString() is {} blocked && blocked is not ("" or "BLOCK_REASON_UNSPECIFIED"))
                throw new TranslationException("Gemini가 입력 처리를 제한했습니다. 해당 모델의 지원 범위를 확인하세요. 자동 재요청하지 않았습니다.");
            if(!root.TryGetProperty("candidates",out var candidates) || candidates.GetArrayLength()==0)continue;
            // Never combine alternatives into a single translation.
            var candidate=candidates.EnumerateArray().FirstOrDefault(IsPrimary);
            if(candidate.ValueKind==JsonValueKind.Undefined)continue;
            string? finish=candidate.TryGetProperty("finishReason",out var reason)?reason.GetString():null;
            if(!string.IsNullOrEmpty(finish) && finish is not ("STOP" or "FINISH_REASON_UNSPECIFIED"))
                throw new TranslationException("Gemini 응답이 완료되지 않았습니다. 모델의 처리 제한 또는 출력 제한을 확인하세요. 자동 재요청하지 않았습니다.");
            int previous=text.Length;
            if(candidate.TryGetProperty("content",out var content) && content.TryGetProperty("parts",out var parts))
                foreach(var part in parts.EnumerateArray()) {
                    if(part.TryGetProperty("thought",out var thought)) {
                        if(thought.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new TranslationException("Gemini 응답 형식을 확인할 수 없습니다. 자동 재요청하지 않았습니다.");
                        if(thought.ValueKind==JsonValueKind.True)continue;
                    }
                    if(part.TryGetProperty("text",out var value))text.Append(value.GetString());
                }
            token.ThrowIfCancellationRequested();
            if(text.Length!=previous && (lastProgress==0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds>=40)) {
                var partial=text.ToString();
                if(!string.IsNullOrWhiteSpace(partial)){progress?.Invoke(partial);lastProgress=System.Diagnostics.Stopwatch.GetTimestamp();}
            }
            token.ThrowIfCancellationRequested();
            if(finish=="STOP") {
                var complete=text.ToString().Trim();
                if(complete.Length==0)throw new TranslationException("Gemini가 번역문을 반환하지 않았습니다.");
                return complete;
            }
        }
        throw new TranslationException("Gemini 번역 스트림이 완료 전에 중단되었습니다. 부분 번역은 문맥에 저장하지 않았습니다. 자동 재요청하지 않습니다.");
    }
}
