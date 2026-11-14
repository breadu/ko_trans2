using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class StabilityTests
{
    private static int count;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
    private static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or TranslationException or ArgumentException) { count++; return; } throw new Exception("Invalid operation accepted"); }
    internal static async Task Run()
    {
        var clock = Stopwatch.StartNew(); var root = Path.Combine(Path.GetTempPath(), "KoTrans2.Stability", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Voice(root); await Audio(root); Models(root); Region(); Tensor(); await PipelineRegion(); await RepeatedLifetime(); await CleanupFailures(); }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} assertions (Stability), {clock.Elapsed.TotalMilliseconds:F0}ms. Synthetic files/Mock only; no desktop, models or audio decoding.");
    }
    private static async Task CleanupFailures()
    {
        var order = new List<int>();
        var errors = await ShutdownCleanup.RunAsync(new (string, Func<Task>)[] {
            ("first", () => { order.Add(1); throw new IOException("private detail"); }),
            ("second", async () => { await Task.Yield(); order.Add(2); throw new InvalidOperationException(); }),
            ("third", () => { order.Add(3); return Task.CompletedTask; })
        });
        Check(order.SequenceEqual(new[] { 1, 2, 3 }), "Shutdown failure skipped later cleanup");
        Check(errors.SequenceEqual(new[] { "first", "second" }), "Shutdown leaked exception details or lost failed stages");
        Check((await ShutdownCleanup.RunAsync(Array.Empty<(string, Func<Task>)>())).Count == 0, "Empty cleanup failed");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        var draining = ShutdownCleanup.RunAsync([("pending OCR", () => release.Task),
            ("dispose", () => { disposed = true; return Task.CompletedTask; })], name => slow.TrySetResult(name), TimeSpan.Zero);
        Check(await slow.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "pending OCR" && !disposed && !draining.IsCompleted,
            "Slow drain was hidden or released resources in use");
        release.SetResult();
        Check((await draining).Count == 0 && disposed, "Drained resources were not released");
    }
    private static byte[] Entry(string word, string path) => Encoding.Unicode.GetBytes(word).Concat(new byte[] { 0, 0, 0 }).Concat(Encoding.ASCII.GetBytes(path)).Append((byte)0).ToArray();
    private static void Voice(string root)
    {
        var folder = Path.Combine(root, "voice"); Directory.CreateDirectory(folder); var file = Path.Combine(folder, "word.idx");
        File.WriteAllBytes(file, Entry("Hello", "hello.mp3")); File.WriteAllBytes(Path.Combine(folder, "hello.mp3"), [1, 2, 3]);
        var cache = new VoiceIndexCache(); var first = cache.Get(folder);
        Check(first.Contains("HELLO") && ReferenceEquals(first, cache.Get(folder)), "Index not reused/case handling");
        Check(Enumerable.Range(0, 100).All(_ => ReferenceEquals(first, cache.Get(folder))), "Repeated index reparsed");
        File.WriteAllBytes(file, Entry("World", "hello.mp3")); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
        Check(!ReferenceEquals(first, cache.Get(folder)) && cache.Get(folder).Contains("world"), "Changed index stale");
        // Atomic replacement with preserved timestamp/length is detected by the file ID.
        var replacement = Path.Combine(folder, "new.idx"); File.WriteAllBytes(replacement, Entry("Other", "hello.mp3")); File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(file)); File.Move(replacement, file, true);
        Check(cache.Get(folder).Contains("Other"), "Replaced file identity ignored");
        var second = Path.Combine(root, "second"); Directory.CreateDirectory(second); File.WriteAllBytes(Path.Combine(second, "word.idx"), Entry("Second", "hello.mp3"));
        Check(cache.Get(second).Contains("Second") && !cache.Get(second).Contains("Other"), "Folder change reused index");
        var before = cache.Get(folder); cache.Invalidate(); Check(!ReferenceEquals(before, cache.Get(folder)), "Explicit invalidation");
        File.WriteAllBytes(file, [1, 2, 3]); Reject(() => cache.Get(folder)); File.Delete(file); Reject(() => cache.Get(folder));
        File.WriteAllBytes(file, Entry("Hello", "hello.mp3")); Check(cache.Get(folder).Contains("Hello"), "Error not recoverable");
        File.Delete(Path.Combine(folder, "hello.mp3")); Reject(() => cache.Get(folder).OpenAudio("Hello"));
        var entries = new[] { Entry("Hello", "hello.mp3"), Entry("World", "hello.mp3") };
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.UTF8, true)) {
            writer.Write("LNVI"u8); writer.Write((uint)entries.Length); writer.Write((uint)entries.Sum(e => e.Length));
            uint offset = 0; foreach (var entry in entries) { writer.Write(offset); offset += (uint)entry.Length; }
            foreach (var entry in entries) writer.Write(entry);
        }
        var lnvi = data.ToArray(); File.WriteAllBytes(file, lnvi);
        var headerIndex = cache.Get(folder);
        Check(headerIndex.Count == 2 && headerIndex.RejectedEntries == 0 && headerIndex.Contains("WORLD"), "Real LNVI header/offset table was parsed as word data");
        var brokenHeader = (byte[])lnvi.Clone(); brokenHeader[4] = 255; File.WriteAllBytes(file, brokenHeader);
        // Cache invalidation is stamp-based. Back-to-back equal-length writes can
        // share a filesystem timestamp; explicitly advance it for the parser test.
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(2)); Reject(() => cache.Get(folder));
        brokenHeader = (byte[])lnvi.Clone(); brokenHeader[12] = 1; File.WriteAllBytes(file, brokenHeader); Reject(() => cache.Get(folder));
        File.WriteAllBytes(file, "LNVI"u8.ToArray()); Reject(() => cache.Get(folder));
        File.WriteAllBytes(file, lnvi);
        var checkout = Path.Combine(root, "voice-checkout"); Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, "KoTrans.slnx"), "synthetic checkout marker");
        var checkoutVoice = Path.Combine(checkout, "voice"); Directory.CreateDirectory(checkoutVoice); File.WriteAllBytes(Path.Combine(checkoutVoice, "word.idx"), lnvi);
        var app = Path.Combine(checkout, "src", "KoTrans.App", "bin"); Directory.CreateDirectory(app);
        Check(VoiceDataPaths.FindDirectory(app) == checkoutVoice, "Development checkout voice folder was not found");
        var adjacent = Path.Combine(app, "voice"); Directory.CreateDirectory(adjacent);
        Check(VoiceDataPaths.FindDirectory(app) == checkoutVoice, "Empty adjacent voice folder hid valid checkout data");
        File.WriteAllBytes(Path.Combine(adjacent, "word.idx"), lnvi);
        Check(VoiceDataPaths.FindDirectory(app) == adjacent, "EXE-adjacent voice data lost priority");
        Check(VoiceDataPaths.FindDirectory(app, folder) == folder, "Existing explicit voice directory was ignored");
        Check(VoiceDataPaths.FindDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))) is null,
            "Voice discovery guessed an unrelated game or current working directory");
    }
    private static async Task Audio(string root)
    {
        var store = new AudioTemporaryFiles(Path.Combine(root, "audio"));
        var source = Path.Combine(root, "original.mp3"); File.WriteAllBytes(source, [4, 5, 6]);
        var path = await store.CopyAsync(new MemoryStream([4, 5, 6]), CancellationToken.None);
        var active = await store.CopyAsync(new MemoryStream([7, 8, 9]), CancellationToken.None);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { store.Release(path); Check(File.Exists(path) && store.PendingCount == 1, "Locked temporary file lost from cleanup queue"); }
        await store.RetryAsync(); Check(!File.Exists(path) && File.Exists(active), "Retry removed active playback or kept released file");
        store.Release(source); Check(File.ReadAllBytes(source).SequenceEqual(new byte[] { 4, 5, 6 }), "Original file modified");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await store.CopyAsync(new MemoryStream([1]), canceled.Token); throw new Exception("Copy cancellation ignored"); } catch (OperationCanceledException) { count++; }
        await store.DisposeAsync(); Check(!File.Exists(active) && store.PendingCount == 0, "Dispose leaked owned files");
        Check(Directory.GetFiles(Path.Combine(root, "audio"), "*.mp3", SearchOption.AllDirectories).Length == 0, "Canceled copy leaked file");
    }
    private static void Models(string root)
    {
        var checkout = Path.Combine(root, "repo"); var app = Path.Combine(checkout, "bin"); Directory.CreateDirectory(app); File.WriteAllText(Path.Combine(checkout, "KoTrans.slnx"), "synthetic");
        Check(ModelCatalog.FindDirectory(app, root, null) == Path.Combine(checkout, "models"), "Checkout discovery");
        var beside = Path.Combine(app, "models"); Directory.CreateDirectory(beside);
        Check(ModelCatalog.FindDirectory(app, root, null) == beside, "EXE adjacent models ignored");
        Check(ModelCatalog.FindDirectory(app, root, checkout) == checkout, "Explicit models override lost");
        Check(ModelCatalog.FindDirectory(Path.Combine(root, "isolated"), root, null) == Path.Combine(root, "KoTrans2", "models"), "User fallback lost");
        var commonRoot=Path.Combine(root,"common");var commonModels=Path.Combine(commonRoot,"models");var gitDir=Path.Combine(commonRoot,".git","worktrees","linked");
        Directory.CreateDirectory(commonModels);Directory.CreateDirectory(gitDir);File.WriteAllText(Path.Combine(commonRoot,"KoTrans.slnx"),"synthetic");File.WriteAllText(Path.Combine(gitDir,"commondir"),"../..");
        var linked=Path.Combine(root,"linked");var linkedApp=Path.Combine(linked,"bin","Release");Directory.CreateDirectory(linkedApp);File.WriteAllText(Path.Combine(linked,"KoTrans.slnx"),"synthetic");
        File.WriteAllText(Path.Combine(linked,".git"),"gitdir: "+Path.GetRelativePath(linked,gitDir));
        Check(ModelCatalog.FindDirectory(linkedApp,root,null)==commonModels,"Linked checkout did not reuse its own common models");
        Check(ModelCatalog.FindDirectory(linkedApp,root,Path.Combine(root,"explicit-missing"))==Path.Combine(root,"explicit-missing"),"Explicit override silently fell back to common models");
        File.WriteAllText(Path.Combine(gitDir,"commondir"),"missing-common");
        Check(ModelCatalog.FindDirectory(linkedApp,root,null)==Path.Combine(linked,"models"),"Malformed common directory selected another project");
        File.WriteAllText(Path.Combine(gitDir,"commondir"),"../..");
        var localModels=Path.Combine(linked,"models");Directory.CreateDirectory(localModels);
        Check(ModelCatalog.FindDirectory(linkedApp,root,null)==localModels,"Local models lost priority over common checkout");
        var adjacentModels=Path.Combine(linkedApp,"models");Directory.CreateDirectory(adjacentModels);
        Check(ModelCatalog.FindDirectory(linkedApp,root,null)==adjacentModels,"Adjacent models lost priority in linked checkout");
        Check(ModelCatalog.PreferredExecution(beside).DetectorRoot is null && ModelCatalog.PreferredExecution(beside).RecognizerRoot is null, "Missing optional fast set did not preserve precision defaults");
        var fast = Path.Combine(beside, "fast"); Directory.CreateDirectory(Path.Combine(fast, "det")); Directory.CreateDirectory(Path.Combine(fast, "ja"));
        File.WriteAllText(Path.Combine(fast, "det", "manifest.json"), "not loaded by discovery"); File.WriteAllText(Path.Combine(fast, "ja", "manifest.json"), "not loaded by discovery");
        var preferred = ModelCatalog.PreferredExecution(beside);
        Check(preferred.DetectorRoot == fast && preferred.RecognizerRoot == fast, "Optional fast discovery loaded or ignored paths");
        var det = Path.Combine(beside, "det"); Directory.CreateDirectory(det); var model = Path.Combine(det, "model.onnx"); File.WriteAllBytes(model, [1, 2, 3]);
        var spec = new ModelSpec { Kind = "detection", Decoder = "DB", Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))) };
        File.WriteAllText(Path.Combine(det, "manifest.json"), JsonSerializer.Serialize(spec)); Check(ModelCatalog.Load(beside, "det").Path == model, "Model manifest verification");
        File.AppendAllText(model, "corrupt"); Reject(() => ModelCatalog.Load(beside, "det"));
        File.WriteAllText(Path.Combine(det, "manifest.json"), "{broken}"); Reject(() => ModelCatalog.Load(beside, "det"));
    }
    private static void Region()
    {
        var original = new ImageFrame(800, 600, 3208, new byte[3208 * 600]);
        var result = new OcrResult([new("text", 1, new(100, 400, 500, 30))], TimeSpan.Zero);
        var tracker = new OcrRegionTracker(); tracker.Observe(original, result, false);
        var pixels = original.Pixels.ToArray(); pixels[410 * 3208 + 300 * 4] = 255; var changed = new ImageFrame(800, 600, 3208, pixels);
        var crop = tracker.Select(changed, false); Check(crop is not null, "Stable dialogue ROI not selected");
        Check(tracker.Select(changed, true) is null, "Manual OCR cropped");
        var cropped = OcrRegionTracker.Crop(changed, crop!.Value);
        Check(cropped.Pixels.Span[(410 - crop.Value.Y) * cropped.Stride + (300 - crop.Value.X) * 4] == 255, "Padded stride crop corrupted");
        var local = new OcrResult([new("text", 1, new(60, 60, 500, 30))], TimeSpan.Zero) { DetectedBoxes = [new(60, 60, 500, 30)] };
        Check(!OcrRegionTracker.NeedsFullSearch(local, crop.Value), "Safe ROI falsely clipped");
        Check(OcrRegionTracker.Restore(local, crop.Value, original, new("en", "ADV", false)).Lines[0].Bounds == result.Lines[0].Bounds, "Original coordinates lost");
        Check(OcrRegionTracker.NeedsFullSearch(new([], TimeSpan.Zero), crop.Value), "Empty crop failed to reacquire");
        Check(OcrRegionTracker.NeedsFullSearch(local with { DetectedBoxes = [new(0, 0, 20, 20)] }, crop.Value), "Boundary text accepted");
        pixels = changed.Pixels.ToArray(); pixels[3] = 255; Check(tracker.Select(new(800, 600, 3208, pixels), false) is not null, "Alpha changed ROI decision");
        pixels[0] = 1; Check(tracker.Select(new(800, 600, 3208, pixels), false) is null, "New outside text missed");
        Check(tracker.Select(new(400, 300, 1600, new byte[1600 * 300]), false) is null, "Resize retained ROI");
        for (int i = 0; i < 4; i++) tracker.Observe(changed, result, true);
        Check(tracker.Select(changed, false) is null, "Periodic full search missing");
        Check(OcrExecutionOptions.DetectionSize(320, 96, 960, true) == (960, 288) && OcrExecutionOptions.DetectionSize(320, 96, 960, false) == (320, 96), "Resize diagnostic contracts");
    }
    private sealed class NoOcr : IOcrService
    {
        public bool Disposed; public OcrState State => OcrState.NotConfigured;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => throw new Exception("No OCR");
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) => throw new Exception("No OCR");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static void Tensor()
    {
        using var backing = new OpenCvSharp.Mat(5, 260, OpenCvSharp.MatType.CV_8UC3);
        using var image = new OpenCvSharp.Mat(backing, new OpenCvSharp.Rect(2, 1, 256, 3));
        for (int y = 0; y < 3; y++) for (int x = 0; x < 256; x++) image.Set(y, x, new OpenCvSharp.Vec3b((byte)x, (byte)(255 - x), (byte)(x / 2)));
        var spec = new ModelSpec { Mean = [.485f, .456f, .406f], Std = [.229f, .224f, .225f] };
        var tensor = PaddleOcrService.TensorFrom(image, 288, 3, spec);
        bool equal = true;
        for (int y = 0; y < 3; y++) for (int x = 0; x < 288; x++) for (int c = 0; c < 3; c++)
        {
            float reference = x >= 256 ? 0 : (image.At<OpenCvSharp.Vec3b>(y, x)[c] / 255f - spec.Mean[c]) / spec.Std[c];
            equal &= BitConverter.SingleToInt32Bits(tensor[0, c, y, x]) == BitConverter.SingleToInt32Bits(reference);
        }
        Check(equal, "Tensor optimization changed BGR/normalization/padding/non-contiguous stride");
    }
    private static async Task RepeatedLifetime()
    {
        var ocr = new NoOcr(); var process = Process.GetCurrentProcess(); process.Refresh(); int before = process.HandleCount;
        for (int i = 0; i < 50; i++)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pipeline = new TranslationPipeline(ocr, new ImmediateTranslation(), _ => throw new Exception("No capture"), new("en", "ADV", false), new(), new(), "synthetic", settle: TimeSpan.Zero);
            pipeline.Updated += update => { if (!string.IsNullOrEmpty(update.Translation)) done.TrySetResult(); };
            pipeline.Start(false); pipeline.SubmitClipboard("Synthetic dialogue"); await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pipeline.DisposeAsync(); await pipeline.DisposeAsync();
        }
        process.Refresh(); Console.WriteLine($"50 pipeline start/stop: handles {before} -> {process.HandleCount}; managed bytes {GC.GetTotalMemory(false)} (not a long-session proof)");
        Check(!ocr.Disposed, "Pipeline disposed shared OCR");
    }
    private sealed class ImmediateTranslation : ITranslationService
    {
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new TranslationResult("합성 번역", TimeSpan.Zero, 0)); }
    }
    private sealed class RegionOcr : IOcrService
    {
        // Exercise the legacy physical crop path; incremental NVL has separate tests.
        public OcrCapabilities Capabilities => OcrCapabilities.Paddle with { LocalParagraph = false };
        public readonly System.Collections.Concurrent.ConcurrentQueue<int> Widths = new();
        public bool Clip;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            Widths.Enqueue(frame.Width); var box = frame.Width == 800 ? new PixelRect(100, 400, 500, 30) : new PixelRect(Clip ? 0 : 60, 60, 500, 30);
            return Task.FromResult(new OcrResult([new("text" + Widths.Count, 1, box)], TimeSpan.Zero) { DetectedBoxes = [box] });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task PipelineRegion()
    {
        var current = new ImageFrame(800, 600, 3200, new byte[3200 * 600]); var ocr = new RegionOcr();
        var updates = new System.Collections.Concurrent.ConcurrentQueue<PipelineUpdate>();
        await using var pipeline = new TranslationPipeline(ocr, new ImmediateTranslation(), _ => Task.FromResult(Volatile.Read(ref current)), new("en", "NVL", false), new(), new(), "synthetic", TimeSpan.FromMilliseconds(10), TimeSpan.Zero);
        pipeline.Updated += updates.Enqueue; pipeline.Start(true);
        static async Task Until(Func<bool> condition) { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.ElapsedMilliseconds > 3000) throw new TimeoutException("Pipeline ROI test"); await Task.Delay(5); } }
        await Until(() => updates.Any(u => u.Original == "text1" && !string.IsNullOrEmpty(u.Translation)));
        ImageFrame Change(byte value, bool outside = false)
        {
            var bytes = current.Pixels.ToArray();
            for (int y = 400; y < 430; y++) for (int x = 100; x < 600; x++) for (int c = 0; c < 3; c++) bytes[y * 3200 + x * 4 + c] = value;
            if (outside) for (int y = 20; y < 80; y++) for (int x = 20; x < 300; x++) for (int c = 0; c < 3; c++) bytes[y * 3200 + x * 4 + c] = value;
            return new(800, 600, 3200, bytes);
        }
        Volatile.Write(ref current, Change(90));
        await Until(() => updates.Any(u => u.Original == "text2" && !string.IsNullOrEmpty(u.Translation)));
        Check(ocr.Widths.ToArray().SequenceEqual(new[] { 800, 620 }), "Pipeline did not send actual ROI pixels");
        Check(updates.Last(u => u.Ocr is not null).Ocr!.Lines[0].Bounds == new PixelRect(100, 400, 500, 30), "Pipeline failed coordinate restoration");
        ocr.Clip = true; Volatile.Write(ref current, Change(160));
        await Until(() => updates.Any(u => u.Original == "text4" && !string.IsNullOrEmpty(u.Translation)));
        Check(ocr.Widths.ToArray().TakeLast(2).SequenceEqual(new[] { 620, 800 }), "Clipped ROI did not retry full frame");
        ocr.Clip = false; Volatile.Write(ref current, Change(220, true));
        await Until(() => updates.Any(u => u.Original == "text5" && !string.IsNullOrEmpty(u.Translation)));
        Check(ocr.Widths.Last() == 800, "New dialogue outside ROI lost");
        await pipeline.RequestManualAsync(); await Until(() => ocr.Widths.Count == 6);
        Check(ocr.Widths.Last() == 800, "Manual pipeline OCR cropped");
    }
}
