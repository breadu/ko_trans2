using System.Collections.Concurrent;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class NvlPerformanceTests
{
    internal static async Task Run()
    {
        var original = Frame("途中の本文");
        var changed = Frame("途中の本文から追加");
        var gate = new RealtimeFrameGate();
        gate.Evaluate(original); gate.Evaluate(original);
        // OCR has already inspected the complete changed image, including pixels
        // outside the newly adopted text ROI. They must not trigger another read.
        var pixels = changed.Pixels.ToArray();
        for (int y = 230; y < 240; y++) for (int x = 320; x < 340; x++)
            pixels[y * changed.Stride + x * 4] = 240;
        var inspected = new ImageFrame(changed.Width, changed.Height, changed.Stride, pixels);
        gate.Observe(new(Rows("途中の本文から追加"), TimeSpan.Zero), inspected, fullCoverage: true);
        int repeatedProbes = Enumerable.Range(0, 20).Count(_ => gate.Evaluate(inspected) != FrameDecision.Unchanged);

        var engine = new Engine(); var updates = new ConcurrentQueue<PipelineUpdate>();
        ImageFrame current = original;
        await using var pipeline = new TranslationPipeline(engine, null, _ => Task.FromResult(Volatile.Read(ref current)),
            new("ja", "NVL", false), new(), new(), "", interval: TimeSpan.FromMilliseconds(15),
            settle: TimeSpan.FromMilliseconds(30), translationEnabled: false);
        pipeline.Updated += updates.Enqueue;
        pipeline.Start(false);
        await pipeline.RequestManualAsync();
        await Until(() => updates.Any(u => u.Original?.EndsWith("途中の本文") == true));
        int before = engine.Calls;
        Volatile.Write(ref current, changed);
        await pipeline.RequestTriggeredAsync();
        await Until(() => updates.Any(u => u.Original?.EndsWith("途中の本文から追加") == true));
        pipeline.Stop(); await pipeline.DisposeAsync();
        int additionCalls = engine.Calls - before;
        Console.WriteLine($"NVL synthetic performance: already inspected frame probes={repeatedProbes}; tight-boundary append OCR calls={additionCalls}.");
        if (repeatedProbes != 0) throw new Exception("Full OCR left the old full-frame baseline and scheduled redundant probes");
        if (additionCalls != 1) throw new Exception("Predictably clipped NVL search performed partial OCR followed by full OCR");
        if (updates.Last(u => u.Original is not null).Original != NvlText.DisplayLines(Rows("途中の本文から追加")))
            throw new Exception("NVL optimization lost retained text or the appended sentence");
        BaselineSafety(original, inspected);
        Console.WriteLine("PASS: NVL redundant full probes, tight crop, retained/appended text and full/partial coverage safety. Synthetic frames and Mock OCR only.");
    }

    private static void BaselineSafety(ImageFrame original, ImageFrame inspected)
    {
        var result = new OcrResult(Rows("途中の本文から追加"), TimeSpan.Zero);
        var partial = new RealtimeFrameGate();
        partial.Evaluate(original); partial.Evaluate(original);
        partial.Observe(result, inspected, fullCoverage: false);
        if (!Enumerable.Range(0, 10).Any(_ => partial.Evaluate(inspected) == FrameDecision.Probe && partial.ProbeOutside))
            throw new Exception("Partial OCR certified uninspected pixels outside its search");
        partial.Observe(result, inspected, fullCoverage: true);
        if (Enumerable.Range(0, 20).Any(_ => partial.Evaluate(inspected) != FrameDecision.Unchanged))
            throw new Exception("Successful full retry did not advance the full-frame baseline");

        var pixels = inspected.Pixels.ToArray();
        for (int y = 265; y < 275; y++) for (int x = 340; x < 355; x++)
            for (int c = 0; c < 3; c++) pixels[y * inspected.Stride + x * 4 + c] = 255;
        var outside = new ImageFrame(inspected.Width, inspected.Height, inspected.Stride, pixels);
        if (!Enumerable.Range(0, 10).Any(_ => partial.Evaluate(outside) == FrameDecision.Probe && partial.ProbeOutside))
            throw new Exception("Updated full baseline hid a new line outside the known text ROI");

        partial.Observe(result, inspected, fullCoverage: true);
        pixels = inspected.Pixels.ToArray();
        for (int y = 67; y < 73; y++) for (int x = 65; x < 85; x++)
            for (int c = 0; c < 3; c++) pixels[y * inspected.Stride + x * 4 + c] = 0;
        var body = new ImageFrame(inspected.Width, inspected.Height, inspected.Stride, pixels);
        if (!Enumerable.Range(0, 3).Any(_ => partial.Evaluate(body) == FrameDecision.Ready))
            throw new Exception("Updated full baseline hid a genuine body change");
        partial.Reset();
        if (partial.Evaluate(original) != FrameDecision.Stabilizing || partial.Evaluate(original) != FrameDecision.Ready)
            throw new Exception("Session reset retained an OCR-confirmed full baseline");
        var resized = new ImageFrame(200, 200, 800, new byte[160000]);
        if (partial.Evaluate(resized) != FrameDecision.Stabilizing || partial.Evaluate(resized) != FrameDecision.Ready)
            throw new Exception("Resize reused the previous full baseline");
    }

    private static OcrLine[] Rows(string tail) => [
        new("前の確定した文章。", 1, new(50, 40, 250, 20)),
        new(tail, 1, new(50, 63, tail.Length * 12, 20))];
    private static ImageFrame Frame(string tail)
    {
        var pixels = new byte[400 * 300 * 4];
        pixels[3] = (byte)tail.Length;
        foreach (var row in Rows(tail))
            for (int y = row.Bounds.Y + 3; y < row.Bounds.Y + row.Bounds.Height - 3; y++)
                for (int x = row.Bounds.X + 3; x < row.Bounds.X + row.Bounds.Width - 3; x++)
                    for (int c = 0; c < 3; c++) pixels[(y * 400 + x) * 4 + c] = 230;
        return new(400, 300, 1600, pixels);
    }
    private sealed class Engine : IOcrService
    {
        internal int Calls;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var rows = Rows(frame.Pixels.Span[3] == "途中の本文".Length ? "途中の本文" : "途中の本文から追加");
            if (options.SearchArea is { } area) rows = rows.Where(l => l.Bounds.Y >= area.Y).ToArray();
            return Task.FromResult(new OcrResult(rows, TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!done()) await Task.Delay(5, timeout.Token);
    }
}
