using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

// Explicit opt-in only. Never called from Background FAST and never downloads a model.
internal static class LocalModelIntegrationTests
{
    public static async Task Run(string model, string worker, string output, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
    {
        var spec = LocalModelSpec.For(selection);
        await using (var stream = File.OpenRead(model))
            if (stream.Length != spec.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The supplied Lab model does not match the frozen Hy-MT2 model.");
        var log = new List<string>(); var translations = new List<object>(); var clock = Stopwatch.StartNew();
        await using var runtime = new LocalModelRuntime(worker, line => { lock (log) log.Add(line); });
        await runtime.PrepareAsync(model, default, selection); var load = clock.Elapsed.TotalMilliseconds;
        var cases = new[] {
            ("en01", "Hold your fire!", "A guard stops archers from shooting at an ally."),
            ("ja01", "まだ言ってない。", "姉に退学のことを話したか、と友達に聞かれた。"),
            ("en02", "We need two more seals, not two more keys.", "The door requires magical seals. The party already has all the keys.") };
        foreach (var (id, source, background) in cases) {
            var result = await runtime.TranslateAsync(HyMtPrompt.BuildWithBackground(source, background, selection), default, selection);
            if (!result.Text.Any(c => c is >= '\uAC00' and <= '\uD7A3')) throw new Exception("Non-Korean translation");
            translations.Add(new { id, source, background, result.Text, milliseconds = result.Elapsed.TotalMilliseconds });
        }
        // Ordinary app prompt uses accepted originals only, without invented Lab scene information.
        var ordinary = await new HyMtTranslationService(runtime, selection).TranslateAsync(new("まだ言ってない。", "", [new("お姉さんには、退学のことを話した？", "ignored")], new() { Provider = TranslationProvider.HyMtLocal, LocalModel = selection }), default);
        translations.Add(new { id = "actual-history", ordinary.Text, milliseconds = ordinary.Elapsed.TotalMilliseconds });
        using (var cts = new CancellationTokenSource()) {
            var pending = runtime.TranslateAsync(HyMtPrompt.Build(string.Join(" ", Enumerable.Repeat("This is a very long story about a friend.", 30)), [], selection), cts.Token, selection);
            cts.CancelAfter(30);
            try { await pending; throw new Exception("Cancellation not observed"); } catch (OperationCanceledException) { }
        }
        var restarted = await runtime.TranslateAsync(HyMtPrompt.Build("Hello.", [], selection), default, selection);
        string backend = runtime.Backend;
        var processes = Process.GetProcessesByName("KoTrans.LocalWorker");
        long resident = processes.Sum(p => p.WorkingSet64);
        string[] LocalModules(Process process) {
            string root = Path.GetDirectoryName(Path.GetFullPath(worker))! + Path.DirectorySeparatorChar;
            var modules = process.Modules.Cast<ProcessModule>().Where(m => m.ModuleName.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)
                || m.ModuleName.ToLowerInvariant() is "llama.dll" or "coreclr.dll" or "msvcp140.dll" or "vcruntime140.dll" or "vcruntime140_1.dll" or "vcomp140.dll").ToArray();
            if (modules.Length < 7 || modules.Any(m => !m.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Worker depended on an external .NET/native/VC/OpenMP runtime: " + string.Join("; ", modules.Select(m => m.FileName)));
            return modules.Select(m => Path.GetRelativePath(root, m.FileName)).ToArray();
        }
        var gpuModules = processes.SelectMany(LocalModules).ToArray();
        await runtime.DisposeAsync();
        foreach (var p in processes) { if (!p.HasExited) throw new Exception("Worker not released on disposal"); p.Dispose(); }
        // CPU native library is loaded separately, after the GPU worker and its model have exited.
        var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(worker)! };
        foreach (string arg in new[] { "--backend", "cpu", "--model", model }) start.ArgumentList.Add(arg);
        using var cpu = Process.Start(start)!;
        var errors = cpu.StandardError.ReadToEndAsync();
        var ready = JsonSerializer.Deserialize<LocalWorkerReply>((await cpu.StandardOutput.ReadLineAsync())!)!;
        if (!ready.Ready || ready.Backend != "CPU") throw new Exception("CPU native load failed");
        await cpu.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new LocalWorkerRequest("cpu", "translate", HyMtPrompt.Build("Hello.", [], selection))));
        var cpuReply = JsonSerializer.Deserialize<LocalWorkerReply>((await cpu.StandardOutput.ReadLineAsync())!)!;
        if (cpuReply.Stop != "EOG" || cpuReply.Text is null || !cpuReply.Text.Any(c => c is >= '\uAC00' and <= '\uD7A3')) throw new Exception("CPU translation failed");
        var cpuModules = LocalModules(cpu); long cpuResident = cpu.WorkingSet64;
        await cpu.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new LocalWorkerRequest("", "shutdown")));
        await cpu.WaitForExitAsync(); string cpuLog = await errors;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { selection, backend, loadMilliseconds = load, workerWorkingSetBytes = resident,
            gpuModules, cpuModules, cpuWorkingSetBytes = cpuResident, translations, cancelThenRestart = restarted.Text, disposed = true, cpu = cpuReply, native = log, cpuNative = cpuLog }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: verified Lab GGUF, GPU/CPU native translation, repeated requests, cancellation/restart and worker disposal. " + output);
    }
}
