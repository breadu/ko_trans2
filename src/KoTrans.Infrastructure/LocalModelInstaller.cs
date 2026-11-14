using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using KoTrans.Core;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KoTrans.Infrastructure;

public sealed record LocalModelSpec(string Revision, string FileName, long Bytes, string Sha256, Uri Source)
{
    public static LocalModelSpec HyMt2Light { get; } = new("a0c709d9fac510f2c807aa3af52872340dc37a4a",
        "Hy-MT2-1.8B-Q4_K_M.gguf", 1133080448, "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699",
        new("https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q4_K_M.gguf"));
    public static LocalModelSpec For(LocalTranslationModel model) => model switch {
        LocalTranslationModel.Standard7B => HyMt2, LocalTranslationModel.Lightweight18B => HyMt2Light,
        _ => throw new InvalidDataException("지원되지 않는 로컬 AI 모델입니다.")
    };
    public static LocalModelSpec HyMt2 { get; } = new("ab8472660ac61fac25f1af43fac2599d52a8a775",
        "Hy-MT2-7B-Q4_K_M.gguf", 4624648896, "9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b",
        new("https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/ab8472660ac61fac25f1af43fac2599d52a8a775/Hy-MT2-7B-Q4_K_M.gguf"));
}
public sealed record LocalPreparationProgress(string Stage, long Received = 0, long Total = 0)
{
    public double Percent => Total > 0 ? Math.Clamp(100d * Received / Total, 0, 100) : 0;
}
public sealed class LocalModelInstaller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    private readonly Func<long> freeSpace;
    private readonly Action<string>? diagnostic;
    public LocalModelSpec Spec { get; }
    public string DirectoryPath { get; }
    public string ModelPath => Path.Combine(DirectoryPath, Spec.FileName);
    private string PartPath => ModelPath + ".part";
    private string ResumePath => ModelPath + ".resume.json";
    private string StatePath => ModelPath + ".verified.json";
    private sealed record Verified(string Revision, string Sha256, long Bytes, long Modified);
    private sealed record Resume(string Revision, string? ETag);
    public LocalModelInstaller(string directory, LocalModelSpec? spec = null, HttpClient? client = null, Func<long>? freeSpace = null, Action<string>? diagnostic = null)
    {
        DirectoryPath = Path.GetFullPath(directory); Spec = spec ?? LocalModelSpec.HyMt2;
        if (Path.GetFileName(Spec.FileName) != Spec.FileName || Spec.Bytes < 4 || Spec.Sha256.Length != 64)
            throw new ArgumentException("Invalid local model descriptor.");
        this.client = client ?? DownloadClient; this.diagnostic = diagnostic;
        this.freeSpace = freeSpace ?? (() => new DriveInfo(Path.GetPathRoot(DirectoryPath)!).AvailableFreeSpace);
    }
    internal static void CheckPath(string path)
    {
        for (var entry = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; entry is not null;
            entry = entry is DirectoryInfo d ? d.Parent : ((FileInfo)entry).Directory)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("로컬 번역 폴더에 연결 경로가 있어 안전하게 사용할 수 없습니다.");
    }
    internal static void CheckOpenedFile(FileStream stream, string expected)
    {
        var final = new StringBuilder(32768);
        uint size = GetFinalPathNameByHandle(stream.SafeFileHandle, final, (uint)final.Capacity, 0);
        if (size == 0 || size >= final.Capacity) throw new IOException("로컬 번역 파일 경로를 확인할 수 없습니다.");
        string actual = final.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new IOException("로컬 번역 파일의 연결 경로는 사용할 수 없습니다.");
        CheckPath(expected);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    private void Paths()
    {
        CheckPath(DirectoryPath); Directory.CreateDirectory(DirectoryPath);
        foreach (var path in new[] { ModelPath, PartPath, ResumePath, StatePath, StatePath + ".tmp", ResumePath + ".tmp", Path.Combine(DirectoryPath, "install.lock") })
            CheckPath(path);
    }
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 8192) return default;
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            CheckOpenedFile(stream, path);
            if (stream.Length > 8192) return default;
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (JsonException) { return default; }
    }
    private static async Task Write<T>(string path, T value, CancellationToken token)
    {
        CheckPath(path + ".tmp");
        await using (var stream = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, true)) {
            CheckOpenedFile(stream, path + ".tmp"); stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token).ConfigureAwait(false);
        }
        CheckPath(path); File.Move(path + ".tmp", path, true);
    }
    public async Task<bool> IsInstalledAsync(CancellationToken token = default)
    {
        var gate = Gates.GetOrAdd(DirectoryPath, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await IsInstalledCoreAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<bool> IsInstalledCoreAsync(CancellationToken token)
    {
        CheckPath(ModelPath); CheckPath(StatePath);
        if (!File.Exists(ModelPath)) return false;
        var info = new FileInfo(ModelPath);
        if (info.Length != Spec.Bytes) return false;
        await using var stream = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        CheckOpenedFile(stream, ModelPath);
        var magic = new byte[4];
        if (await stream.ReadAsync(magic, token).ConfigureAwait(false) != 4 || !magic.AsSpan().SequenceEqual("GGUF"u8)) return false;
        var state = Read<Verified>(StatePath);
        if (state is not null && state.Revision == Spec.Revision && state.Sha256 == Spec.Sha256 &&
            state.Bytes == info.Length && state.Modified == info.LastWriteTimeUtc.Ticks) return true;
        stream.Position = 0;
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (!digest.Equals(Spec.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        await Write(StatePath, new Verified(Spec.Revision, Spec.Sha256, info.Length, info.LastWriteTimeUtc.Ticks), token).ConfigureAwait(false);
        return true;
    }
    public async Task<string> InstallAsync(IProgress<LocalPreparationProgress>? progress, CancellationToken token)
    {
        var gate = Gates.GetOrAdd(DirectoryPath, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Paths();
            // A second app/process cannot write this installation concurrently.
            await using var ownership = new FileStream(Path.Combine(DirectoryPath, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CheckOpenedFile(ownership, Path.Combine(DirectoryPath, "install.lock"));
            if (await IsInstalledCoreAsync(token).ConfigureAwait(false)) return ModelPath;
            for (int attempt = 0; ; attempt++)
            {
                try { await DownloadAsync(progress, token).ConfigureAwait(false); break; }
                catch (Exception ex) when (attempt < 2 && ex is HttpRequestException or IOException)
                {
                    diagnostic?.Invoke($"Local download retry {attempt + 1}: {ex.GetType().Name}; HRESULT=0x{ex.HResult:X8}; HTTP={(ex as HttpRequestException)?.StatusCode}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token).ConfigureAwait(false);
                }
            }
            progress?.Report(new("다운로드 검증 중"));
            Paths();
            await using (var input = new FileStream(PartPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            {
                CheckOpenedFile(input, PartPath);
                if (input.Length != Spec.Bytes) throw new TranslationException("모델 파일 크기가 올바르지 않습니다. 다시 시도해 주세요.");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                if (!hash.Equals(Spec.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostic?.Invoke("Local model checksum mismatch: " + hash);
                    input.Close(); File.Delete(PartPath); File.Delete(ResumePath);
                    throw new TranslationException("모델 파일 검증에 실패했습니다. 다시 다운로드해 주세요.");
                }
                input.Position = 0; var magic = new byte[4]; await input.ReadExactlyAsync(magic, token).ConfigureAwait(false);
                if (!magic.AsSpan().SequenceEqual("GGUF"u8)) throw new TranslationException("모델 파일 형식을 확인할 수 없습니다.");
            }
            token.ThrowIfCancellationRequested(); Paths();
            // Same-volume atomic replacement; the old model survives every failure before this point.
            File.Move(PartPath, ModelPath, true);
            var file = new FileInfo(ModelPath);
            await Write(StatePath, new Verified(Spec.Revision, Spec.Sha256, file.Length, file.LastWriteTimeUtc.Ticks), token).ConfigureAwait(false);
            File.Delete(ResumePath);
            return ModelPath;
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(CancellationToken token = default)
    {
        var gate = Gates.GetOrAdd(DirectoryPath, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckPath(Path.Combine(DirectoryPath, "install.lock"));
            if (!Directory.Exists(DirectoryPath)) return;
            var files = new[] { ModelPath, PartPath, ResumePath, StatePath, StatePath + ".tmp", ResumePath + ".tmp" };
            // Validate every managed path before deleting anything. Never recursively delete a folder.
            foreach (var file in files) CheckPath(file);
            string lockPath = Path.Combine(DirectoryPath, "install.lock");
            await using (var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                CheckOpenedFile(ownership, lockPath); token.ThrowIfCancellationRequested();
                foreach (var file in files) { CheckPath(file); File.Delete(file); }
            }
            CheckPath(lockPath); File.Delete(lockPath);
            CheckPath(Path.Combine(DirectoryPath, "placeholder"));
            if (!Directory.EnumerateFileSystemEntries(DirectoryPath).Any()) Directory.Delete(DirectoryPath, recursive: false);
        }
        finally { gate.Release(); }
    }
    private async Task DownloadAsync(IProgress<LocalPreparationProgress>? progress, CancellationToken token)
    {
        Paths(); var resume = Read<Resume>(ResumePath);
        long offset = File.Exists(PartPath) ? new FileInfo(PartPath).Length : 0;
        if (offset > Spec.Bytes || resume?.Revision != Spec.Revision || string.IsNullOrEmpty(resume.ETag)) offset = 0;
        if (offset == Spec.Bytes) return;
        long required = Spec.Bytes - offset + Math.Min(512L * 1024 * 1024, Spec.Bytes);
        if (freeSpace() < required) throw new TranslationException($"로컬 번역을 설치할 공간이 부족합니다. 약 {required / 1_000_000_000d:F1}GB를 확보해 주세요.");
        using var response = await SendAsync(offset, resume?.ETag, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(PartPath); File.Delete(ResumePath); throw new IOException("Resume range rejected."); }
        response.EnsureSuccessStatusCode();
        bool append = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        var tag = response.Headers.ETag?.ToString();
        if (append)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || range.To != Spec.Bytes - 1 || range.Length != Spec.Bytes ||
                tag != resume?.ETag) { File.Delete(PartPath); File.Delete(ResumePath); throw new IOException("Resume identity changed."); }
        }
        else
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Unexpected download status.");
            offset = 0;
        }
        var length = response.Content.Headers.ContentLength;
        if (length is not null && length != Spec.Bytes - offset) throw new IOException("Unexpected content length.");
        await Write(ResumePath, new Resume(Spec.Revision, tag), token).ConfigureAwait(false);
        Paths();
        await using var output = new FileStream(PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 131072, true);
        CheckOpenedFile(output, PartPath);
        if (!append) output.SetLength(0);
        else if (output.Length != offset) throw new IOException("Partial file changed.");
        output.Position = offset;
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[131072];
        progress?.Report(new("모델 다운로드 중", offset, Spec.Bytes));
        int count; var nextReport = Environment.TickCount64;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (offset + count > Spec.Bytes) throw new IOException("Download exceeds pinned length.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); offset += count;
            if (Environment.TickCount64 >= nextReport)
            { progress?.Report(new("모델 다운로드 중", offset, Spec.Bytes)); nextReport = Environment.TickCount64 + 150; }
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        if (offset != Spec.Bytes) throw new IOException("Download interrupted.");
        progress?.Report(new("모델 다운로드 중", offset, Spec.Bytes));
    }
    private async Task<HttpResponseMessage> SendAsync(long offset, string? etag, CancellationToken token)
    {
        var uri = Spec.Source;
        for (int redirect = 0; redirect < 8; redirect++)
        {
            if (!AllowedDownloadUri(uri)) throw new TranslationException("공식 모델 다운로드 주소를 확인할 수 없습니다.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.TryAddWithoutValidation("If-Range", etag);
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new HttpRequestException("Missing redirect.");
                uri = new Uri(uri, location); continue;
            }
            return response;
        }
        throw new HttpRequestException("Too many model redirects.");
    }
    public static bool AllowedDownloadUri(Uri uri) => uri.Scheme == "https" && uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) && (uri.Host == "huggingface.co" || uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
        uri.Host == "hf.co" || uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase) ||
        uri.Host == "xethub.hf.co" || uri.Host.EndsWith(".xethub.hf.co", StringComparison.OrdinalIgnoreCase));
}
