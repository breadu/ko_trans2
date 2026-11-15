using System.Net;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class OpenAiNewlineTests
{
    private static int count;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); count++; }
    private static TranslationRequest Request(Action<string>? progress = null) => new("First line.\nSecond line.", "Translate into Korean.",
        [new("Earlier line.", "이전 문장.")], new() { Provider = TranslationProvider.OpenAI, Model = "synthetic-model" }, Progress: progress);
    private static object Response(string text) => new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } };
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Delta(string text) => Event(new { type = "response.output_text.delta", delta = text });
    private static string Done(string text) => Event(new { type = "response.completed", response = Response(text) });

    internal static async Task Run()
    {
        var cases = new[] {
            (@"첫째 줄\n둘째 줄", "첫째 줄\n둘째 줄"),
            (@"첫 문단\n\n둘째 문단", "첫 문단\n\n둘째 문단"),
            (@"첫째 줄\r\n둘째 줄", "첫째 줄\n둘째 줄"),
            ("첫째 줄\r\n둘째 줄\n셋째 줄", "첫째 줄\r\n둘째 줄\n셋째 줄"),
            ("첫째 줄\n둘째 줄\\n셋째 줄", "첫째 줄\n둘째 줄\n셋째 줄"),
            (@"「안녕.\n반가워。」", "「안녕.\n반가워。」"),
            (@"첫째🐈\n둘째😀", "첫째🐈\n둘째😀"),
            (@"C:\new\notes.txt 파일", @"C:\new\notes.txt 파일"),
            (@"assets\name.txt", @"assets\name.txt"),
            (@"\\nas\notes", @"\\nas\notes"),
            (@"첫째\\n둘째", @"첫째\\n둘째"),
            (@"인용한 '\n' 표기", @"인용한 '\n' 표기"),
            (@"인용한 「\n」 표기", @"인용한 「\n」 표기"),
            (@"`첫째\n둘째`", @"`첫째\n둘째`"),
            ("{\"value\":\"첫째\\n둘째\"}", "{\"value\":\"첫째\\n둘째\"}"),
            (@"기호\t탭\u1234문자\r유지", @"기호\t탭\u1234문자\r유지"),
            (@"영문 ID\n다음 줄", @"영문 ID\n다음 줄"),
            (@"다음\nname.txt", @"다음\nname.txt")
        };
        foreach (bool streaming in new[] { false, true }) foreach (var (raw, expected) in cases) {
            var progress = new List<string>();
            using var handler = new Handler(streaming ? Delta(raw) + Done(raw) : JsonSerializer.Serialize(Response(raw)), streaming);
            using var client = new HttpClient(handler);
            var request = Request(streaming ? progress.Add : null);
            var result = await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(request, default);
            Check(result.Text == expected && result.HttpAttempts == 1 && handler.Calls == 1, "Newline conversion damaged text or retried");
            if (streaming) Check(progress.Single() == expected, "Streaming/final newline policy differs");
            using var sent = JsonDocument.Parse(handler.Body!);
            Check(sent.RootElement.GetProperty("instructions").GetString() == request.SystemPrompt
                && sent.RootElement.GetProperty("model").GetString() == request.Options.Model
                && sent.RootElement.GetProperty("input")[0].GetProperty("content").GetString() == TranslationPrompt.Input(request),
                "Newline correction changed request/prompt/history");
        }
        foreach (string source in new[] { @"Print \n literally.", @"C:\new\notes.txt", "`line break`", "{\"line\":1}" }) {
            const string raw = @"첫째\n둘째";
            using var handler = new Handler(Delta(raw) + Done(raw), true); using var client = new HttpClient(handler);
            var progress = new List<string>();
            var result = await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(Request(progress.Add) with { Text = source }, default);
            Check(result.Text == raw && progress.Single() == raw, "Source literal/code guard did not preserve notation");
        }
        await SplitEscapes();
        await ScopeAndFailure();
        Console.WriteLine($"PASS: {count} assertions (OpenAI newline, streaming, literal safety, JSON/provider scope). Mock HTTP only.");
    }

    private static async Task SplitEscapes()
    {
        foreach (var pieces in new[] { new[] { "첫째\\", "n둘째" }, new[] { "첫째\\r", "\\", "n둘째" }, new[] { @"첫째\n", "둘째" } }) {
            string raw = string.Concat(pieces);
            var progress = new List<string>();
            using var handler = new Handler(pieces.Select(Delta).Append(Done(raw)).ToArray()); using var client = new HttpClient(handler);
            var result = await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(Request(progress.Add), default);
            Check(result.Text == "첫째\n둘째" && progress.Last() == result.Text, "Split newline was lost or final differed");
            Check(progress.First() == "첫째" && progress.All(p => !p.Contains('\\')), "Incomplete escape flashed in a subtitle");
        }
        using (var handler = new Handler(new[] { Delta("첫째\\"), Done("첫째\\") }))
        using (var client = new HttpClient(handler)) {
            var result = await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(Request(_ => { }), default);
            Check(result.Text == "첫째\\", "Authoritative final response lost a trailing literal backslash");
        }
        using (var handler = new Handler(JsonSerializer.Serialize(Response(@"첫 문단\n\n둘째 문단")), false))
        using (var client = new HttpClient(handler)) {
            var result = await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(Request() with { ParagraphCount = 2 }, default);
            Check(NvlTranslationBatch.Split(result.Text, 2)?.SequenceEqual(new[] { "첫 문단", "둘째 문단" }) == true,
                "NVL paragraph boundaries were not available before caching/splitting");
        }
    }

    private static async Task ScopeAndFailure()
    {
        const string json = """{"full_translation":"첫째\n둘째","sentence_explanation":null,"vocabulary":[]}""";
        foreach (bool streaming in new[] { false, true }) {
            using var handler = new Handler(streaming ? Delta(json) + Done(json) : JsonSerializer.Serialize(Response(json)), streaming);
            using var client = new HttpClient(handler);
            var progress = new List<string>();
            var result = await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(Request(progress.Add) with { OutputSchema = ExplanationPrompt.Schema }, default);
            Check(result.Text == json && StudyJson.Parse(result.Text).FullTranslation == "첫째\n둘째", "Study JSON was unescaped twice");
            if (streaming) Check(progress.Single() == json, "Structured streaming data was modified");
        }
        const string raw = @"첫째\n둘째";
        foreach (var provider in new[] { TranslationProvider.Gemini, TranslationProvider.Ollama, TranslationProvider.DeepL }) {
            object response = provider switch {
                TranslationProvider.Gemini => new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = raw } } } } } },
                TranslationProvider.Ollama => new { done = true, message = new { content = raw } },
                _ => new { translations = new[] { new { text = raw } } }
            };
            using var handler = new Handler(JsonSerializer.Serialize(response), false); using var client = new HttpClient(handler);
            ITranslationService service = provider switch {
                TranslationProvider.Gemini => new GeminiTranslationService(client, "synthetic-key"),
                TranslationProvider.Ollama => new OllamaTranslationService(client),
                _ => new DeepLTranslationService(client, "synthetic-key")
            };
            var result = await service.TranslateAsync(Request() with { SourceLanguage = "en", Options = Request().Options with { Provider = provider } }, default);
            Check(result.Text == raw && handler.Calls == 1, "OpenAI-only correction changed another provider");
        }
        foreach (bool cancel in new[] { false, true }) {
            using var token = new CancellationTokenSource();
            using var handler = new Handler(Delta(raw) + (cancel ? Done(raw) : ""), true); using var client = new HttpClient(handler);
            bool rejected = false;
            try { await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(Request(_ => { if (cancel) token.Cancel(); }), token.Token); }
            catch (OperationCanceledException) when (cancel) { rejected = true; }
            catch (TranslationException) when (!cancel) { rejected = true; }
            Check(rejected && handler.Calls == 1, "Correction accepted interrupted/canceled output or retried");
        }
        using (var handler = new Handler(Delta(@"\n") + Done(@"\n"), true))
        using (var client = new HttpClient(handler)) {
            int callbacks = 0; bool rejected = false;
            try { await new OpenAiTranslationService(client, "synthetic-key").TranslateAsync(Request(_ => callbacks++), default); }
            catch (TranslationException) { rejected = true; }
            Check(rejected && callbacks == 0, "Empty converted output cleared subtitles or completed successfully");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly string[] parts; private readonly bool streaming, delayed;
        internal string? Body; internal int Calls;
        internal Handler(string body, bool streaming) { parts = [body]; this.streaming = streaming; }
        internal Handler(string[] parts) { this.parts = parts; streaming = delayed = true; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token); Calls++;
            HttpContent content = delayed ? new StreamContent(new DelayedChunks(parts)) : new StringContent(parts[0], Encoding.UTF8);
            content.Headers.ContentType = new(streaming ? "text/event-stream" : "application/json");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private sealed class DelayedChunks(string[] parts) : Stream
    {
        private int index;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (index == parts.Length) return 0;
            await Task.Delay(55, token);
            var bytes = Encoding.UTF8.GetBytes(parts[index++]); bytes.CopyTo(buffer); return bytes.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
