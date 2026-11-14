using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoTrans.Core;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 4 || args[0] != "--backend" || args[2] != "--model" || args[1] is not ("vulkan" or "cpu")) return 2;
bool gpu = args[1] == "vulkan";
var outputGate = new SemaphoreSlim(1, 1);
async Task Reply(LocalWorkerReply reply)
{
    await outputGate.WaitAsync();
    try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(reply)); await Console.Out.FlushAsync(); }
    finally { outputGate.Release(); }
}
try
{
    // Load the packaged CRT explicitly: the native loader otherwise prefers System32.
    // The dependency order avoids any reliance on an installed VC/OpenMP runtime.
    // These process-lifetime module references are released when this owned worker exits.
    foreach (var file in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll" })
        NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, file));
    string nativeLogTail = ""; bool fullGpuOffload = false;
    NativeLibraryConfig.All.WithCuda(false).WithVulkan(gpu).WithAutoFallback(false)
        .WithLogCallback((_, message) => {
            lock (outputGate) {
                nativeLogTail += message;
                fullGpuOffload |= Regex.IsMatch(nativeLogTail, @"offloaded\s+([1-9]\d*)/\1 layers to GPU");
                if (nativeLogTail.Length > 512) nativeLogTail = nativeLogTail[^512..];
            }
            Console.Error.Write(message);
        });
    var config = new ModelParams(args[3]) { ContextSize = 2048, BatchSize = 256, UBatchSize = 256, GpuLayerCount = gpu ? 99 : 0, Threads = 8, BatchThreads = 8 };
    using var weights = LLamaWeights.LoadFromFile(config);
    if (weights.Metadata["general.architecture"] != "hunyuan-dense") throw new InvalidDataException("Unexpected model architecture.");
    using var context = weights.CreateContext(config);
    if (gpu && !fullGpuOffload)
        throw new InvalidOperationException("Full GPU offload was not confirmed.");
    await Reply(new("", Ready: true, Backend: gpu ? "GPU" : "CPU"));
    CancellationTokenSource? current = null;
    Task running = Task.CompletedTask;
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (line.Length > 131072) break;
        var request = JsonSerializer.Deserialize<LocalWorkerRequest>(line)!;
        if (request.Type == "cancel") { current?.Cancel(); continue; }
        if (request.Type == "shutdown") { current?.Cancel(); break; }
        if (request.Type != "translate") { await Reply(new(request.Id, Error: "Worker busy.")); continue; }
        await running;
        current?.Dispose(); current = new();
        var token = current.Token;
        running = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                if (request.MaxTokens is < 1 or > 512) throw new ArgumentException("Invalid output budget.");
                if (request.Prompt.Length > 24000) { await Reply(new(request.Id, Error: "input_limit")); return; }
                context.NativeHandle.MemoryClear();
                var input = context.Tokenize(request.Prompt, addBos: false, special: true).ToList();
                if (input.Count + request.MaxTokens > context.ContextSize) { await Reply(new(request.Id, Error: "input_limit")); return; }
                using var sampler = new DefaultSamplingPipeline { Temperature = .7f, TopP = .6f, TopK = 20, MinP = 0, RepeatPenalty = 1.05f, PresencePenalty = 0, Seed = 4271 };
                var batch = new LLamaBatch();
                var (code, _, next) = await context.DecodeAsync(input, LLamaSeqId.Zero, batch, 0, token);
                if (code != DecodeResult.Ok) throw new InvalidOperationException("Prompt decode failure.");
                var decoder = new StreamingTokenDecoder(context) { DecodeSpecialTokens = true };
                var text = new StringBuilder();
                bool ended = false; int outputTokens = 0; double firstToken = 0;
                for (int i = 0; i < request.MaxTokens; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var sampled = sampler.Sample(context.NativeHandle, batch.TokenCount - 1);
                    if (sampled.IsEndOfGeneration(weights.Vocab)) { ended = true; break; }
                    if (outputTokens++ == 0) firstToken = clock.Elapsed.TotalMilliseconds;
                    decoder.Add(sampled); text.Append(decoder.Read());
                    if (i == request.MaxTokens - 1) break;
                    batch.Clear(); batch.Add(sampled, next++, LLamaSeqId.Zero, true);
                    if (await context.DecodeAsync(batch, token) != DecodeResult.Ok) throw new InvalidOperationException("Token decode failure.");
                }
                token.ThrowIfCancellationRequested();
                await Reply(new(request.Id, Text: text.ToString(), Stop: ended ? "EOG" : "limit", Milliseconds: clock.Elapsed.TotalMilliseconds,
                    OutputTokens: outputTokens, InputTokens: input.Count, FirstTokenMilliseconds: firstToken));
            }
            catch (OperationCanceledException) { await Reply(new(request.Id, Error: "cancelled")); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine(ex.GetType().Name); await Reply(new(request.Id, Error: "inference")); }
        });
    }
    current?.Cancel(); await running; current?.Dispose();
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine(ex.GetType().Name); await Reply(new("", Error: "load")); return 1;
}
