using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class NvlLayoutTests
{
    private static int count;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
    internal static async Task Run()
    {
        OcrLine Row(string text, int x, int y, int width) => new(text, 1, new(x, y, width, 20));
        string[] Plan(string language, params OcrLine[] rows) {
            string source = NvlText.Canonical(string.Join("\n", rows.Select(r => r.Text)), language);
            var blocks = NvlTranslationLayout.Plan(source, rows, language, 0);
            Check(string.Concat(blocks.Select(b => b.Source)) == source, "Layout partition changed source characters");
            return blocks.Select(b => b.Source.Trim()).ToArray();
        }
        Check(Plan("ja", Row("長い文章の",20,20,280), Row("続きです。",20,50,100), Row("「次の会話」",20,80,180))
            .SequenceEqual(new[] { "長い文章の続きです。", "「次の会話」" }), "Visual wrapping became a paragraph break");
        Check(Plan("en", Row("This is a long",20,20,280), Row("sentence.",20,50,100), Row("\"Another dialogue\"",20,80,240))
            .SequenceEqual(new[] { "This is a long sentence.", "\"Another dialogue\"" }), "English wrapping lost a word space");
        Check(Plan("ja", Row("最初の文。",20,20,280), Row("同じ段落の次の文。",20,50,270)).Length == 1,
            "A period exactly at the visual right edge created a guessed paragraph");
        Check(Plan("ja", Row("見出し",20,20,80), Row("まだ途中の説明",20,110,220))
            .SequenceEqual(new[] { "見出し", "まだ途中の説明" }), "Large vertical paragraph gap was discarded");
        Check(Plan("ja", Row("短い段落",20,20,100), Row("新しく字下げした段落",50,50,250)).Length == 2,
            "Short final line plus a new indentation was not recognized");
        Check(Plan("ja", Row("「一つ目」「二つ目」",20,20,280)).SequenceEqual(new[] { "「一つ目」", "「二つ目」" }),
            "Adjacent complete dialogue blocks on one physical row were flattened");
        Check(Plan("ja", Row("「閉じていない",20,20,280), Row("会話。続き」",20,50,270)).Length == 1,
            "Wrapped open dialogue was split at its internal punctuation");
        Check(Plan("ja", Row("本文。",20,20,100), Row("VO",20,50,45)).Length == 1,
            "An ambiguous marker-like tail became its own request");
        var sameRow = new[] { Row("前半",20,20,80) with { Paragraph = 3 }, Row("後半",110,21,180) with { Paragraph = 9 }, Row("続き。",20,50,260) };
        string joined = NvlText.Canonical(NvlText.MergeVisualRows(sameRow, "ja").Select(r => r.Text).Aggregate((a,b) => a+"\n"+b), "ja");
        Check(NvlTranslationLayout.Plan(joined, sameRow, "ja", 0).Count == 1, "Detector paragraph IDs or same-row fragments created false breaks");
        // Layout inferences cannot freeze an unfinished suffix; only the proven paragraph prefix is reused.
        var heading = Row("見出し",20,20,80);
        var partial = Row("まだ途中",20,110,220);
        var page = new NvlTranslationPage();
        string first = "見出しまだ途中";
        page.Begin(first, false);
        var initial = NvlTranslationLayout.Plan(first, [heading, partial], "ja", 0);
        Check(initial.Count == 2 && initial[0].Complete && !initial[1].Complete, "Geometry froze an unfinished last paragraph");
        page.Commit(initial[0].Source, "제목", paragraphBoundary: initial[0].Complete);
        string expanded = "見出しまだ途中から完成。";
        page.Begin(expanded, false);
        var continuation = NvlTranslationLayout.Plan(expanded, [heading, partial with { Text = "まだ途中から完成。" }], "ja", page.FixedSource.Length);
        Check(continuation.Count == 1 && continuation[0].Source == "まだ途中から完成。"
            && page.Compose("완성된 본문") == "제목\n완성된 본문", "Inferred paragraph prefix was repeated during incremental replacement");
        var mismatched = NvlTranslationLayout.Plan("本文。次の文。", [Row("別の検出結果",20,20,80)], "ja", 0);
        Check(string.Concat(mismatched.Select(b => b.Source)) == "本文。次の文。" && mismatched.Count == 2,
            "Mismatched OCR geometry changed source or prevented textual fallback");
        foreach (var provider in new[] { TranslationProvider.OpenAI, TranslationProvider.Gemini, TranslationProvider.DeepL, TranslationProvider.HyMtLocal, TranslationProvider.Ollama })
            await FullPage(provider, false);
        await FullPage(TranslationProvider.Ollama, true);
        foreach (string output in new[] { "합쳐진 전체 번역", "첫 문단\n\n둘째 문단\n\n추가 내용" }) await UnmatchedOutput(output);
        Check(NvlTranslationBatch.Split("첫 문단\r\n\r\n둘째 문단", 2)!.SequenceEqual(new[] { "첫 문단", "둘째 문단" }), "CRLF paragraph response was not retained");
        Check(NvlTranslationBatch.Split("첫 문단\n둘째 문단", 2)!.Length == 2, "Single-newline response was rejected");
        Check(NvlTranslationBatch.Split("전체 번역", 2) is null, "Flattened output was arbitrarily assigned to source paragraphs");
        Console.WriteLine($"PASS: {count} NVL logical-paragraph/full-retranslation assertions. Synthetic layout and Mock AI only.");
    }
    private static async Task FullPage(TranslationProvider provider, bool specialProtocol)
    {
        var ocr = new Ocr(); var ai = new Ai(); var context = new TranslationSession();
        var options = new TranslationOptions { Provider = provider, Model = "test-model" };
        if (specialProtocol) options.OllamaModels = options.OllamaModels.SetItem(options.Model, new(OllamaRequestFormat.TranslateGemma));
        var frame = ocr.Frame(new("長い文章の",1,new(20,20,280,20)), new("続きです。",1,new(20,50,100,20)), new("「次の会話」",1,new(20,80,180,20)));
        var updates = new ConcurrentQueue<PipelineUpdate>();
        await using var pipe = new TranslationPipeline(ocr, ai, _ => Task.FromResult(frame), new("ja","NVL",false), options, context, "test");
        pipe.Updated += updates.Enqueue; pipe.Start(false);
        const string expected = "T:長い文章の続きです。\nT:「次の会話」";
        async Task Send(bool force) {
            int before = updates.Count;
            await pipe.RequestManualAsync(force);
            await Until(() => updates.Skip(before).Any(u => u.Translation == expected), "Full NVL translation lost its paragraph breaks");
        }
        await Send(false);
        Check(ai.Calls.Count == 1 && ai.Calls.Single().Text == "長い文章の続きです。\n\n「次の会話」", "Whole-page paragraphs were not batched into exactly one request");
        Check(ai.Calls.All(r => r.ParagraphCount == 2 && r.SystemPrompt.Contains("exactly 2 translated paragraphs") && r.Progress is null), "Paragraph formatting contract or atomic display was lost");
        await Send(true);
        Check(ai.Calls.Count == 2 && context.History.Count == 1, "Forced full-page translation duplicated its history or issued extra calls");
        Check(ai.Calls.ElementAt(1).History.Count == 0, "Forced retry used the old full-page translation as dialogue context");
        Check(ai.Calls.All(r => r.History.Count == 0), "A batch supplied its own paragraphs as previous dialogue");
        await Send(false);
        Check(ai.Calls.Count == 2 && context.History.Single().Translation == expected, "Cached full-page result lost breaks or sent another AI request");
    }
    private static async Task UnmatchedOutput(string output)
    {
        var ocr = new Ocr(); var ai = new Ai { Output = output }; var context = new TranslationSession();
        var frame = ocr.Frame(new("確定。",1,new(20,20,120,20)), new("まだ途中",1,new(20,50,240,20)));
        var updates = new ConcurrentQueue<PipelineUpdate>();
        await using var pipe = new TranslationPipeline(ocr, ai, _ => Task.FromResult(frame), new("ja","NVL",false), new(), context, "test");
        pipe.Updated += updates.Enqueue; pipe.Start(false);
        await pipe.RequestManualAsync();
        await Until(() => updates.Any(u => u.Translation == output), "Malformed paragraph output was discarded or truncated");
        await pipe.RequestManualAsync(); await Task.Delay(60);
        Check(ai.Calls.Count == 1 && context.History.Single().Translation == output, "Format mismatch issued a repair call or lost cached output");
    }
    private static async Task Until(Func<bool> done, string message)
    { var clock = Stopwatch.StartNew(); while (!done()) { if (clock.ElapsedMilliseconds > 4000) throw new Exception(message); await Task.Delay(5); } }
    private sealed class Ocr : IOcrService
    {
        private readonly Dictionary<ImageFrame,OcrResult> results = new();
        public OcrState State => OcrState.Ready;
        public ImageFrame Frame(params OcrLine[] rows) { var frame = new ImageFrame(400,200,1600,new byte[320000]); results.Add(frame,new(rows,TimeSpan.Zero)); return frame; }
        public Task InitializeAsync(OcrOptions options,CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token) => Task.FromResult(results[frame]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        internal readonly ConcurrentQueue<TranslationRequest> Calls = new();
        internal string? Output;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken token)
        { Calls.Enqueue(request); return Task.FromResult(new TranslationResult(Output ?? string.Join("\n\n", request.Text.Split("\n\n").Select(p => "T:" + p)),TimeSpan.Zero,0)); }
    }
}
