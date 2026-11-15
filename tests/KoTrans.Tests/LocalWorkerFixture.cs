using System.Text.Json;
using KoTrans.Core;

internal static class LocalWorkerFixture
{
    public static async Task Run(string[] args)
    {
        if (args[3].Contains("fail-all")) return;
        if (args[3].Contains("slow-load")) { Console.Error.WriteLine("loading"); await Task.Delay(1500); }
        if (args[1] == "vulkan") return; // Simulated native loader failure in its own process.
        async Task Reply(LocalWorkerReply value) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(); }
        await Reply(new("", Ready: true, Backend: "CPU"));
        CancellationTokenSource? cancellation = null; Task pending = Task.CompletedTask;
        while (await Console.In.ReadLineAsync() is { } line) {
            var request = JsonSerializer.Deserialize<LocalWorkerRequest>(line)!;
            if (request.Type == "shutdown") { cancellation?.Cancel(); break; }
            if (request.Type == "cancel") { cancellation?.Cancel(); continue; }
            await pending; cancellation?.Dispose(); cancellation = new(); var token = cancellation.Token;
            pending = Task.Run(async () => {
                try {
                    if (request.Prompt == "CRASH") Environment.Exit(17);
                    if (request.Prompt == "INPUT_LIMIT") { await Reply(new(request.Id, Error: "input_limit")); return; }
                    if (request.Prompt == "OUTPUT_LIMIT") { await Reply(new(request.Id, Text: "잘린 번역은 표시하면 안 됨", Stop: "limit")); return; }
                    if (request.Prompt == "INFERENCE_ERROR") { await Reply(new(request.Id, Error: "inference")); return; }
                    if (request.Prompt == "SLOW") { Console.Error.WriteLine("slow-start"); await Task.Delay(10000, token); }
                    await Reply(new(request.Id, Text: "모의 번역 " + Environment.ProcessId, Stop: "EOG"));
                } catch (OperationCanceledException) { await Reply(new(request.Id, Error: "cancelled")); }
            });
        }
        await pending; cancellation?.Dispose();
    }
}
