using System.Buffers.Binary;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class DirectMlTests
{
    internal static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
        var options = new OcrOptions("ja", "ADV", false, UseDirectMl: true);
        var frame = new ImageFrame(80, 40, 320, new byte[12800]);
        var result = new OcrResult([new("確認用の文章", .99f, new(0, 0, 40, 20))], TimeSpan.FromMilliseconds(1));
        Check(!JsonSerializer.Deserialize<Profile>("{}")!.OcrGpuAcceleration && !JsonSerializer.Deserialize<OcrOptions>("{\"Language\":\"ja\",\"ReadingMode\":\"ADV\",\"Vertical\":false}")!.UseDirectMl, "Legacy profile/QA did not default to CPU");
        var settings = new AppSettings(); settings.Validate(); settings.DefaultProfile.OcrGpuAcceleration = true;
        var copy = settings.CreateGameProfile("GPU copy");
        Check(copy.OcrGpuAcceleration && copy.OcrOptions().UseDirectMl, "Profile duplication lost GPU preference");
        Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.Profiles.All(p => p.OcrGpuAcceleration), "Preference not restored");
        foreach (string id in new[] { "default", "v6-medium-full", "v5-det-v6-medium-rec" }) Check(DirectMlProtocol.Supports(options with { ModelPresetId = id }), "Supported Paddle missing");
        foreach (var bad in new[] { options with { EngineId = "meiki" }, options with { ModelPresetId = "meiki-tiny-det-v6-medium-rec" }, options with { ModelPresetId = "future" } }) Check(!DirectMlProtocol.Supports(bad), "Unvalidated model opted into GPU");
        var gpu = new Fake { Result = result }; var cpu = new Fake { Result = result };
        await using (var service = new DirectMlOcrService(() => cpu, () => gpu)) {
            var ordinary = await service.RecognizeAsync(frame, options, default);
            Check(ordinary.Attempts is null, "Ordinary execution collected debug attempt metadata");
            service.CollectAttemptDiagnostics = true;
            Check(gpu.Calls == 1 && cpu.Calls == 0 && service.ExecutionStatus.Backend == "DirectML", "GPU success used CPU");
            gpu.Error = new DirectMlFailureException("worker-exited"); int notices = 0;
            service.ExecutionChanged += s => { if (s.IsFallback) notices++; };
            var recovered = await service.RecognizeAsync(frame, options, default); await service.RecognizeAsync(frame, options, default);
            Check(recovered.Attempts == new OcrAttemptCounts(1, 1), "Logical fallback call lost GPU/CPU attempt distinction");
            Check(recovered.Text == result.Text && recovered.Execution.IsFallback && gpu.Calls == 2 && gpu.Disposed && cpu.Calls == 2 && notices == 1 && service.ExecutionStatus.IsFallback, "Failure did not recover once/stay CPU/notify once");
        }
        foreach (string reason in new[] { "worker-missing", "startup-timeout", "request-timeout", "DllNotFound", "device-removed", "output-contract" }) {
            var failedGpu = new Fake { Error = new DirectMlFailureException(reason) }; var fallback = new Fake { Result = result };
            await using var service = new DirectMlOcrService(() => fallback, () => failedGpu);
            await service.InitializeAsync(options, default); await service.RecognizeAsync(frame, options, default);
            Check(failedGpu.Initializations == 1 && failedGpu.Calls == 0 && fallback.Initializations == 1 && fallback.Calls == 1 && failedGpu.Disposed, "Init fallback broken: " + reason);
        }
        foreach (Exception error in new Exception[] { new OperationCanceledException(), new OcrInitializationException("damaged-model"), new ArgumentException("bad-input") }) {
            var failedGpu = new Fake { Error = error }; var unused = new Fake { Result = result };
            await using var service = new DirectMlOcrService(() => unused, () => failedGpu);
            try { await service.RecognizeAsync(frame, options, default); throw new Exception("Failure accepted"); } catch (Exception ex) when (ReferenceEquals(ex, error)) { checks++; }
            Check(unused.Calls == 0 && !service.ExecutionStatus.IsFallback, "Cancellation/input/model failure triggered CPU");
            if (error is OcrInitializationException) Check(service.ExecutionStatus.Backend == "Error" && service.State == OcrState.Failed, "Model error incorrectly displayed as running/preparing GPU");
        }
        var blockedGpu = new Fake { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously), Result = result }; int creates = 0;
        var serialized = new DirectMlOcrService(() => new Fake(), () => { creates++; return blockedGpu; });
        var one = serialized.RecognizeAsync(frame, options, default); var two = serialized.RecognizeAsync(frame, options, default);
        Check(blockedGpu.Calls == 1 && creates == 1, "Concurrent Run admitted");
        var disposal = serialized.DisposeAsync().AsTask(); Check(!disposal.IsCompleted, "Disposed running GPU");
        blockedGpu.Pending.SetResult(result); await Task.WhenAll(one, two, disposal);
        Check(blockedGpu.MaxConcurrent == 1 && blockedGpu.Disposed, "Run/disposal serialization failed");
        var mockAi = new Ai(); var pipelineCpu = new Fake { Result = result }; var pipelineGpu = new Fake { Error = new DirectMlFailureException("worker-killed") };
        await using (var service = new DirectMlOcrService(() => pipelineCpu, () => pipelineGpu))
        await using (var pipeline = new TranslationPipeline(service, mockAi, _ => Task.FromResult(frame), options, new(), new TranslationSession(), "test")) {
            pipeline.Start(false); await pipeline.RequestManualAsync(); await Wait(() => mockAi.Calls == 1); await pipeline.RequestManualAsync();
            await Wait(() => pipelineCpu.Calls >= 2); await Task.Delay(30);
            Check(pipeline.IsRunning && mockAi.Calls == 1 && pipelineGpu.Calls == 1 && pipelineCpu.Calls == 2, $"Fallback pipeline counts: running={pipeline.IsRunning}, ai={mockAi.Calls}, gpu={pipelineGpu.Calls}, cpu={pipelineCpu.Calls}");
            Check(pipelineGpu.LastOptions!.BypassOcrCaches && pipelineCpu.LastOptions!.BypassOcrCaches,"Manual cache bypass was lost on GPU or CPU fallback");
        }
        using (var stream = new MemoryStream()) {
            await DirectMlProtocol.WriteAsync(stream, new DirectMlRequest(Guid.NewGuid(), 1, "initialize", options), DirectMlProtocol.MaxHeader, default);
            stream.Position = 0; var message = await DirectMlProtocol.ReadAsync<DirectMlRequest>(stream, DirectMlProtocol.MaxHeader, default);
            Check(message.Options == options && message.Id != Guid.Empty, "IPC options roundtrip lost data");
        }
        using(var stream=new MemoryStream()) {
            var fresh=options with {BypassOcrCaches=true};
            await DirectMlProtocol.WriteAsync(stream,new DirectMlRequest(Guid.NewGuid(),1,"recognize",fresh),DirectMlProtocol.MaxHeader,default);
            stream.Position=0;var message=await DirectMlProtocol.ReadAsync<DirectMlRequest>(stream,DirectMlProtocol.MaxHeader,default);
            Check(message.Options.BypassOcrCaches,"Manual bypass was lost across worker IPC");
        }
        using(var stream=new MemoryStream()) {
            var stats=new OcrRecognitionCacheStats(3,5,OcrCacheMissReason.Evicted) {Misses=new(Empty:1,InputNotCached:1,Evicted:1)};
            await DirectMlProtocol.WriteAsync(stream,new DirectMlReply(Guid.NewGuid(),1,Result:result with {RecognitionCache=stats}),DirectMlProtocol.MaxResponse,default);
            stream.Position=0;var message=await DirectMlProtocol.ReadAsync<DirectMlReply>(stream,DirectMlProtocol.MaxResponse,default);
            Check(message.Result!.RecognitionCache==stats,"Recognition cache measurements were lost across worker IPC");
        }
        foreach (int length in new[] { -1, 0, DirectMlProtocol.MaxResponse + 1, int.MaxValue }) {
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length); using var stream = new MemoryStream(header);
            try { await DirectMlProtocol.ReadAsync<DirectMlReply>(stream, DirectMlProtocol.MaxResponse, default); throw new Exception("Unbounded read"); } catch (InvalidDataException) { checks++; }
        }
        var identity = new OcrModelIdentity("test", "1", "hash", "", "");
        var valid = result with { Models = new(identity, identity, true, 2) { FlushDenormalsToZero = true } };
        DirectMlWorkerClient.ValidateResult(valid, frame, options); checks++;
        foreach (var invalid in new[] { valid with { Lines = [new("bad", float.NaN, new(0,0,10,10))] }, valid with { DetectedBoxes = [new(79, 0, 20, 10)] }, valid with { Models = null },
            valid with {RecognitionCache=new(-1,2)},valid with {RecognitionCache=new(1,0)},
            valid with {RecognitionCache=new(0,1) {Misses=null!}} }) {
            try { DirectMlWorkerClient.ValidateResult(invalid, frame, options); throw new Exception("Invalid GPU result accepted"); } catch (DirectMlFailureException) { checks++; }
        }
        var factory = new OcrServiceFactory(OcrEngineCatalog.Default, "no-models", new("never-start.exe"));
        Check(!factory.CheckModels(options with { EngineId = "meiki" }).Ready, "Retired engine must be rejected before GPU routing");
        await using var off = factory.Create(options with { UseDirectMl = false });
        Check(off.ExecutionStatus == OcrExecutionStatus.Cpu, "CPU-only factory selected GPU");
        var contexts = new TranslationContexts(); var profile = Guid.NewGuid();
        Check(ReferenceEquals(contexts.Get(profile, options, new(), "test"), contexts.Get(profile, options with { UseDirectMl = false }, new(), "test")), "GPU preference reset translation context");
        Console.WriteLine($"PASS: {checks} DirectML opt-in/IPC/fallback/pipeline assertions (Mock, no models/GPU/AI).");
    }
    private static async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(3000); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class Fake : IOcrService
    {
        internal Exception? Error;
        internal OcrResult Result = new([], TimeSpan.Zero);
        internal TaskCompletionSource<OcrResult>? Pending;
        internal int Calls, Initializations, Concurrent, MaxConcurrent;
        internal bool Disposed;
        internal OcrOptions? LastOptions;
        public OcrState State => OcrState.Ready;
        public OcrExecutionStatus ExecutionStatus => new("DirectML", "test");
        public Task InitializeAsync(OcrOptions options, CancellationToken token) { Initializations++; if (Error is not null) throw Error; return Task.CompletedTask; }
        public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        { LastOptions=options;Calls++; MaxConcurrent = Math.Max(MaxConcurrent, ++Concurrent); try { if (Error is not null) throw Error; return Pending is null ? Result : await Pending.Task.WaitAsync(token); } finally { Concurrent--; } }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Ai : ITranslationService
    {
        internal int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token) { Calls++; return Task.FromResult(new TranslationResult("확인", TimeSpan.Zero)); }
    }
}
