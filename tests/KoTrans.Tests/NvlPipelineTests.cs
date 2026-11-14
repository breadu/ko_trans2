using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class NvlPipelineTests
{
    private static int count;
    private static void Check(bool ok,string message) { if (!ok) throw new Exception(message); count++; }
    private static async Task Until(Func<bool> done,string message) {
        var clock=Stopwatch.StartNew(); while(!done()) { if(clock.ElapsedMilliseconds>3500) throw new Exception(message); await Task.Delay(5); }
    }
    private sealed class Ocr : IOcrService {
        public OcrState State=>OcrState.Ready;
        public bool ClipLocalResult;
        public readonly ConcurrentDictionary<ImageFrame,OcrResult> Results=new();
        public readonly ConcurrentQueue<OcrOptions> Calls=new();
        public ImageFrame Frame(params string[] lines) {
            var pixels=new byte[1600*300]; pixels[3]=(byte)(Results.Count+1);
            var boxes=lines.Select((s,i)=>new OcrLine(s,1,new(50,40+i*36,Math.Min(280,Math.Max(50,s.Length*12)),20))).ToArray();
            foreach(var line in boxes) for(int y=line.Bounds.Y+3;y<line.Bounds.Y+17;y++) for(int x=line.Bounds.X+3;x<line.Bounds.X+line.Bounds.Width-3;x++)
                pixels[y*1600+x*4]=(byte)(100+(line.Text[(x-line.Bounds.X)%line.Text.Length]%150));
            var frame=new ImageFrame(400,300,1600,pixels); Results[frame]=new(boxes,TimeSpan.Zero); return frame;
        }
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t) {
            Calls.Enqueue(o); var result=Results[f];
            if(o.SearchArea is { } r) {
                result=result with { Lines=result.Lines.Where(l=>l.Bounds.Y>=r.Y && l.Bounds.Y+l.Bounds.Height<=r.Y+r.Height).ToArray() };
                if(ClipLocalResult && result.Lines.Count>0)
                    result=result with {Lines=result.Lines.Select((line,index)=>index==0 ? line with {Bounds=line.Bounds with {Y=r.Y}} : line).ToArray()};
            }
            return Task.FromResult(result);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService {
        public readonly ConcurrentQueue<TranslationRequest> Calls=new();
        public Func<TranslationRequest,CancellationToken,Task<TranslationResult>>? Handler;
        public Task<TranslationResult> TranslateAsync(TranslationRequest r,CancellationToken t) {
            Calls.Enqueue(r); return Handler?.Invoke(r,t) ?? Task.FromResult(new TranslationResult(string.Join("\n\n", r.Text.Split("\n\n").Select(p => "T:" + p)),TimeSpan.Zero,0));
        }
    }
    internal static async Task Run() {
        var clock=Stopwatch.StartNew(); Rules(); TranslationEndings(); await DelimiterAccumulation(); await Sequence(); await Unstable(); await Superseded(); await ProvisionalReplacement(); await EnglishAndProtocolFallback(); await Presentation(); await ReflowWhileTranslating(); await ClearAndReturn(); await AnimatedCandidateJitter(); await AnimatedCandidateJitter(true); await AcceptedRowsSurviveIncrementalMerge(); await ShortTailRequests();
        await RejectedCropUsesFullSearch();
        await OllamaHyMtAccumulation();
        Console.WriteLine($"PASS: {count} NVL page/context/provisional assertions; {clock.ElapsedMilliseconds}ms. Mock only.");
    }
    private sealed class OllamaHttp : System.Net.Http.HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Inputs = new();
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            string input = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Inputs.Enqueue(input);
            string result = input.EndsWith("次の文章です。", StringComparison.Ordinal) ? "새 문장 번역." : "첫 문장 번역.";
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { done = true, message = new { content = result } })) };
        }
    }
    private static async Task OllamaHyMtAccumulation()
    {
        const string first = "「最初の文です」", next = "次の文章です。";
        using var handler = new OllamaHttp(); using var client = new System.Net.Http.HttpClient(handler);
        var options = new TranslationOptions { Provider = TranslationProvider.Ollama, Model = "synthetic-hy-mt-alias" };
        options.OllamaModels = options.OllamaModels.SetItem(options.Model, new(OllamaRequestFormat.HyMt));
        var context = new TranslationSession();
        // An earlier page repeats the current first line, as can happen on a later scene/retranslation.
        context.Accept("以前の頁。" + first + "以前の返答。", "POISONED_OLD_TRANSLATION", false);
        var ocr = new Ocr(); var frame = ocr.Frame(first); var seen = new ConcurrentQueue<PipelineUpdate>(); var telemetry = new DebugTelemetry();
        await using var pipe = new TranslationPipeline(ocr, new OllamaTranslationService(client), _ => Task.FromResult(Volatile.Read(ref frame)),
            new("ja", "NVL", false), options, context, "test", settle: TimeSpan.FromMilliseconds(20), diagnostics: telemetry);
        pipe.Updated += seen.Enqueue; pipe.Start(false);
        await pipe.RequestTriggeredAsync();
        await Until(() => seen.Any(u => u.Translation == "첫 문장 번역."), "HY-MT initial translation missing");
        Volatile.Write(ref frame, ocr.Frame(first, next));
        await pipe.RequestTriggeredAsync();
        await Until(() => seen.Any(u => u.Translation == "첫 문장 번역.\n새 문장 번역."), "HY-MT incremental response was duplicated or lost");
        var calls = handler.Inputs.ToArray();
        Check(calls.Length == 2 && !calls[0].Contains("以前の頁") && !calls[0].Contains("以前の返答"), "HY-MT duplicated the requested line through prior page context");
        Check(calls[1].Contains(first) && calls[1].EndsWith(next) && calls.All(p => !p.Contains("POISONED_OLD_TRANSLATION")), "HY-MT lost adjacent original context or leaked past generated answers");
        Check(context.History.Last().Translation == "첫 문장 번역.\n새 문장 번역.", "HY-MT accumulation polluted the cached page translation");
        Check(telemetry.Snapshot().Values is { LastAiResultLines: 1, LastAiResultRelation: DebugAiResultRelation.DifferentSourceDifferentResult, AiRepeatedResultForChangedSource: 0 }
            && telemetry.Snapshot().Values.LastAiResultCharacters == "새 문장 번역.".Length,
            "NVL response diagnostics measured the accumulated subtitle instead of the provider response");
    }
    private static async Task RejectedCropUsesFullSearch()
    {
        var ocr = new Ocr { ClipLocalResult = true };
        var ai = new Ai();
        var frame = ocr.Frame("確定した前の文。", "最初の未完本文");
        var updates = new ConcurrentQueue<PipelineUpdate>();
        await using var pipe = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Volatile.Read(ref frame)),
            new("ja", "NVL", false), new(), new(), "test",
            interval: TimeSpan.FromMilliseconds(15), settle: TimeSpan.FromMilliseconds(30));
        pipe.Updated += updates.Enqueue;
        pipe.Start(true);
        await Until(() => updates.Any(u => u.Translation is not null), "Initial full NVL recognition was not accepted");
        foreach (string tail in new[] { "更新された未完本文", "さらに更新された未完本文" }) {
            Volatile.Write(ref frame, ocr.Frame("確定した前の文。", tail));
            await Until(() => updates.Any(u => u.Original?.EndsWith(tail) == true && u.Translation is not null),
                "Full search after rejected crop lost a genuine tail change");
        }
        Check(ocr.Calls.Count(o => o.SearchArea is not null) == 1, "Known clipped NVL boundary was retried after every tail change");
        Check(ai.Calls.Count == 3, "Rejected crop caused missing or duplicate translations");
    }
    private static void TranslationEndings()
    {
        foreach (string source in new[] { "文。", "Sentence.", "来て！", "なぜ？", "What?", "Stop!", "Really?!", "「行こう」", "『はい。』", "[Hello]", "［はい］", "【はい】", "〈はい〉", "《はい》", "\"Hello\"", "“I'm ready.”", "‘Yes’", "「『はい』と言った」", "「終わり。\n」" }) {
            Check(NvlText.TranslationComplete(source), "Completed translation ending was not accepted: " + source);
            Check(NvlTranslationPage.Blocks(source + "次の途中").SequenceEqual(new[] { source, "次の途中" }), "New dialogue was not split after: " + source);
            var page = new NvlTranslationPage(); page.Begin(source, false); page.Commit(source, "  訳1\r\n");
            Check(page.Begin(source + "次の途中", false) == "次の途中" && page.Compose("\r\n訳2 \n") == "訳1\n訳2",
                "Translation accumulation did not use exactly one boundary newline");
            Check(NvlTranslationPage.Blocks(source + "VO").SequenceEqual(new[] { source + "VO" }), "A short ambiguous marker was translated by itself");
        }
        foreach (string source in new[] { "まだ途中", "文、", "文…", "...", "Wait...", "Mr.", "U.S.", "値は3.14", "example.com", "「まだ。途中", "「『はい』", "\"Not finished.", "He called it \"hope\"", "彼は「はい」と答え", "[unfinished", "[]", "", "「不一致]" })
            Check(!NvlText.TranslationComplete(source), "Unfinished/inline translation was frozen: " + source);
        Check(NvlText.LastTranslationCompleteEnd("「はい」次の途中") == 4, "Quoted source boundary lost its closing delimiter");
        Check(NvlText.LastTranslationCompleteEnd("「途中。まだ") == 0, "Open dialogue split at its inner punctuation");
        Check(!NvlText.Complete("「はい」") && !NvlText.Complete("なぜ？"), "Translation change widened OCR pixel-prefix reuse");
    }
    private static async Task DelimiterAccumulation()
    {
        foreach (var provider in new[] { TranslationProvider.Gemini, TranslationProvider.OpenAI, TranslationProvider.Ollama, TranslationProvider.HyMtLocal }) {
            var ocr = new Ocr(); var ai = new Ai(); var frame = ocr.Frame("「行こう」"); var seen = new ConcurrentQueue<PipelineUpdate>();
            var context = new TranslationSession();
            await using var pipe = new TranslationPipeline(ocr, ai, _ => Task.FromResult(Volatile.Read(ref frame)), new("ja", "NVL", false),
                new() { Provider = provider }, context, "test", settle: TimeSpan.FromMilliseconds(20));
            pipe.Updated += seen.Enqueue; pipe.Start(false);
            var rows = new List<string>(); var translations = new List<string>();
            foreach (string source in new[] { "「行こう」", "『いいよ』", "[待って]", "\"わかった\"", "来て！", "どうして？" }) {
                rows.Add(source); translations.Add("T:" + source); Volatile.Write(ref frame, ocr.Frame(rows.ToArray()));
                await pipe.RequestTriggeredAsync();
                await Until(() => seen.LastOrDefault(u => u.Translation is not null)?.Translation == string.Join("\n", translations), "Missing incremental newline for " + provider + ": " + source);
                Check(ai.Calls.Count == rows.Count && ai.Calls.Last().Text == source, "Closed dialogue caused full-page retranslation: " + provider);
            }
            rows.Add("「まだ途中"); translations.Add("T:「まだ途中"); Volatile.Write(ref frame, ocr.Frame(rows.ToArray()));
            await pipe.RequestTriggeredAsync(); await Until(() => seen.LastOrDefault(u => u.Translation is not null)?.Translation == string.Join("\n", translations), "Provisional quoted block missing");
            rows[^1] = "「まだ途中から完成」"; translations[^1] = "T:" + rows[^1]; Volatile.Write(ref frame, ocr.Frame(rows.ToArray()));
            await pipe.RequestTriggeredAsync(); await Until(() => seen.LastOrDefault(u => u.Translation is not null)?.Translation == string.Join("\n", translations), "Provisional dialogue appended instead of replacing");
            Check(context.History.Count == 1 && ai.Calls.Count == 8, "Delimiter processing duplicated context or requests");
        }
    }
    private sealed class JitterOcr(bool split=false) : IOcrService {
        private int calls;
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token) {
            int call=Interlocked.Increment(ref calls);
            bool changed=frame.Pixels.Span[95*1600+70*4]==150;
            if(split && !changed && call%2==0) return Task.FromResult(new OcrResult([
                new("本文。",.99f,new(200,89,80,22),Paragraph:7),new("安定した",.99f,new(60,91,80,18),Paragraph:2)],TimeSpan.Zero));
            return Task.FromResult(new OcrResult([new(changed?"別の本文です。":call%2==1?"安定した本文。":"安定した本文、",.99f,
                call%2==1?new(60,90,220,20):new(59,89,222,22))],TimeSpan.Zero));
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static async Task AnimatedCandidateJitter(bool split=false) {
        var ocr=new JitterOcr(split);var ai=new Ai();var seen=new ConcurrentQueue<PipelineUpdate>();int captures=0,source=0;
        ImageFrame Capture() {
            var pixels=new byte[1600*300];int phase=Interlocked.Increment(ref captures)%2;
            for(int y=95;y<99;y++) for(int x=70;x<74;x++) pixels[y*1600+x*4]=(byte)(Volatile.Read(ref source)==0?60:150);
            for(int y=100;y<105;y++) for(int x=290+phase*7;x<295+phase*7;x++) pixels[y*1600+x*4]=255;
            return new(400,300,1600,pixels);
        }
        await using var pipeline=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Capture()),new("ja","NVL",false),new(),new(),"test",
            interval:TimeSpan.FromMilliseconds(15),settle:TimeSpan.FromMilliseconds(80));
        pipeline.Updated+=seen.Enqueue;pipeline.Start(true);
        await Until(()=>seen.Any(u=>u.Original=="安定した本文。"),"Animated pixels and detector punctuation jitter starved a new candidate");
        int target=captures+20;await Until(()=>Volatile.Read(ref captures)>=target,"Jitter fixture stopped capturing");
        Check(ai.Calls.Count==1,"Unchanged body crop jitter generated repeated AI requests");
        Volatile.Write(ref source,1);
        await Until(()=>seen.Any(u=>u.Original=="別の本文です。"),"Actual body pixel change was suppressed with the decoration");
        Check(ai.Calls.Count==2,"Actual new source was lost or duplicated");
    }
    private sealed class TallRowOcr : IOcrService {
        public OcrState State=>OcrState.Ready;
        public int Full, Partial;
        public static OcrLine[] Rows(bool changed) => [
            new("最初の本文。",.99f,new(50,40,300,20)),
            new("次の本文。",.99f,new(50,80,300,20)),
            new("最後の確定文。",.99f,new(50,120,300,20)),
            new(changed?"新しく加わった本文":"続きの本文",.99f,new(60,160,280,140))];
        public Task InitializeAsync(OcrOptions options,CancellationToken token)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions options,CancellationToken token) {
            var rows=Rows(frame.Pixels.Span[180*1600+80*4]==150);
            if(options.SearchArea is {} area) {
                Interlocked.Increment(ref Partial);
                rows=rows.Where(l=>l.Bounds.Y>=area.Y && l.Bounds.Y+l.Bounds.Height<=area.Y+area.Height).ToArray();
            } else Interlocked.Increment(ref Full);
            return Task.FromResult(new OcrResult(rows,TimeSpan.Zero));
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static async Task AcceptedRowsSurviveIncrementalMerge() {
        var ocr=new TallRowOcr();var ai=new Ai();var seen=new ConcurrentQueue<PipelineUpdate>();int captures=0,source=0;
        ImageFrame Capture() {
            var pixels=new byte[1600*400];int phase=Interlocked.Increment(ref captures)%2;
            for(int y=180;y<185;y++) for(int x=80;x<85;x++) pixels[y*1600+x*4]=(byte)(Volatile.Read(ref source)==0?60:150);
            for(int y=330;y<335;y++) for(int x=370+phase*7;x<375+phase*7;x++) pixels[y*1600+x*4]=255;
            return new(400,400,1600,pixels);
        }
        await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Capture()),new("ja","NVL",false),new(),new(),"test",
            interval:TimeSpan.FromMilliseconds(15),settle:TimeSpan.FromMilliseconds(80));
        pipe.Updated+=seen.Enqueue;pipe.Start(true);
        string original=NvlText.DisplayLines(TallRowOcr.Rows(false));
        await Until(()=>seen.Any(u=>u.Original==original && u.Translation is not null),"Full/partial OCR lost retained rows and never accepted a stable NVL page");
        int target=captures+20;await Until(()=>Volatile.Read(ref captures)>=target,"Incremental merge fixture stopped capturing");
        Check(ocr.Full>0 && ocr.Partial>0,"Regression did not exercise both full and partial recognition");
        Check(ai.Calls.Count==1 && ai.Calls.Single().Text.Replace("\n","")==string.Concat(TallRowOcr.Rows(false).Select(l=>l.Text)),
            "Incremental merge omitted body text or repeatedly translated the same page");
        Volatile.Write(ref source,1);
        await Until(()=>seen.Any(u=>u.Original==NvlText.DisplayLines(TallRowOcr.Rows(true)) && u.Translation is not null),"Real tail edit was hidden after merge stabilization");
        Check(ai.Calls.Count==2,"Changed tail was lost or translated more than once");
    }
    private static void Rules() {
        foreach(var suffix in new[]{"VO","68","V0","ＶＯ","１２","OK","LIVE"}) {
            string source="これは本文。"+suffix;
            Check(NvlTranslationPage.Blocks(source).SequenceEqual(new[]{source}),"Short suffix was isolated or stripped: "+suffix);
        }
        Check(NvlTranslationPage.Blocks("本文。別の途中").SequenceEqual(new[]{"本文。","別の途中"}),"Meaningful Japanese incremental tail lost existing block boundaries");
        Check(!NvlTranslationPage.IsShortAlphanumericTail("本文") && !NvlTranslationPage.IsShortAlphanumericTail("12345"),"Short alphanumeric policy widened to arbitrary body text");
        foreach(var text in new[]{"文。","Sentence.","文。 次。"}) Check(NvlText.Complete(text),"Period not complete");
        foreach(var text in new[]{"文、","文！","文？","文…","...","Wait...","文」","文。」","Mr.","値は3.14", ""}) Check(!NvlText.Complete(text),"Non-period block frozen: "+text);
        Check(NvlText.LastCompleteEnd("終わり。途中")==4,"Complete prefix boundary");
        foreach(var text in new[]{"e.g. sample","i.e. sample","U.S. example","example.com","文。 」"}) Check(NvlText.LastCompleteEnd(text)==0,"Abbreviation/quote created a fixed prefix");
        Check(NvlText.Canonical("一行\n二行", "ja")=="一行二行", "Japanese wrap changed prefix");
        Check(NvlText.Canonical("Old warrior は\n来た。", "ja")=="Old warriorは来た。","Japanese layout normalization joined Latin words");
        var p=new NvlTranslationPage(); p.Begin("前。途中",false); p.Commit("前。","先");
        Check(p.Begin("前。途中の続き。",false)=="途中の続き。" && p.FixedTranslation=="先","Continuation rewrote fixed block");
        var page=p.Id; p.Begin("別の画面。",false); Check(p.Id!=page && p.FixedTranslation=="","Replacement retained previous page");
        page=p.Id; p.Begin("別の画面。",true); Check(p.Id!=page,"Explicit blank/page boundary ignored");
        var context=new TranslationSession(); context.Accept("前ページ。","前ページ訳",false);
        context.AcceptNvlPage(p.Id,"前。途中","先\n仮訳");
        var history=context.ForNvlRequest(p.Id,"前。","先");
        Check(history.Count==2 && history[0].Source=="前ページ。" && history[1]==new ConversationTurn("前。","先"),"Provisional text contaminated read-only context");
        context.AcceptNvlPage(p.Id,"前。途中の続き。","先\n完成");
        Check(context.History.Count==2 && context.History.Last().Source=="前。途中の続き。","Page snapshots duplicated history");
        var restored=new NvlTranslationPage();restored.Begin("先。",false);restored.Commit("先。","訳1");
        restored.Begin("先。次。",false);restored.Commit("次。","訳2");var oldPage=restored.Id;
        Check(restored.Begin("先。次。追加。",true)=="追加。" && restored.FixedTranslation=="訳1\n訳2","Returning body discarded completed block boundaries");
        Check(restored.Id!=oldPage && restored.RestoredFrom==oldPage,"Restoration hid the physical page transition");
        restored.Begin("先。次。追加。途中",false);restored.Commit("追加。","訳3");
        restored.Begin("先。次。追加。別内容",true);
        Check(restored.FixedSource=="" && restored.RestoredFrom is null,"Only a shared completed prefix restored a different partial page");
    }
    private static async Task ShortTailRequests() {
        foreach(string language in new[]{"ja","en"}) foreach(string suffix in new[]{"VO","68","ＶＯ"}) {
            var ocr=new Ocr();var ai=new Ai {Handler=(_,_)=>Task.FromResult(new TranslationResult("한 문장 번역",TimeSpan.Zero,0))};
            string body=language=="ja"?"これは一つの文。":"One complete sentence.";
            var frame=ocr.Frame(body,suffix);var updates=new ConcurrentQueue<PipelineUpdate>();var context=new TranslationSession();var debug=new DebugTelemetry();
            string source=NvlText.Canonical(body+"\n"+suffix,language);
            await using var pipeline=new TranslationPipeline(ocr,ai,_=>Task.FromResult(frame),new(language,"NVL",false),new(),context,"test",
                interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(20),diagnostics:debug);
            pipeline.Updated+=updates.Enqueue;pipeline.Start(false);await pipeline.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Translation=="한 문장 번역"),"Short suffix appended a repeated context translation");
            Check(ai.Calls.Count==1 && ai.Calls.Single().Text==source,"Original tail was deleted or separately requested");
            Check(updates.Last(u=>u.Translation is not null).Original==body+"\n"+suffix,"Original display was rewritten to hide the suffix");
            Check(debug.Snapshot().Values is {NvlPlannedBlocks:1,NvlShortTailCombined:true,AiRequests:1},"Combined request plan/counters were not diagnosed");
            await pipeline.RequestTriggeredAsync();await Task.Delay(60);
            Check(ai.Calls.Count==1,"Repeated unchanged combined source requested AI again");
        }
        foreach(bool blank in new[]{false,true}) {
            var ocr=new Ocr();var ai=new Ai {Handler=(_,_)=>Task.FromResult(new TranslationResult("동일 번역",TimeSpan.Zero,0))};
            var frame=ocr.Frame("確定した文。");var updates=new ConcurrentQueue<PipelineUpdate>();var context=new TranslationSession();var debug=new DebugTelemetry();
            await using var pipeline=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),context,"test",
                interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(20),diagnostics:debug);
            pipeline.Updated+=updates.Enqueue;pipeline.Start(false);await pipeline.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Translation=="동일 번역"),"Prefix was not translated");
            if(blank) {
                int seen=ocr.Calls.Count;Volatile.Write(ref frame,ocr.Frame());await pipeline.RequestTriggeredAsync();
                await Until(()=>ocr.Calls.Count>seen,"Blank boundary not observed");await Task.Delay(40);
            }
            int before=updates.Count;Volatile.Write(ref frame,ocr.Frame("確定した文。","VO"));await pipeline.RequestTriggeredAsync();
            await Until(()=>updates.Skip(before).Any(u=>u.Translation=="동일 번역"),"Combined tail did not replace the old page translation");
            Check(ai.Calls.Count==2 && ai.Calls.Last().Text=="確定した文。VO","A suffix-only request was sent after a committed prefix");
            Check(ai.Calls.Last().History.Count==0 && context.History.Count==1,"Reopened page duplicated its own read-only context");
            Check(updates.Skip(before).All(u=>u.Translation is null or "동일 번역"),"Reopened prefix and suffix translations were concatenated twice");
            Check(debug.Snapshot().Values.NvlPrefixReopened,"Prefix replacement missing from diagnosis");
        }
        // Identical translations of genuinely repeated source remain intact.
        {
            var ocr=new Ocr();var ai=new Ai {Handler=(r,_)=>Task.FromResult(new TranslationResult(string.Join("\n\n",r.Text.Split("\n\n").Select(_=>"반복")),TimeSpan.Zero,0))};var frame=ocr.Frame("同じ文。","同じ文");
            var updates=new ConcurrentQueue<PipelineUpdate>();
            await using var pipeline=new TranslationPipeline(ocr,ai,_=>Task.FromResult(frame),new("ja","NVL",false),new(),new(),"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(20));
            pipeline.Updated+=updates.Enqueue;pipeline.Start(false);await pipeline.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Translation=="반복\n반복"),"Legitimate repeated source was globally deduplicated");
            Check(ai.Calls.Count==1 && ai.Calls.Single().ParagraphCount==2,"Repeated source paragraphs were not preserved in one batch");
        }
    }
    private static async Task Sequence() {
        var ocr=new Ocr(); var ai=new Ai(); var frame=ocr.Frame("前の文。","まだ途中");
        var updates=new ConcurrentQueue<PipelineUpdate>(); var context=new TranslationSession(); var qa=new GameQaRecorder();
        var diagnostics=new DebugTelemetry();
        await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),context,"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(25),gameQa:qa,qaProfile:GameQaProfile.From(new()),diagnostics:diagnostics);
        pipe.Updated+=updates.Enqueue; pipe.Start(false);
        async Task Send(ImageFrame next,string translation) {
            Volatile.Write(ref frame,next); await pipe.RequestTriggeredAsync();
            await Until(()=>updates.LastOrDefault(u=>u.Translation is not null)?.Translation==translation,"NVL expected composed result: "+translation);
        }
        await Send(frame,"T:前の文。\nT:まだ途中");
        Check(ai.Calls.Count==1 && ai.Calls.Single().Text=="前の文。\n\nまだ途中","Initial completed prefix/tail not batched");
        Check(ai.Calls.Single().History.Count==0,"Current batch duplicated its prefix as previous context");
        Check(ai.Calls.All(r=>r.Progress is null),"Provisional subtitle replaced by partial streaming");
        await Send(ocr.Frame("前の文。","まだ途中の続き。","次の途中"),"T:前の文。\nT:まだ途中の続き。\nT:次の途中");
        Check(ai.Calls.Count==2 && ai.Calls.Last().Text=="まだ途中の続き。\n\n次の途中","Mid-line continuation retransmitted fixed text or issued extra calls");
        Check(ai.Calls.Last().History.Last().Source=="前の文。","Previously completed page prefix not supplied as context");
        Check(qa.Snapshot!.Record.NvlPrefix.Count==1 && qa.Snapshot.Record.Ocr!.Lines.Count==2
            && qa.Snapshot.Record.PipelineText=="前の文。まだ途中の続き。次の途中" && qa.Snapshot.Frame.Width==400,
            "QA conflated native ROI text and reused prefix");
        GameQaFiles.Validate(qa.Snapshot.Record);
        int prefixCalls=ai.Calls.Count;
        await Send(ocr.Frame("前の文。","まだ途中の続き。"),"T:前の文。\nT:まだ途中の続き。");
        Check(ai.Calls.Count==prefixCalls && context.TryLast("前の文。まだ途中の続き。",out _),"Removed provisional tail resent fixed prefix or kept stale context");
        await Send(ocr.Frame("前の文。","まだ途中の続き。","別の途中"),"T:前の文。\nT:まだ途中の続き。\nT:別の途中");
        Check(ai.Calls.Last().Text=="別の途中","New tail after removal retransmitted fixed source");
        int before=ai.Calls.Count; await pipe.RequestTriggeredAsync(); await Task.Delay(70);
        Check(ai.Calls.Count==before,"Same stable frame duplicated AI request");
        await Send(ocr.Frame("全く別の画面。"),"T:全く別の画面。");
        Check(ai.Calls.Last().Text=="全く別の画面。" && !updates.Last(u=>u.Translation is not null).Translation!.Contains("T:前"),"Non-cumulative replacement appended old page");
        Check(context.History.Count==2,"Prior page not retained as bounded context");
        int seen=ocr.Calls.Count; Volatile.Write(ref frame,ocr.Frame()); await pipe.RequestTriggeredAsync();
        await Until(()=>ocr.Calls.Count>seen,"Blank frame not inspected"); await Task.Delay(30);
        Check(updates.All(u=>u.Translation!= ""),"Blank page erased visible translation");
        await Send(ocr.Frame("次ページの途中"),"T:次ページの途中");
        Check(ai.Calls.Last().History.Any(h=>h.Source=="全く別の画面。"),"Page boundary lost previous dialogue context");
        Check(updates.Where(u=>u.Original is not null).All(u=>u.State!=PipelineState.Recognizing),"Unconfirmed original published");
        Check(context.History.Count==3,"Page state crossed blank boundary");
        await Send(ocr.Frame("次ページの途中","後半。"),"T:次ページの途中後半。");
        int manualBefore=ai.Calls.Count;
        await pipe.RequestManualAsync(true); await Until(()=>ai.Calls.Count==manualBefore+1,"Manual forced retry not executed");
        Check(ai.Calls.Last().Text=="次ページの途中後半。", "Manual path did not use complete source");
        await pipe.RequestTriggeredAsync();await Task.Delay(70);
        Check(ai.Calls.Count==manualBefore+1,"Returning from manual OCR duplicated the same NVL source");
        await pipe.DisposeAsync();
        Check(diagnostics.Snapshot().Values.LogicalOcrCalls==ocr.Calls.Count && diagnostics.Snapshot().Values.AiRequests==ai.Calls.Count,"NVL incremental/full/manual counters differ from actual calls");
    }
    private static async Task Unstable() {
        var ocr=new Ocr();var ai=new Ai();var frame=ocr.Frame("途");var updates=new ConcurrentQueue<PipelineUpdate>();
        await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),new(),"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(100));
        pipe.Updated+=updates.Enqueue;pipe.Start(false);
        foreach(var text in new[]{"途","途中","途中の","途中の文"}) {
            Volatile.Write(ref frame,ocr.Frame(text));await pipe.RequestTriggeredAsync();await Task.Delay(15);
        }
        Check(ai.Calls.IsEmpty && !updates.Any(u=>u.Original is not null),"Typewriter fragments translated/published before quiet interval");
        await Until(()=>updates.Any(u=>u.Translation=="T:途中の文"),"Quiet unfinished block not translated");
        Check(ai.Calls.Count==1,"Unstable fragments generated extra calls");
    }
    private static async Task Superseded() {
        var ocr=new Ocr();var ai=new Ai();var frame=ocr.Frame("確定。");var updates=new ConcurrentQueue<PipelineUpdate>();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Handler=async(r,t)=>{ if(r.Text=="途中") { started.TrySetResult();await release.Task; } return new("T:"+r.Text,TimeSpan.Zero,0); };
        await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),new(),"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(20));
        pipe.Updated+=updates.Enqueue;pipe.Start(false);
        try {
            await pipe.RequestTriggeredAsync();await Until(()=>updates.Any(u=>u.Translation=="T:確定。"),"Prefix was not accepted before the delayed suffix");
            Volatile.Write(ref frame,ocr.Frame("確定。","途中"));
            await pipe.RequestTriggeredAsync();await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref frame,ocr.Frame("確定。","途中から完成。"));await pipe.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Translation=="T:確定。\nT:途中から完成。"),"New suffix blocked by canceled provider");
            release.TrySetResult();await Task.Delay(40);
            Check(!updates.Any(u=>u.Translation?.EndsWith("T:途中")==true),"Late unfinished response replaced newer result");
            Check(ai.Calls.Count(r=>r.Text=="確定。")==1,"Successful prefix retransmitted after cancellation");
            var oldPageStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldPageRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ai.Handler=async(r,t)=>{if(r.Text=="新画面の途中"){oldPageStarted.TrySetResult();await oldPageRelease.Task;}return new("T:"+r.Text,TimeSpan.Zero,0);};
            try {
                Volatile.Write(ref frame,ocr.Frame("新画面の途中"));await pipe.RequestTriggeredAsync();await oldPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Volatile.Write(ref frame,ocr.Frame("さらに別のページ。"));await pipe.RequestTriggeredAsync();
                await Until(()=>updates.Any(u=>u.Translation=="T:さらに別のページ。"),"Page transition blocked by old request");
                oldPageRelease.TrySetResult();await Task.Delay(40);
                Check(!updates.Any(u=>u.Translation=="T:新画面の途中"),"Late response crossed a page boundary");
            } finally {oldPageRelease.TrySetResult();}
        } finally {release.TrySetResult();}
    }
    private static async Task ProvisionalReplacement() {
        var ocr=new Ocr();var ai=new Ai();var frame=ocr.Frame("固定。","途中");var updates=new ConcurrentQueue<PipelineUpdate>();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),new(),"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(25));
        pipe.Updated+=updates.Enqueue;pipe.Start(false);
        try {
            await pipe.RequestTriggeredAsync(); await Until(()=>updates.Any(u=>u.Translation=="T:固定。\nT:途中"),"Provisional initial result missing");
            ai.Handler=async(r,t)=>{await release.Task;return new("新訳:"+r.Text,TimeSpan.Zero,0);};
            int start=updates.Count;Volatile.Write(ref frame,ocr.Frame("固定。","途中からの追加"));await pipe.RequestTriggeredAsync();
            await Until(()=>ai.Calls.Count==2,"Continuation request missing");
            Check(updates.Skip(start).All(u=>u.Translation is null),"Existing provisional translation replaced before new response completed");
            release.TrySetResult();await Until(()=>updates.Any(u=>u.Translation=="T:固定。\n新訳:途中からの追加"),"Provisional block not atomically replaced");
            Check(ai.Calls.Last().History.Last()==new ConversationTurn("固定。","T:固定。"),"Previous unfinished translation leaked into fixed context");
            ai.Handler=(r,t)=>Task.FromException<TranslationResult>(new TranslationException("Synthetic failure"));
            start=updates.Count;Volatile.Write(ref frame,ocr.Frame("固定。","途中からの追加続き"));await pipe.RequestTriggeredAsync();
            await Until(()=>updates.Skip(start).Any(u=>u.State==PipelineState.Error),"Suffix error missing");
            await pipe.RequestTriggeredAsync(); await Task.Delay(60);
            Check(ai.Calls.Count==3 && updates.Skip(start).All(u=>u.Translation is null),"Suffix failure retried or erased readable translation");
            // An unrelated, unfinished replacement is a new page even without a blank sample.
            ai.Handler=null;Volatile.Write(ref frame,ocr.Frame("別画面の途中"));await pipe.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Translation=="T:別画面の途中"),"Non-cumulative provisional replacement appended fixed page");
            Check(ai.Calls.Last().Text=="別画面の途中", "Replacement sent stale prefix");
        }finally{release.TrySetResult();}
    }
    private static async Task EnglishAndProtocolFallback() {
        foreach(bool specialProtocol in new[]{false,true}) {
            var ocr=new Ocr();var ai=new Ai();var frame=ocr.Frame("First.","Half a sentence");var updates=new ConcurrentQueue<PipelineUpdate>();
            var options=new TranslationOptions {Provider=specialProtocol?TranslationProvider.Ollama:TranslationProvider.Gemini, Model="chosen-exact-model"};
            if(specialProtocol) options.OllamaModels=options.OllamaModels.SetItem(options.Model,new(OllamaRequestFormat.TranslateGemma));
            await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("en","NVL",false),options,new(),"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(20));
            pipe.Updated+=updates.Enqueue;pipe.Start(false);await pipe.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Translation is not null),"English/protocol first result missing");
            Volatile.Write(ref frame,ocr.Frame("First.","Half a sentence is completed."));await pipe.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Translation?.EndsWith("is completed.")==true),"English/protocol continuation missing");
            Check(ai.Calls.All(r=>r.Options.Model=="chosen-exact-model"),"Incremental/fallback silently switched model");
            if(specialProtocol) Check(ai.Calls.Count==2 && ai.Calls.Last().Text=="Half a sentence is completed." && ai.Calls.All(r=>r.History.Count==0),"TranslateGemma paragraph layout sent unsupported history or repeated the whole page");
            else Check(ai.Calls.Count==2 && ai.Calls.Last().Text=="Half a sentence is completed."
                && ai.Calls.Last().History.Last().Source=="First.","English inline continuation repeated fixed translation or lost context");
        }
    }
    private static async Task Presentation() {
        var ocr=new Ocr();var ai=new Ai();var frame=ocr.Frame("一。");var updates=new ConcurrentQueue<PipelineUpdate>();
        await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),new(),"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(25));
        pipe.Updated+=updates.Enqueue;pipe.Start(false);
        async Task Send(string original,string translation,params string[] rows) {
            Volatile.Write(ref frame,ocr.Frame(rows));await pipe.RequestTriggeredAsync();
            await Until(()=>updates.Any(u=>u.Original==original && u.Translation==translation),"Visual original and incremental translation rows differ");
        }
        await Send("一。","T:一。","一。");
        await Send("一。二。","T:一。\nT:二。","一。二。");
        Check(updates.Last(u=>u.Translation is not null).Original=="一。二。","Same-Y addition inserted original newline");
        await Send("一。二。\n三の途中","T:一。\nT:二。\nT:三の途中","一。二。","三の途中");
        await Send("一。二。\n三の途中から完成。","T:一。\nT:二。\nT:三の途中から完成。","一。二。","三の途中から完成。");
        Check(ai.Calls.Select(r=>r.Text).SequenceEqual(new[]{"一。","二。","三の途中","三の途中から完成。"}),"Presentation change disturbed incremental API targets");
        Check(updates.Last(u=>u.Translation is not null).Translation!.Count(c=>c=='\n')==2,"Provisional replacement accumulated extra translated rows");
        await pipe.RequestTriggeredAsync();await Task.Delay(50);
        Check(updates.Last(u=>u.Original is not null).Original=="一。二。\n三の途中から完成。" && ai.Calls.Count==4,"Duplicate restoration flattened the visual source");
    }
    private static async Task ReflowWhileTranslating() {
        foreach(string language in new[]{"ja","en"}) {
            var ocr=new Ocr();var ai=new Ai();string full=language=="ja"?"甲乙丙丁":"One two three four";
            string[] rows=language=="ja"?["甲乙","丙丁"]:["One two","three four"];
            var frame=ocr.Frame(full);var updates=new ConcurrentQueue<PipelineUpdate>();
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool canceled=false;
            ai.Handler=async(r,t)=>{using var registration=t.Register(()=>canceled=true);await release.Task;return new("held translation",TimeSpan.Zero,0);};
            await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new(language,"NVL",false),new(),new(),"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(80));
            pipe.Updated+=updates.Enqueue;pipe.Start(false);
            try {
                await pipe.RequestTriggeredAsync();await Until(()=>ai.Calls.Count==1,"Reflow first AI request missing");
                int before=updates.Count;Volatile.Write(ref frame,ocr.Frame(rows));await pipe.RequestTriggeredAsync();
                await Until(()=>updates.Skip(before).Any(u=>u.State==PipelineState.Recognizing),"Layout candidate not examined");
                Check(updates.Skip(before).All(u=>u.Original is null),"Unstable row layout was published immediately");
                string displayed=string.Join("\n",rows);
                await Until(()=>updates.Any(u=>u.Original==displayed),"Stable layout-only change not displayed");
                Check(ai.Calls.Count==1 && !canceled,"Layout-only reflow canceled or resent AI");
                release.TrySetResult();await Until(()=>updates.Any(u=>u.Translation=="held translation"),"Held AI failed after reflow");
                Check(updates.Last(u=>u.Translation is not null).Original==displayed,"AI completion restored the obsolete original layout");
            }finally{release.TrySetResult();}
        }
    }
    private static async Task ClearAndReturn() {
        foreach(bool clear in new[]{true,false}) {
            var ocr=new Ocr();var ai=new Ai();var frame=ocr.Frame("前。");var context=new TranslationSession();var updates=new ConcurrentQueue<PipelineUpdate>();
            await using var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),context,"test",interval:TimeSpan.FromMilliseconds(10),settle:TimeSpan.FromMilliseconds(20));
            pipe.Updated+=updates.Enqueue;pipe.Start(false);
            async Task Send(ImageFrame image,string translated) {
                Volatile.Write(ref frame,image);await pipe.RequestTriggeredAsync();
                await Until(()=>updates.LastOrDefault(u=>u.Translation is not null)?.Translation==translated,"Clear/return lost the translated block layout");
            }
            await Send(frame,"T:前。");await Send(ocr.Frame("前。","次。"),"T:前。\nT:次。");
            int start=updates.Count;
            if(clear) {
                Volatile.Write(ref frame,ocr.Frame());int before=ocr.Calls.Count;await pipe.RequestTriggeredAsync();
                await Until(()=>ocr.Calls.Count>before,"Transition blank not recognized");await Task.Delay(30);
            }
            var returned=ocr.Frame("前。","次。","続き。");
            // A changed background forces full reacquisition without changing the retained text.
            var pixels=returned.Pixels.ToArray();for(int y=0;y<returned.Height;y++)for(int x=0;x<returned.Width;x++)for(int c=0;c<3;c++)pixels[y*returned.Stride+x*4+c]=(byte)Math.Min(255,pixels[y*returned.Stride+x*4+c]+70);
            var changed=new ImageFrame(returned.Width,returned.Height,returned.Stride,pixels);ocr.Results[changed]=ocr.Results[returned];
            await Send(changed,"T:前。\nT:次。\nT:続き。");
            Check(ai.Calls.Count==3 && ai.Calls.Last().Text=="続き。","Transient clear resent the retained whole page");
            Check(updates.Skip(start).All(u=>u.Translation is null or "T:前。\nT:次。\nT:続き。"),"Transition published flattened/replaced old blocks");
            Check(ai.Calls.Last().History.Count==1 && ai.Calls.Last().History[0].Source=="前。次。","Restored page duplicated its own context");
            Check(context.History.Count==1,"Restored page duplicated its accumulated snapshot");
            await Send(ocr.Frame("別の内容。"),"T:別の内容。");
            Check(context.History.Count==2 && ai.Calls.Last().Text=="別の内容。","Unrelated new page reused retained translations");
            var partial=ocr.Frame("別の内容。","未完");await Send(partial,"T:別の内容。\nT:未完");
            int cachedCalls=ai.Calls.Count, seen=ocr.Calls.Count;Volatile.Write(ref frame,ocr.Frame());await pipe.RequestTriggeredAsync();
            await Until(()=>ocr.Calls.Count>seen,"Partial-page clear not inspected");await Task.Delay(30);
            int completed=updates.Count(u=>u.Translation is not null);Volatile.Write(ref frame,partial);await pipe.RequestTriggeredAsync();
            await Until(()=>updates.Count(u=>u.Translation is not null)>completed,"Cached provisional page did not restore");
            Check(ai.Calls.Count==cachedCalls && updates.Last(u=>u.Translation is not null).Translation=="T:別の内容。\nT:未完","Returning identical provisional page resent or flattened cached translation");
            await Send(ocr.Frame("別の内容。","未完成。"),"T:別の内容。\nT:未完成。");
            Check(ai.Calls.Last().Text=="未完成。" && ai.Calls.Last().History.Last().Source=="別の内容。","Restored provisional tail lost its independent replacement/context");
        }
    }
}
