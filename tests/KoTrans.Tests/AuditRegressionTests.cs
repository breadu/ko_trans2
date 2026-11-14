using System.Collections.Concurrent;
using KoTrans.Core;
using KoTrans.Infrastructure;

// Focused audit regressions. No desktop, models, audio, real keys or external HTTP.
internal static class AuditRegressionTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Audit regression: " + message); }

    public static int SettingsAndLaunch()
    {
        int count = 0;
        foreach (var failure in new Exception[] { new IOException("synthetic lock"), new UnauthorizedAccessException("synthetic access") })
        {
            using var gate = new SemaphoreSlim(1, 1); gate.Wait();
            bool deleted = false;
            try
            {
                try { throw new InvalidDataException("original save failure"); }
                finally { SettingsStore.FinishSave("synthetic.tmp", _ => { deleted = true; throw failure; }, gate); }
            }
            catch (InvalidDataException ex) { Check(ex.Message == "original save failure", "Cleanup masked save error"); count++; }
            Check(deleted && gate.Wait(0), "Cleanup failure stranded save gate"); count++; gate.Release();
        }
        using (var gate = new SemaphoreSlim(1, 1))
        {
            gate.Wait(); SettingsStore.FinishSave(null, _ => throw new Exception("Successful save must not delete"), gate);
            Check(gate.Wait(0), "Successful save stranded gate"); count++; gate.Release();
            gate.Wait();
            try { SettingsStore.FinishSave("synthetic.tmp", _ => throw new InvalidOperationException("unexpected"), gate); }
            catch (InvalidOperationException) { count++; }
            Check(gate.Wait(0), "Unexpected cleanup error stranded gate"); count++; gate.Release();
        }
        var root = Path.Combine(Path.GetTempPath(), "KoTrans2.Arguments", Guid.NewGuid().ToString("N"));
        var production = Path.Combine(root, "production"); var isolated = Path.Combine(root, "isolated");
        foreach (var args in new string[][] {
            ["--mock-ai"], ["--mock-ai", "--test-data"], ["--test-data"],
            ["--test-data", "--mock-ai"], ["--test-data", " "], ["--test-data", "bad\0path"],
            ["--test-data", isolated, "--test-data", isolated], ["--mock-ai", "--test-data", production],
            ["--test-data", Path.Combine(production, "child", "..")]
        })
        {
            bool rejected = false;
            try { AppLaunchOptions.Parse(args, production); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Malformed/test-production arguments accepted"); count++;
        }
        var normal = AppLaunchOptions.Parse(["--dev-run"], production);
        Check(!normal.MockAi && normal.DataDirectory == production, "Normal launch changed"); count++;
        var qa = AppLaunchOptions.Parse(["--dev-run", "--test-data", isolated, "--mock-ai"], production);
        Check(qa.MockAi && qa.DataDirectory == Path.GetFullPath(isolated), "Valid isolated Mock rejected"); count++;
        Check(!Directory.Exists(root), "Argument parsing touched the filesystem"); count++;
        return count;
    }

    public static async Task TranslationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "KoTrans2.Audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "characters.json");
            foreach (var text in new[] { "[null]", "[1]", "[\"text\"]", "[[]]", "[true]", "[{\"name\":\"A\",\"korean_name\":\"에이\"},null]" })
            {
                File.WriteAllText(file, text); bool rejected = false;
                try { CharacterDictionary.Load(file); }
                catch (TranslationException) { rejected = true; }
                Check(rejected, "Non-object dictionary item escaped safe error boundary");
                Check(File.ReadAllText(file) == text, "Invalid dictionary file was rewritten");
            }
            File.WriteAllText(file, "[{\"name\":\"A\",\"korean_name\":\"에이\",\"info\":\"first\",\"description\":\"second\"}]");
            Check(CharacterDictionary.Load(file).Single().Description == "first / second", "Valid dictionary compatibility changed");
        }
        finally { Directory.Delete(root, true); }
        await RejectedClipboard(new string('x', 16001), PipelineState.Error);
        await RejectedClipboard("   ", PipelineState.Watching);
        Console.WriteLine("PASS: audit dictionary + rejected/empty clipboard regressions. Mock only.");
    }

    private static async Task RejectedClipboard(string invalid, PipelineState expectedState)
    {
        var ai = new DelayedAi(); var context = new TranslationSession();
        var updates = new ConcurrentQueue<PipelineUpdate>();
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new TranslationPipeline(new NoOcr(), ai, _ => throw new Exception("No capture expected"),
            new("en", "ADV", false), new() { Model = "synthetic" }, context, "synthetic", settle: TimeSpan.Zero);
        pipeline.Updated += update => { updates.Enqueue(update); if (update.Translation == "Tsecond") fresh.TrySetResult(); };
        try
        {
            pipeline.Start(false); pipeline.SubmitClipboard("first");
            await ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var oldVersion = pipeline.Version;
            pipeline.SubmitClipboard(invalid);
            Check(pipeline.Version > oldVersion, "Rejected text did not invalidate old request");
            Check(updates.Any(u => u.Version == pipeline.Version && u.State == expectedState && u.Original == "" && u.Translation == ""), "Rejected text did not clear stale UI");
            ai.Release.TrySetResult();
            pipeline.SubmitClipboard("second");
            await fresh.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!updates.Any(u => u.Translation == "Tfirst"), "Late first result escaped generation guard");
            Check(context.History.Count == 1 && context.History[0].Source == "second", "Canceled result contaminated context");
            Check(ai.Calls == 2, "Rejected text caused extra AI request");
        }
        finally { ai.Release.TrySetResult(); await pipeline.DisposeAsync(); }
    }
    private sealed class DelayedAi : ITranslationService
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            { Entered.TrySetResult(); await Release.Task; } // Deliberately ignores cancellation.
            return new("T" + request.Text, TimeSpan.Zero, 0);
        }
    }
    private sealed class NoOcr : IOcrService
    {
        public OcrState State => OcrState.NotConfigured;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => throw new Exception("No OCR expected");
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) => throw new Exception("No OCR expected");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
