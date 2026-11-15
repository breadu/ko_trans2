using System.Text;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

internal static class OpenAiStream
{
    public static async Task<string> ReadAsync(Stream source, Action<string>? progress, CancellationToken token,
        Func<string, bool, string>? normalize = null)
    {
        var text = new StringBuilder(); long lastProgress = 0;
        await foreach (var value in ServerSentEvents.ReadAsync(source,token).ConfigureAwait(false))
        {
            if (value == "[DONE]") break;
            using var json = JsonDocument.Parse(value); var item = json.RootElement;
            var type = item.GetProperty("type").GetString();
            if (type == "response.output_text.delta")
            {
                text.Append(item.GetProperty("delta").GetString());
                token.ThrowIfCancellationRequested();
                if (lastProgress == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 40)
                {
                    var partial = text.ToString();
                    if (normalize is not null) partial = normalize(partial, false);
                    if (!string.IsNullOrWhiteSpace(partial))
                    { progress?.Invoke(partial); lastProgress = System.Diagnostics.Stopwatch.GetTimestamp(); }
                }
            }
            else if (type == "response.completed")
            {
                var response = item.GetProperty("response");
                if (response.GetProperty("status").GetString() != "completed") break;
                var final = string.Join("\n", response.GetProperty("output").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "message")
                    .SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.GetProperty("type").GetString() == "output_text").Select(x => x.GetProperty("text").GetString()));
                if (normalize is not null) final = normalize(final, true);
                final = final.Trim();
                if (final.Length == 0) throw new TranslationException("AI가 번역문을 반환하지 않았습니다.");
                token.ThrowIfCancellationRequested(); return final;
            }
            else if (type is "error" or "response.failed" or "response.incomplete") break;
        }
        throw new TranslationException("번역 스트림이 완료 전에 중단되었습니다. 부분 번역은 문맥에 저장하지 않았습니다. 자동 재요청하지 않습니다.");
    }
}
