using System.Collections.Concurrent;
using System.Diagnostics;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class DebugDiagnosticsTests
{
    private static int checks;
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
    internal static async Task Storage()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.Debug", Guid.NewGuid().ToString("N"));
        try {
            Check(!AppLaunchOptions.Parse([], root).DebugOverlay, "Ordinary execution enables debugging");
            var options = AppLaunchOptions.Parse(["--dev-run", "--debug-overlay", "--mock-ai", "--test-data", root + "-isolated"], root);
            Check(options.DebugOverlay && options.MockAi && options.DataDirectory.EndsWith("-isolated"), "Debug options did not compose with isolation/mock");
            Check(!Directory.Exists(root), "Argument parse wrote files");
            var store = new DebugOverlayStateStore(root); var defaults = await store.LoadAsync();
            Check(defaults.OcrExpanded && defaults.Placement.Width == 400, "Missing debug state did not default safely");
            var saved = defaults with { OcrExpanded = false, EventsExpanded = true, DpiScale = 1.5,
                Placement = new() { X = -1600, Y = 100, Width = 420, Height = 650, MonitorId = "synthetic-monitor", MonitorWorkX = -1920, MonitorWorkY = 0 } };
            await store.SaveAsync(saved); var loaded = await store.LoadAsync();
            Check(!loaded.OcrExpanded && loaded.EventsExpanded && loaded.DpiScale == 1.5 && loaded.Placement.MonitorId == "synthetic-monitor", "Debug state did not round trip");
            var fallback = OverlayPlacementPolicy.Resolve(loaded.Placement, [new("main", "main", new(0, 0, 1920, 1080), 1.5, true)]);
            Check(fallback.Temporary && fallback.X >= 0 && fallback.Width <= 1920 / 1.5, "Missing monitor fallback unsafe");
            await store.SaveAsync(loaded with { TranslationExpanded = true });
            Check((await store.LoadAsync()).Placement.X == -1600, "Collapse save overwrote disconnected monitor placement");
            var restored = OverlayPlacementPolicy.Resolve(loaded.Placement, [new("synthetic-monitor", "side", new(-1920, 0, 1920, 1080), 1.5)]);
            Check(!restored.Temporary && restored.X == -1600, "Saved monitor did not return");
            await File.WriteAllTextAsync(Path.Combine(root, "debug-overlay.json"), "{broken}");
            Check((await store.LoadAsync()).Placement.Width == 400, "Broken debug JSON blocked startup");
            var telemetry = new DebugTelemetry(); long session = telemetry.BeginSession(new("en", "ADV", false), new() { Model = "C:\\private\\SECRET_TOKEN" }, 700);
            telemetry.OcrStarted(session, DebugReason.Manual); telemetry.Accept(session, false, false, false, false);
            var snapshot = telemetry.Snapshot(); telemetry.Ai(session, snapshot.Current!.Id, false);
            string a = await DebugReportWriter.SaveAsync(root, snapshot, "1.0.0", new string('a', 40));
            string b = await DebugReportWriter.SaveAsync(root, snapshot, "1.0.0", new string('a', 40));
            Check(a != b && File.Exists(a) && File.Exists(b), "Report save overwrote an existing file");
            string report = await File.ReadAllTextAsync(a);
            Check(report.Contains("[RECENT EVENTS]") && report.Contains("AI=0") && !report.Contains("SECRET_TOKEN") && !report.Contains(root), "Report privacy/snapshot contract failed");
            Check(snapshot.Values.AiRequests == 0 && telemetry.Snapshot().Values.AiRequests == 1, "Snapshot mutated after capture");
            string blocked = Path.Combine(root, "blocked"); await File.WriteAllTextAsync(blocked, "not a directory");
            bool failed = false; try { await new DebugOverlayStateStore(blocked).SaveAsync(saved); } catch (IOException) { failed = true; }
            Check(failed && (await new DebugOverlayStateStore(blocked).LoadAsync()).OcrExpanded, "State failure did not allow safe defaults/retry");
            bool reportFailed = false; try { await DebugReportWriter.SaveAsync(blocked, snapshot, "1", ""); } catch (IOException) { reportFailed = true; }
            Check(reportFailed, "Report write ignored invalid destination");
            var requests=new DebugTelemetry();long requestSession=requests.BeginSession(new("ja","NVL",false),new(),700);
            string firstHash=new('a',64),secondHash=new('b',64);
            requests.Ai(requestSession,0,false,firstHash,12);
            Check(requests.Snapshot().Values.LastAiSourceRelation==DebugAiSourceRelation.First,"First request source was not distinguished");
            requests.Ai(requestSession,0,false,firstHash,12);
            Check(requests.Snapshot().Values.LastAiSourceRelation==DebugAiSourceRelation.SameAsPrevious,"Identical consecutive request sources were not diagnosed");
            requests.Ai(requestSession,0,false,secondHash,2);
            Check(requests.Snapshot().Values is {LastAiSourceRelation:DebugAiSourceRelation.DifferentFromPrevious,LastAiSourceCharacters:2},"Different request source was reported as identical");
            requests.Ai(requestSession,0,true);
            Check(requests.Snapshot().Values.LastAiSourceRelation==DebugAiSourceRelation.DifferentFromPrevious,"Cache reuse replaced the last actual request relation");
            string requestReport=DebugReportWriter.Format(requests.Snapshot(),"1.0.0",new('c',40));
            Check(!requestReport.Contains(firstHash) && !requestReport.Contains(secondHash) && requestReport.Contains("LastAiSourceCharacters: 2"),"Request comparison leaked fingerprints or omitted numeric metadata");
            requests.BeginSession(new("ja","NVL",false),new(),700);
            Check(requests.Snapshot().Values.LastAiSourceRelation==DebugAiSourceRelation.NotMeasured && requests.Snapshot().Values.LastAiSourceCharacters is null,"Request relation crossed session boundaries");
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {checks} debug launch/state/monitor/report/privacy assertions; temporary data only.");
    }
    internal static async Task Pipeline()
    {
        Collector();
        ResponsePrivacyAndLifetime();
        OverlayPrivacyAndLifetime();
        await TranslationErrors();
        var plain = await Scenario(false); var observed = await Scenario(true);
        Check(plain == observed && observed == (4, 1, 3), $"Diagnostics changed pipeline work: {plain}/{observed}");
        foreach (bool empty in new[] { true, false }) {
            var collector = new DebugTelemetry(); var engine = new Engine { Empty = empty, Fail = !empty };
            await using var pipeline = new TranslationPipeline(engine, null, _ => Task.FromResult(Frame(1)), new("ja", "ADV", false), new(), new(), "", translationEnabled: false, diagnostics: collector);
            pipeline.Start(false); await pipeline.RequestManualAsync(); await Until(() => collector.Snapshot().Values.LogicalOcrCalls == 1 && collector.Snapshot().Events.Any(e => e.Code == DebugCode.OCR_TEXT_REJECTED));
            await pipeline.DisposeAsync(); var snap = collector.Snapshot();
            Check(snap.Current!.OcrCalls == 1 && snap.Current.AiRequests == 0 && snap.Current.Status != "Accepted", "Failed/unconfirmed OCR cycle was lost or counted as accepted");
        }
        Console.WriteLine($"PASS: {checks} debug collector, OCR invocation/ROI retry/cycle and enabled/disabled A/B assertions; Mock only.");
    }
    private static void OverlayPrivacyAndLifetime()
    {
        var telemetry = new DebugTelemetry();
        long owner = telemetry.BeginSession(new("ja", "NVL", false), new(), 500);
        const string source = "PRIVATE CURRENT SOURCE", answer = "PRIVATE CURRENT ANSWER";
        telemetry.OverlayApplied(owner, true, source, answer, source, "PRIVATE OLD ANSWER");
        var mismatch = telemetry.Snapshot();
        Check(mismatch.Values is { OverlaySourceMatchesUpdate: true, OverlayTranslationMatchesUpdate: false, OverlayContentMismatches: 1 },
            "A visible but stale subtitle was counted as a matching update");
        telemetry.OverlayApplied(owner, true, source, null, source, "PRIVATE OLD ANSWER");
        Check(telemetry.Snapshot().Values is { OverlaySourceMatchesUpdate: true, OverlayTranslationMatchesUpdate: null, OverlayContentMismatches: 1 },
            "A source-only update treated deliberately retained translation as a new mismatch");
        telemetry.OverlayApplied(owner, false, source, answer, source, answer);
        Check(telemetry.Snapshot().Values is { Overlay: DebugReason.Hidden, OverlaySourceMatchesUpdate: true, OverlayTranslationMatchesUpdate: true }
            && mismatch.Events.First().Values.OverlayTranslationMatchesUpdate == false,
            "Visibility was confused with content equality or changed a historical event");
        string report = DebugReportWriter.Format(telemetry.Snapshot(), "test", "");
        Check(report.Contains("OverlayContentMismatches: 1") && report.Contains("OverlayTranslationMatchesUpdate: True")
            && !report.Contains("PRIVATE"), "Subtitle comparison was absent from the report or leaked content");
        telemetry.Stop(owner);
        telemetry.OverlayApplied(owner, true, source, answer, "", "");
        Check(telemetry.Snapshot().Values.OverlayContentMismatches == 1, "Stopped UI callback changed subtitle diagnostics");
        telemetry.BeginSession(new("ja", "NVL", false), new(), 500);
        telemetry.OverlayApplied(owner, true, source, answer, "", "");
        Check(telemetry.Snapshot().Values is { OverlaySourceMatchesUpdate: null, OverlayTranslationMatchesUpdate: null, OverlayContentMismatches: 0 },
            "Old session subtitle diagnostics contaminated the new session");
    }
    private static void ResponsePrivacyAndLifetime()
    {
        var telemetry = new DebugTelemetry();
        long owner = telemetry.BeginSession(new("ja", "NVL", false), new(), 500);
        string source = new('a', 64), result = new('b', 64), next = new('c', 64);
        telemetry.AiResponse(owner, source, result, 21, 30, 2);
        Check(telemetry.Snapshot().Values.LastAiResultRelation == DebugAiResultRelation.First, "First successful response was not distinguished");
        telemetry.AiResponse(owner, next, result, 22, 30, 2);
        var repeated = telemetry.Snapshot();
        Check(repeated.Values is { LastAiResultRelation: DebugAiResultRelation.DifferentSourceSameResult, AiRepeatedResultForChangedSource: 1 }, "Changed source/same model response was not distinguished");
        telemetry.AiResponse(owner, next, result, 22, 30, 2);
        Check(telemetry.Snapshot().Values is { LastAiResultRelation: DebugAiResultRelation.SameSourceSameResult, AiRepeatedResultForChangedSource: 1 }, "Same-source retranslation inflated changed-source repetition");
        string report = DebugReportWriter.Format(repeated, "1", "");
        Check(report.Contains("LastAiResultCharacters: 30") && report.Contains("LastAiResultLines: 2")
            && !report.Contains(source) && !report.Contains(result) && !report.Contains(next), "Response report lost counts or exposed hashes");
        telemetry.Stop(owner); telemetry.AiResponse(owner, result, next, 99, 99, 9);
        Check(telemetry.Snapshot().Values.LastAiResultCharacters == 30, "Stopped response overwrote completed metadata");
        telemetry.BeginSession(new("ja", "NVL", false), new(), 500);
        telemetry.AiResponse(owner, result, next, 99, 99, 9);
        Check(telemetry.Snapshot().Values is { LastAiResultRelation: DebugAiResultRelation.NotMeasured, LastAiResultCharacters: null, AiRepeatedResultForChangedSource: 0 }, "Response metadata crossed session boundaries");
    }
    private static void Collector()
    {
        var time = DateTimeOffset.UtcNow; var c = new DebugTelemetry(() => time);
        long s = c.BeginSession(new("en", "ADV", false), new(), 700);
        c.Record(s,DebugCode.CAPTURE_FAILED,DebugReason.CaptureUnavailable,v=>v with {CaptureStatus=DebugReason.CaptureUnavailable,CaptureError=DebugReason.CaptureUnavailable,LastError=DebugReason.CaptureUnavailable});
        c.CaptureCompleted(s,400,300);
        Check(c.Snapshot().Values.CaptureError==DebugReason.None && c.Snapshot().Values.LastError==DebugReason.CaptureUnavailable,"Recovered capture retained a current error or erased historical last error");
        c.Record(s,DebugCode.FRAME_CHANGED,DebugReason.Stabilizing,v=>v with {Decision=DebugReason.Stabilizing,FinalFrameAction=DebugFrameAction.CheckingStability});
        c.Reuse(s,DebugReason.VerifiedFrame);
        Check(c.Snapshot().Values is {Decision:DebugReason.Stabilizing,FinalFrameAction:DebugFrameAction.Reused,OcrInProgress:false},"Final reuse was confused with the raw pixel decision");
        var reuseEvent=c.Snapshot().Events.First(e=>e.Code==DebugCode.OCR_FRAME_REUSED);
        c.OcrStarted(s,DebugReason.Standard);
        Check(c.Snapshot().Values is {FinalFrameAction:DebugFrameAction.Recognizing,OcrInProgress:true,ReuseReason:DebugReason.None}
            && reuseEvent.Values.FinalFrameAction==DebugFrameAction.Reused,"New OCR retained stale reuse or changed a historical final outcome");
        c.OcrCompleted(s,new([],TimeSpan.Zero),1);
        Check(!c.Snapshot().Values.OcrInProgress && c.Snapshot().Values.FinalFrameAction==DebugFrameAction.OcrCompleted,"Completed OCR remained active");
        c.OcrStarted(s,DebugReason.Standard);c.Record(s,DebugCode.OCR_TEXT_REJECTED,DebugReason.OcrFailure);
        Check(!c.Snapshot().Values.OcrInProgress && c.Snapshot().Values.FinalFrameAction==DebugFrameAction.Watching,"Failed OCR remained active");
        // Outcome-only checks use their own session so cycle accounting below stays unchanged.
        s=c.BeginSession(new("en","ADV",false),new(),700);
        c.OcrStarted(s, DebugReason.Standard); c.OcrStarted(s, DebugReason.AdvRecovery); long first = c.Accept(s, false, false, false, false);
        c.Ai(s, first, false); c.Reuse(s, DebugReason.RepeatedAnimation); c.Reuse(s, DebugReason.VerifiedFrame);
        c.OcrStarted(s, DebugReason.Roi); c.SameSource(s);
        Check(c.Snapshot().Current is { OcrCalls: 3, AiRequests: 1, Reuses: 2 } && c.Snapshot().Current!.Id == first, "Same dialogue/reuse reset or split an OCR cycle");
        c.OcrStarted(s, DebugReason.Standard); c.Accept(s, false, true, false, false);
        Check(c.Snapshot().Current!.Id == first && c.Snapshot().Current!.OcrCalls == 4, "ADV typewriter continuation split the same cycle");
        c.OcrStarted(s, DebugReason.NvlIncremental); long second = c.Accept(s, false, false, false, true); c.Ai(s, second, true);
        Check(second != first && c.Snapshot().Previous!.OcrCalls == 4 && c.Snapshot().Current!.AiRequests == 0 && c.Snapshot().Current!.CacheHits == 1, "NVL append/cache counters are mixed");
        c.OcrStarted(s, DebugReason.NvlFullRetry); long page = c.Accept(s, true, false, true, true);
        Check(page != second && c.Snapshot().Current!.FullRetries == 1, "NVL page transition incorrectly merged");
        var historical = c.Snapshot().Events.First(e => e.Code == DebugCode.OCR_STARTED); c.Ai(s, page, false);
        Check(historical.Values.AiRequests != c.Snapshot().Values.AiRequests, "Past event details are live values");
        c.Record(s, DebugCode.WAIT_STABLE, DebugReason.CandidateQuietWait, v => v with { StableMs = 0, SettleMs = 700 });
        time += TimeSpan.FromMilliseconds(430);
        Check(c.Snapshot().Values.StableMs == 430 && c.Snapshot().Events[0].Values.StableMs == 0, "Live quiet time changed historical event measurements");
        c.Record(s, DebugCode.OCR_TEXT_REJECTED, DebugReason.CandidateSuperseded); time += TimeSpan.FromMilliseconds(500);
        Check(c.Snapshot().Values.StableMs == 430, "Ended candidate kept advancing its quiet timer");
        for (int i = 0; i < 80; i++) { time += TimeSpan.FromMilliseconds(1); c.Record(s, DebugCode.OVERLAY_UPDATED, DebugReason.Displayed); }
        Check(c.Snapshot().Events.Count == 50, "Event buffer is unbounded");
        time += TimeSpan.FromMinutes(4); Check(c.Snapshot().Events.Count == 0, "Old events were not expired on read");
        long next = c.BeginSession(new("ja", "NVL", false), new(), 700); c.Ai(s, page, false);
        Check(c.Snapshot().Session == next && c.Snapshot().Values.AiRequests == 0, "Old session changed new counters");
        c.Record(s, DebugCode.STALE_RESULT_DISCARDED, DebugReason.CandidateSuperseded);
        Check(c.Snapshot().Events[0].Session == s && c.Snapshot().Events[0].Values.Engine == "N/A", "Stale callback manufactured current session measurements");
        c.Stop(next);c.Reuse(next,DebugReason.VerifiedFrame);c.OcrStarted(next,DebugReason.Standard);
        Check(c.Snapshot().Values.FinalFrameAction==DebugFrameAction.Stopped && !c.Snapshot().Values.OcrInProgress,"Late diagnostic work reactivated a stopped display");
    }
    private static async Task TranslationErrors()
    {
        var telemetry = new DebugTelemetry(); var ai = new FailingAi();
        await using var pipe = new TranslationPipeline(new Engine(), ai, _ => Task.FromResult(Frame(1)), new("ja", "ADV", false),
            new() { Provider = TranslationProvider.Ollama, Model = "synthetic" }, new(), "", diagnostics: telemetry);
        pipe.Start(false); await pipe.RequestManualAsync(image: Frame(1));
        await Until(() => telemetry.Snapshot().Values.TranslationFailures == 1);
        var failed = telemetry.Snapshot();
        Check(failed.Values is { LastOllamaFailure: OllamaFailureKind.InternalConnectionClosed,
            LastOllamaFailureStage: OllamaFailureStage.Tokenize, LastOllamaHttpStatus: 500 }, "Pipeline lost typed Ollama error metadata");
        ai.Fail = false; await pipe.RequestManualAsync(image: Frame(2));
        await Until(() => telemetry.Snapshot().Values.TranslationState == DebugReason.Completed);
        for (int i = 0; i < 60; i++) telemetry.Record(pipe.DiagnosticSession, DebugCode.FRAME_CHANGED, DebugReason.Stabilizing);
        string report = DebugReportWriter.Format(telemetry.Snapshot(), "1", "");
        Check(report.Contains("TranslationFailures: 1") && report.Contains("LastOllamaFailureStage: Tokenize")
            && report.Contains("LastOllamaHttpStatus: 500") && !report.Contains("PRIVATE") && !report.Contains("127.0.0.1"),
            "Recovered translation/event eviction erased last failure or leaked the raw server error");
        telemetry.BeginSession(new("ja", "ADV", false), new(), 500);
        Check(telemetry.Snapshot().Values is { TranslationFailures: 0, LastOllamaFailure: OllamaFailureKind.None, LastOllamaHttpStatus: null },
            "Ollama failure metadata leaked into a new session");
    }
    private sealed class FailingAi : ITranslationService
    {
        internal bool Fail = true;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) => Fail
            ? Task.FromException<TranslationResult>(OllamaFailure.Classify(500, "Post \"http://127.0.0.1:9999/tokenize\": wsarecv forcibly closed PRIVATE"))
            : Task.FromResult(new TranslationResult("synthetic answer", TimeSpan.Zero));
    }
    private static async Task<(int, int, int)> Scenario(bool enabled)
    {
        var telemetry = enabled ? new DebugTelemetry() : null; var engine = new Engine(); var ai = new Ai(); var frame = Frame(1);
        var updates = new ConcurrentQueue<PipelineUpdate>();
        await using var pipe = new TranslationPipeline(engine, ai, _ => Task.FromResult(Volatile.Read(ref frame)), new("ja", "ADV", false), new(), new(), "",
            interval: TimeSpan.FromMilliseconds(10), settle: TimeSpan.Zero, diagnostics: telemetry) { SuppressRepeatedAnimations = false };
        pipe.Updated += updates.Enqueue; pipe.Start(false); await pipe.RequestTriggeredAsync(); await Until(() => ai.Calls == 1 && updates.Any(u => u.Translation is not null));
        frame = Frame(2); await pipe.RequestTriggeredAsync(); await Until(() => ai.Calls == 2 && updates.Any(u => u.Original == "synthetic sentence 160"));
        await pipe.RequestManualAsync(image: Frame(3)); await Until(() => ai.Calls == 3 && updates.Any(u => u.Original == "synthetic sentence 240"));
        await pipe.DisposeAsync();
        if (telemetry is not null) {
            var snap = telemetry.Snapshot();
            Check(snap.Values.LogicalOcrCalls == engine.Calls && updates.Max(u => u.OcrRequests) == engine.Calls, $"ROI full retry counter differs: telemetry={snap.Values.LogicalOcrCalls}, service={engine.Calls}, pipeline={updates.Max(u => u.OcrRequests)}");
            Check(snap.Cycles.Sum(c => c.OcrCalls) == engine.Calls && snap.Cycles.Sum(c => c.AiRequests) == ai.Calls, "Cycle counts diverged from actual OCR/provider calls");
            Check(snap.Events.Any(e => e.Reason == DebugReason.RoiFullRetry), "ROI fallback path not observed");
            Check(snap.Values is { LastAiResultRelation: DebugAiResultRelation.DifferentSourceSameResult, AiRepeatedResultForChangedSource: 2, LastAiResultLines: 1 },
                "Actual provider response did not reach equality diagnostics");
            string report = DebugReportWriter.Format(snap, "1", "");
            Check(!report.Contains("synthetic sentence") && !report.Contains("synthetic translation"), "Response diagnostics leaked text");
        }
        return (engine.Calls, engine.Crops, ai.Calls);
    }
    private static ImageFrame Frame(int phase)
    {
        var pixels = new byte[800 * 600 * 4];
        for (int y = 225; y < 235; y++) for (int x = 310; x < 330; x++) for (int c = 0; c < 3; c++) pixels[y * 3200 + x * 4 + c] = (byte)(phase * 80);
        return new(800, 600, 3200, pixels);
    }
    private sealed class Engine : IOcrService
    {
        public int Calls, Crops; public bool Empty, Fail;
        public bool CollectAttemptDiagnostics { set => throw new InvalidOperationException("synthetic diagnostic opt-in failure"); }
        public OcrCapabilities Capabilities => new(true, true, true, false, true);
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions o, CancellationToken t) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f, OcrOptions o, CancellationToken t) {
            Interlocked.Increment(ref Calls); if (Fail) throw new IOException("synthetic private exception that must not appear in reports");
            if (f.Width < 800) { Interlocked.Increment(ref Crops); return Task.FromResult(new OcrResult([], TimeSpan.Zero)); }
            int phase = f.Pixels.Span[225 * f.Stride + 310 * 4];
            return Task.FromResult(new OcrResult(Empty ? [] : [new("synthetic sentence " + phase, 1, new(300, 220, 300, 20))], TimeSpan.FromMilliseconds(3)) { DetectedRegions = Empty ? 0 : 1 });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        public int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest r, CancellationToken t) { Interlocked.Increment(ref Calls); return Task.FromResult(new TranslationResult("synthetic translation", TimeSpan.FromMilliseconds(2))); }
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Debug pipeline fixture timed out"); await Task.Delay(5); }
    }
}
