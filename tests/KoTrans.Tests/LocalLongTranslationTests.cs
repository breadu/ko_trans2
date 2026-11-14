using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class LocalLongTranslationTests
{
    private static int count;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { count++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");
    private static TranslationRequest Request(string text, LocalTranslationModel model = LocalTranslationModel.Standard7B)
        => new(text, "CLOUD_ONLY", [], new() { Provider = TranslationProvider.HyMtLocal, LocalModel = model });
    private sealed record Call(string Source, string Prompt, LocalTranslationModel Model);
    private sealed class Runtime : ILocalModelRuntime
    {
        internal readonly List<Call> Calls = [], Accepted = [];
        internal Func<Call, CancellationToken, Task<string>> Reply = (call, _) => Task.FromResult(call.Source);
        public string Backend => "CPU";
        public Task PrepareAsync(string path, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) => throw new Exception("Must reuse prepared model");
        public Task UnloadAsync(CancellationToken token, LocalTranslationModel? selection = null) => throw new Exception("Must not unload between chunks");
        public async Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
        {
            token.ThrowIfCancellationRequested();
            string suffix = selection == LocalTranslationModel.Standard7B ? HyMtPrompt.Suffix : "<｜hy_Assistant｜>";
            string source = prompt[(prompt.LastIndexOf("[Source Text]\n", StringComparison.Ordinal) + "[Source Text]\n".Length)..^suffix.Length];
            var call = new Call(source, prompt, selection); Calls.Add(call);
            var text = await Reply(call, token); Accepted.Add(call);
            return new(text, TimeSpan.FromMilliseconds(1), 0);
        }
    }

    internal static async Task Run()
    {
        foreach (var model in Enum.GetValues<LocalTranslationModel>()) {
            var runtime = new Runtime(); var service = new HyMtTranslationService(runtime, model);
            var shortRequest = Request("短い会話。", model);
            Check((await service.TranslateAsync(shortRequest, default)).Text == shortRequest.Text && runtime.Calls.Count == 1,
                "Short dialogue no longer uses one local inference");
            string paragraph = string.Concat(Enumerable.Repeat("佐藤は窓の外を見た。静かな夜だった。", 23));
            string source = paragraph + "\n\n「佐藤、明日は一緒に行こう。」\n\n" + paragraph;
            runtime.Calls.Clear(); runtime.Accepted.Clear(); int progress = 0;
            var history = new[] { new ConversationTurn("直前の背景。", "OLD_TRANSLATION") };
            var request = Request(source, model) with { History = history, ParagraphCount = 3,
                Characters = [new("佐藤", "사토", "BIOGRAPHY")], Progress = _ => progress++ };
            var result = await service.TranslateAsync(request, default);
            Check(Compact(result.Text) == Compact(source) && result.Text.Split("\n\n").Length == 3,
                "Long page lost, duplicated, reordered text or paragraph boundaries");
            Check(runtime.Calls.Count > 3 && runtime.Calls.All(c => c.Source.Length <= HyMtTranslationService.ChunkCharacters && c.Model == model),
                "Chunk/model selection limit was bypassed");
            Check(runtime.Calls.Where(c => c.Source.Contains("佐藤")).All(c => c.Prompt.Contains("\"佐藤\" translates to \"사토\"")),
                "Chunk lost character dictionary terminology");
            Check(runtime.Calls.All(c => !c.Prompt.Contains("OLD_TRANSLATION") && !c.Prompt.Contains("BIOGRAPHY") && !c.Prompt.Contains("CLOUD_ONLY"))
                && runtime.Calls[0].Prompt.Contains("直前の背景。")
                && runtime.Calls[1].Prompt.Contains("[Background Information]\n"), "Chunk context was lost or polluted");
            Check(progress == 0 && result.HttpAttempts == 0 && history.Length == 1, "Partial page escaped or caller history was mutated");
        }

        string english = string.Join(" ", Enumerable.Repeat("Dr. Alice waited by the gate. She opened the door.", 20));
        var parts = LocalTranslationChunks.Split(english, 240);
        Check(string.Join(" ", parts.Select(c => c.Source)) == english && parts.All(c => c.Separator != "\n\n"),
            "English words/abbreviations were lost or invented paragraph breaks appeared");
        string unicode = string.Concat(Enumerable.Repeat("🌟か\u3099", 150));
        parts = LocalTranslationChunks.Split(unicode, 239);
        Check(string.Concat(parts.Select(c => c.Source)) == unicode && parts.All(c => !char.IsLowSurrogate(c.Source[0])
            && !char.IsHighSurrogate(c.Source[^1]) && c.Source[0] != '\u3099'), "Fallback split damaged Unicode text elements");
        string named = new string('あ', 239) + "佐藤美咲" + new string('い', 100);
        parts = LocalTranslationChunks.Split(named, 240, [new("佐藤美咲", "사토 미사키", "")]);
        Check(string.Concat(parts.Select(c => c.Source)) == named && parts.Count(c => c.Source.Contains("佐藤美咲")) == 1,
            "Fallback split severed a dictionary name");
        var fallback = new Runtime { Reply = (call, _) => call.Source.Length > 65
            ? throw new LocalTranslationLimitException(LocalTranslationLimit.Output) : Task.FromResult(call.Source) };
        string longSentence = new string('文', 220) + "。";
        Check(Compact((await new HyMtTranslationService(fallback).TranslateAsync(Request(longSentence), default)).Text) == longSentence
            && fallback.Accepted.Count > 1, "Output limit did not retry smaller complete chunks");

        var input = new Runtime { Reply = (call, _) => call.Prompt.Length > 1100
            ? throw new LocalTranslationLimitException(LocalTranslationLimit.Input) : Task.FromResult(call.Source) };
        string oversizedContext = new string('背', 3000) + "RECENT_CONTEXT";
        var contextual = Request("今の短い会話。") with { History = [new("OLD", ""), new(oversizedContext, "IGNORED")] };
        Check((await new HyMtTranslationService(input).TranslateAsync(contextual, default)).Text == contextual.Text
            && input.Accepted.Single().Prompt.Contains("RECENT_CONTEXT") && input.Calls.Count < 10,
            "Oversized history blocked a short dialogue or discarded its recent suffix unnecessarily");
        input = new Runtime { Reply = (call, _) => call.Source.Length > 70
            ? throw new LocalTranslationLimitException(LocalTranslationLimit.Input) : Task.FromResult(call.Source) };
        Check(Compact((await new HyMtTranslationService(input).TranslateAsync(Request(longSentence), default)).Text) == longSentence,
            "Input limit without history failed to split the source");
        var broken = new Runtime { Reply = (_, _) => throw new TranslationException("inference failure") };
        await Reject<TranslationException>(() => new HyMtTranslationService(broken).TranslateAsync(Request(longSentence), default));
        Check(broken.Calls.Count == 1, "Unrelated inference error caused retry storm");
        var impossible = new Runtime { Reply = (_, _) => throw new LocalTranslationLimitException(LocalTranslationLimit.Output) };
        await Reject<LocalTranslationLimitException>(() => new HyMtTranslationService(impossible).TranslateAsync(Request(longSentence), default));
        Check(impossible.Calls.Count < 10, "Irreducible limit loop was not bounded");
        var invalid = new Runtime(); var invalidService = new HyMtTranslationService(invalid);
        await Reject<TranslationException>(() => invalidService.TranslateAsync(Request(new string('x', 239) + "<|extra_0|>"), default));
        await Reject<TranslationException>(() => invalidService.TranslateAsync(Request(new string('x', 16001)), default));
        Check(invalid.Calls.Count == 0, "Partitioning bypassed full-input validation");
        Check((await invalidService.TranslateAsync(Request("短い会話。" + new string(' ', 300)), default)).Text == "短い会話。"
            && invalid.Calls.Count == 1, "Whitespace padding caused a false oversized-input failure");
        using (var cancelled = new CancellationTokenSource()) {
            var late = new Runtime { Reply = (call, _) => { cancelled.Cancel(); return Task.FromResult(call.Source); } };
            await Reject<OperationCanceledException>(() => new HyMtTranslationService(late).TranslateAsync(Request(english), cancelled.Token));
            Check(late.Calls.Count == 1, "Cancellation started the next chunk or published a late result");
        }
        foreach (string mode in new[] { "NVL", "ADV" }) {
            foreach (var model in Enum.GetValues<LocalTranslationModel>()) {
                await Pipeline(mode, false, model);
                await Pipeline(mode, true, model);
            }
        }
        await LightweightParagraphs();
        Console.WriteLine($"PASS: {count} local long-text/chunk/limit/context/Unicode/atomic NVL+ADV assertions. Mock only.");
    }

    private static async Task LightweightParagraphs()
    {
        const LocalTranslationModel light = LocalTranslationModel.Lightweight18B;
        const string first = "赤い箱を開けた。", second = "青い鍵を取り出した。", third = "白い扉を閉めた。";
        string source = first + "\n\n" + second + "\n\n" + third;
        var translations = new Dictionary<string, string> { [first] = "빨간 상자를 열었다.", [second] = "파란 열쇠를 꺼냈다.", [third] = "하얀 문을 닫았다." };
        var runtime = new Runtime { Reply = (call, _) => Task.FromResult(translations[call.Source]) };
        var service = new HyMtTranslationService(runtime, light);
        var history = new[] { new ConversationTurn("昨日は駅へ行った。", "NEVER_OUTPUT_HISTORY") };
        var result = await service.TranslateAsync(Request(source, light) with { History = history, ParagraphCount = 3 }, default);
        Check(runtime.Calls.Select(c => c.Source).SequenceEqual(new[] { first, second, third })
            && result.Text == string.Join("\n\n", translations.Values), "Lightweight paragraphs were not translated and joined in source order");
        Check(runtime.Calls.All(c => !c.Prompt.Contains("NEVER_OUTPUT_HISTORY") && c.Model == light)
            && runtime.Calls[1].Prompt.Contains("[Background Information]\n昨日は駅へ行った。")
            && runtime.Calls[1].Prompt.Contains(first), "Lightweight paragraph translation lost original-only context");
        runtime.Calls.Clear();
        // ADV may supply explicit paragraphs without ParagraphCount metadata.
        result = await service.TranslateAsync(Request(first + "\r\n \r\n" + second, light), default);
        Check(runtime.Calls.Count == 2 && result.Text == translations[first] + "\n\n" + translations[second],
            "Lightweight ADV/CRLF paragraph boundaries were ignored");
        runtime = new Runtime(); service = new HyMtTranslationService(runtime, light);
        foreach (string visual in new[] { first + "\n" + second, first + "\r\n" + second, first + "\n\n  " }) {
            runtime.Calls.Clear();
            await service.TranslateAsync(Request(visual, light), default);
            Check(runtime.Calls.Count == 1 && runtime.Calls[0].Source == visual, "Visual wrapping or trailing whitespace split a single paragraph");
        }
        runtime.Calls.Clear();
        await new HyMtTranslationService(runtime).TranslateAsync(Request(source) with { ParagraphCount = 3 }, default);
        Check(runtime.Calls.Count == 1 && runtime.Calls[0].Source == source && runtime.Calls[0].Prompt.Contains("exactly 3 translated paragraphs"),
            "Lightweight policy changed standard model batching");
        using var cancelled = new CancellationTokenSource();
        runtime = new Runtime { Reply = (call, _) => { cancelled.Cancel(); return Task.FromResult(call.Source); } };
        await Reject<OperationCanceledException>(() => new HyMtTranslationService(runtime, light).TranslateAsync(Request(source, light), cancelled.Token));
        Check(runtime.Calls.Count == 1, "Cancelled lightweight paragraph batch continued inference");
    }

    private static async Task Pipeline(string mode, bool fail, LocalTranslationModel model)
    {
        string source = string.Concat(Enumerable.Repeat("窓の外では静かに雨が降っていた。", 32));
        var runtime = new Runtime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        runtime.Reply = async (call, token) => {
            if (++calls == 2) { entered.TrySetResult(); await resume.Task.WaitAsync(token); if (fail) throw new TranslationException("mock failure"); }
            return call.Source;
        };
        var session = new TranslationSession();
        var updates = new ConcurrentQueue<PipelineUpdate>();
        var frame = new ImageFrame(400,200,1600,new byte[320000]);
        await using var pipeline = new TranslationPipeline(new Ocr(source), new HyMtTranslationService(runtime, model), _ => Task.FromResult(frame),
            new("ja", mode, false), new() { Provider = TranslationProvider.HyMtLocal, LocalModel = model }, session, "test");
        pipeline.Updated += updates.Enqueue; pipeline.Start(false);
        await pipeline.RequestManualAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(session.History.Count == 0 && updates.All(u => string.IsNullOrEmpty(u.Translation)),
            mode + " published or cached a partly translated page");
        resume.TrySetResult();
        await Until(() => fail ? updates.Any(u => u.State == PipelineState.Error)
            : updates.Any(u => !string.IsNullOrEmpty(u.Translation) && Compact(u.Translation) == source));
        if (fail) Check(session.History.Count == 0 && updates.All(u => string.IsNullOrEmpty(u.Translation)), mode + " committed partial work after failure");
        else {
            Check(session.History.Count == 1 && Compact(session.History.Single().Translation) == source, mode + " committed chunk history separately");
            int before = calls; await pipeline.RequestManualAsync();
            await Until(() => updates.Any(u => u.CacheHits > 0));
            Check(calls == before, mode + " did not reuse the complete translation cache");
        }
    }
    private static async Task Until(Func<bool> condition)
    { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.ElapsedMilliseconds > 5000) throw new Exception("Local pipeline did not settle"); await Task.Delay(10); } }
    private sealed class Ocr(string source) : IOcrService
    {
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
            => Task.FromResult(new OcrResult([new(source, 1, new(20,20,350,20))], TimeSpan.Zero));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
