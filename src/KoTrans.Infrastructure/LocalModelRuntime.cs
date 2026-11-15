using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using KoTrans.Core;
namespace KoTrans.Infrastructure;

public interface ILocalModelRuntime
{
    string Backend { get; }
    Task PrepareAsync(string model, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B);
    Task UnloadAsync(CancellationToken token, LocalTranslationModel? selection = null);
    Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B);
}
public sealed class LocalModelRuntime : ILocalModelRuntime, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task? stderr;
    private string? modelPath;
    private LocalTranslationModel selectedModel;
    private FileStream? modelLease;
    private bool disposed;
    public string Backend { get; private set; } = "";
    private readonly string worker;
    private readonly Action<string>? diagnostic;
    private readonly Func<ulong> availableMemory;
    public LocalModelRuntime(string? worker = null, Action<string>? diagnostic = null, Func<ulong>? availableMemory = null)
    {
        this.worker = worker ?? Path.Combine(AppContext.BaseDirectory, "local-worker", "KoTrans.LocalWorker.exe");
        this.diagnostic = diagnostic;
        this.availableMemory = availableMemory ?? AvailableCpuMemory;
    }
    public async Task PrepareAsync(string model, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await EnsureAsync(model, token, selection).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task EnsureAsync(string model, CancellationToken token, LocalTranslationModel selection)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var modelInfo = LocalTranslationModels.Get(selection);
        model = Path.GetFullPath(model);
        if (process is { HasExited: false } && modelPath == model && selectedModel == selection) return;
        await StopWorkerAsync().ConfigureAwait(false);
        modelPath = null; Backend = "";
        LocalModelInstaller.CheckPath(worker);
        if (!File.Exists(worker)) throw new TranslationException("로컬 번역 실행 파일이 누락되었습니다. 프로그램 업데이트를 확인해 주세요.");
        LocalModelInstaller.CheckPath(model);
        // Hold the verified file against writes/replacement while native code uses its path.
        modelLease = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.Read);
        modelPath = model; selectedModel = selection;
        try
        {
            LocalModelInstaller.CheckOpenedFile(modelLease, model);
            foreach (var backend in new[] { "vulkan", "cpu" })
            {
                token.ThrowIfCancellationRequested();
                if (backend == "cpu" && availableMemory() < modelInfo.RequiredCpuMemory)
                    throw new TranslationException($"{modelInfo.DisplayName}의 CPU 실행에는 가용 RAM {modelInfo.RequiredCpuMemory / (1024UL * 1024 * 1024)}GiB 이상이 필요합니다. 다른 프로그램의 메모리 사용량을 확인해 주세요.");
                try
                {
                    var info = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(worker)! };
                    info.ArgumentList.Add("--backend"); info.ArgumentList.Add(backend); info.ArgumentList.Add("--model"); info.ArgumentList.Add(model);
                    process = Process.Start(info) ?? throw new IOException("Worker launch failed.");
                    var started = process;
                    stderr = Task.Run(async () => { while (await started.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) diagnostic?.Invoke(line); });
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    var ready = await ReadAsync(timeout.Token).ConfigureAwait(false);
                    if (!ready.Ready) throw new IOException("Native model load failed.");
                    Backend = ready.Backend ?? backend;
                    // Real model smoke test before declaring ready.
                    var smoke = await RequestAsync(HyMtPrompt.Build("Hello.", [], selection), timeout.Token).ConfigureAwait(false);
                    if (!smoke.Text.Any(c => c is >= '\uAC00' and <= '\uD7A3')) throw new IOException("Korean self-test failed.");
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or TranslationException)
                {
                    diagnostic?.Invoke("Local backend preparation failed: " + backend + "/" + ex.GetType().Name);
                    await StopProcessAsync().ConfigureAwait(false);
                }
            }
            throw new TranslationException("로컬 번역 엔진을 시작하지 못했습니다. 프로그램을 다시 시작하고, 계속 실패하면 로컬 AI 진단 기록을 확인해 주세요.");
        }
        catch { await StopWorkerAsync().ConfigureAwait(false); throw; }
    }
    private static ulong AvailableCpuMemory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref state) ? state.AvailablePhysical : 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
    private async Task<LocalWorkerReply> ReadAsync(CancellationToken token)
    {
        var line = await process!.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null || line.Length > 131072) throw new IOException("Local worker ended.");
        try { return JsonSerializer.Deserialize<LocalWorkerReply>(line) ?? throw new IOException("Invalid local reply."); }
        catch (JsonException ex) { throw new IOException("Invalid local reply.", ex); }
    }
    private Task WriteAsync(LocalWorkerRequest request, CancellationToken token) =>
        process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
    private async Task<TranslationResult> RequestAsync(string prompt, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await WriteAsync(new(id, "translate", prompt), token).ConfigureAwait(false);
        try
        {
            var reply = await ReadAsync(token).ConfigureAwait(false);
            if (reply.Id != id) throw new IOException("Local response generation mismatch.");
            if (reply.Error == "input_limit") throw new LocalTranslationLimitException(LocalTranslationLimit.Input);
            if (reply.Error is not null) throw new TranslationException("로컬 번역 실행 중 오류가 발생했습니다. 번역을 중지한 뒤 다시 시작해 주세요.");
            if (reply.Stop == "limit") throw new LocalTranslationLimitException(LocalTranslationLimit.Output);
            if (reply.Stop != "EOG") throw new TranslationException("로컬 번역 결과를 확인할 수 없습니다. 다시 시도해 주세요.");
            if (string.IsNullOrWhiteSpace(reply.Text) || reply.Text.Contains("<|", StringComparison.Ordinal))
                throw new TranslationException("로컬 번역 결과를 확인할 수 없습니다. 다시 시도해 주세요.");
            return new(reply.Text.Trim(), TimeSpan.FromMilliseconds(reply.Milliseconds), 0);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await WriteAsync(new(id, "cancel"), CancellationToken.None).ConfigureAwait(false);
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var reply = await ReadAsync(drain.Token).ConfigureAwait(false);
                if (reply.Id != id) await StopWorkerAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { await StopWorkerAsync().ConfigureAwait(false); }
            throw;
        }
    }
    public async Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (modelPath is null) throw new TranslationException("먼저 로컬 번역 준비를 완료해 주세요.");
            if (selectedModel != selection) throw new TranslationException("로컬 AI 모델이 변경되었습니다. 번역을 다시 시작해 주세요.");
            var selectedPath = modelPath;
            await EnsureAsync(selectedPath, token, selection).ConfigureAwait(false);
            return await RequestAsync(prompt, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { await StopWorkerAsync().ConfigureAwait(false); throw; }
        finally { gate.Release(); }
    }
    private async Task StopProcessAsync()
    {
        if (process is not { } owned) return;
        process = null;
        try
        {
            if (!owned.HasExited)
            {
                await owned.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new LocalWorkerRequest("", "shutdown"))).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await owned.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { owned.Kill(entireProcessTree: true); await owned.WaitForExitAsync().ConfigureAwait(false); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
        finally { owned.Dispose(); if (stderr is not null) await stderr.ConfigureAwait(false); stderr = null; }
    }
    private async Task StopWorkerAsync()
    {
        await StopProcessAsync().ConfigureAwait(false); modelLease?.Dispose(); modelLease = null;
    }
    public async Task UnloadAsync(CancellationToken token = default, LocalTranslationModel? selection = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            if (selection is not null && selectedModel != selection) return;
            await StopWorkerAsync().ConfigureAwait(false); modelPath = null; Backend = "";
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; await StopWorkerAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
public sealed class LocalTranslationPreparation(LocalModelInstaller installer, ILocalModelRuntime runtime, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
{
    public Task ReleaseAsync() => runtime.UnloadAsync(CancellationToken.None);
    public Task<bool> IsInstalledAsync(CancellationToken token = default) => installer.IsInstalledAsync(token);
    public async Task DeleteAsync(CancellationToken token = default)
    {
        await runtime.UnloadAsync(token, selection).ConfigureAwait(false);
        await installer.DeleteAsync(token).ConfigureAwait(false);
    }
    public async Task<bool> EnsureAsync(Func<Task<bool>> consent, IProgress<LocalPreparationProgress>? progress, CancellationToken token)
    {
        bool installed = await installer.IsInstalledAsync(token).ConfigureAwait(false);
        if (!installed && !await consent().ConfigureAwait(false)) return false;
        // A healthy installation never opens a download/progress window, even on a cold engine start.
        var installationProgress = installed ? null : progress;
        var model = installed ? installer.ModelPath : await installer.InstallAsync(installationProgress, token).ConfigureAwait(false);
        installationProgress?.Report(new("번역 엔진 준비 중"));
        await runtime.PrepareAsync(model, token, selection).ConfigureAwait(false);
        installationProgress?.Report(new(runtime.Backend == "CPU" ? "로컬 AI 번역 — CPU 모드 (번역이 느릴 수 있음)" : "번역 준비 완료"));
        return true;
    }
}
public static class LocalTranslationEnvironment
{
    public static LocalModelInstaller Installer { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoTrans2", "Models", "HyMT2"), diagnostic: LocalDiagnostic.Write);
    public static LocalModelRuntime Runtime { get; } = new(diagnostic: message => LocalDiagnostic.Write(message));
    public static LocalTranslationPreparation Preparation { get; } = new(Installer, Runtime);
    private static LocalModelInstaller LightInstaller { get; } = new(Path.Combine(Installer.DirectoryPath, "1.8B"), LocalModelSpec.HyMt2Light, diagnostic: LocalDiagnostic.Write);
    public static LocalModelInstaller InstallerFor(LocalTranslationModel selection) => selection switch {
        LocalTranslationModel.Standard7B => Installer, LocalTranslationModel.Lightweight18B => LightInstaller,
        _ => throw new InvalidDataException("지원되지 않는 로컬 AI 모델입니다.")
    };
    public static LocalTranslationPreparation PreparationFor(LocalTranslationModel selection) => new(InstallerFor(selection), Runtime, selection);
}
public static class LocalDiagnostic
{
    public static void Delete()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoTrans2", "Diagnostics", "local-ai.log");
        LocalModelInstaller.CheckPath(file); File.Delete(file);
    }
    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoTrans2", "Diagnostics");
            LocalModelInstaller.CheckPath(dir); Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "local-ai.log"); LocalModelInstaller.CheckPath(file);
            if (File.Exists(file) && new FileInfo(file).Length > 1048576) File.Delete(file);
            File.AppendAllText(file, DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
