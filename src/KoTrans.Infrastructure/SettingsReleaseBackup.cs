using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KoTrans.Infrastructure;

// A recovery point independent of settings.json.bak and schema migration backups.
internal sealed class SettingsReleaseBackup(string directory, string releaseId)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root = Path.GetFullPath(directory);
    private bool prepared;
    private sealed record Entry(string Release, string File, string Sha256, long Bytes, DateTimeOffset CreatedUtc);
    private sealed record State(int Format, string CurrentRelease, Entry[] Backups);
    private static string Name(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl)) throw new InvalidDataException("릴리즈 식별자가 올바르지 않습니다.");
        var prefix = new string(id.Take(40).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return "settings-before-" + prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant() + ".json";
    }
    private static void Check(string path) => LocalModelInstaller.CheckPath(path);
    private static byte[] Read(string path, int maximum)
    {
        Check(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        LocalModelInstaller.CheckOpenedFile(input, path);
        if (input.Length > maximum) throw new InvalidDataException("설정 또는 백업 메타데이터의 크기 제한을 초과했습니다.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task WriteNew(string path, byte[] bytes, CancellationToken token)
    {
        Check(path);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            LocalModelInstaller.CheckOpenedFile(file, path);
            await file.WriteAsync(bytes, token).ConfigureAwait(false); file.Flush(true);
            file.Position = 0; var read = new byte[bytes.Length]; await file.ReadExactlyAsync(read, token).ConfigureAwait(false);
            if (!bytes.AsSpan().SequenceEqual(read)) throw new IOException("백업 내용을 확인할 수 없습니다.");
        }
    }
    public async Task EnsureAsync(CancellationToken token)
    {
        if (prepared) return;
        string fileName = Name(releaseId), settings = Path.Combine(root, "settings.json"), metadata = Path.Combine(root, "update-state.json"), backups = Path.Combine(root, "Backups");
        var gate = Gates.GetOrAdd(root, _ => new(1, 1)); await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Check(metadata); Check(settings);
            State state = File.Exists(metadata) ? JsonSerializer.Deserialize<State>(Read(metadata, 65536)) ?? throw new InvalidDataException("백업 메타데이터가 비어 있습니다.") : new(1, "", []);
            if (state.Format != 1 || state.Backups is null || state.Backups.Any(b => b is null) || state.Backups.Length > 5 || state.CurrentRelease is null || state.Backups.Select(b => b.Release).Distinct().Count() != state.Backups.Length)
                throw new InvalidDataException("지원하지 않는 백업 메타데이터입니다.");
            foreach (var item in state.Backups)
                if (item.File != Name(item.Release) || item.Bytes is < 0 or > SettingsStore.MaxFileBytes || item.Sha256 is null || item.Sha256.Length != 64)
                    throw new InvalidDataException("백업 메타데이터를 확인할 수 없습니다.");
            if (state.CurrentRelease == releaseId)
            {
                if (state.Backups.SingleOrDefault(b => b.Release == releaseId) is { } prior &&
                    !Matches(Path.Combine(backups, prior.File), prior)) throw new IOException("기존 버전 백업이 없거나 손상되었습니다.");
                prepared = true; return;
            }
            Directory.CreateDirectory(root);
            var entries = state.Backups.ToList();
            if (File.Exists(settings))
            {
                var original = Read(settings, SettingsStore.MaxFileBytes);
                Check(Path.Combine(backups, fileName)); Directory.CreateDirectory(backups);
                var prior = entries.SingleOrDefault(b => b.Release == releaseId);
                string target = Path.Combine(backups, fileName);
                if (prior is not null)
                {
                    if (!Matches(target, prior)) throw new IOException("기존 버전 백업이 손상되어 원본을 보호합니다.");
                }
                else
                {
                    if (File.Exists(target))
                    {
                        if (!original.AsSpan().SequenceEqual(Read(target, SettingsStore.MaxFileBytes))) throw new IOException("같은 이름의 다른 백업이 있어 원본을 보호합니다.");
                    }
                    else
                    {
                        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await WriteNew(temporary, original, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested(); Check(target); File.Move(temporary, target, false);
                    }
                    prior = new(releaseId, fileName, Hash(original), original.Length, DateTimeOffset.UtcNow);
                    if (!Matches(target, prior)) throw new IOException("버전 백업 무결성 검사에 실패했습니다.");
                    entries.Add(prior);
                }
            }
            var retained = entries.OrderByDescending(b => b.Release == releaseId).ThenByDescending(b => b.CreatedUtc).Take(5).ToArray();
            byte[] updated = JsonSerializer.SerializeToUtf8Bytes(new State(1, releaseId, retained));
            string staging = metadata + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await WriteNew(staging, updated, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); Check(metadata); File.Move(staging, metadata, true);
            if (!updated.AsSpan().SequenceEqual(Read(metadata, 65536))) throw new IOException("백업 상태 저장을 확인할 수 없습니다.");
            prepared = true;
            // Only delete files recorded by this feature, with matching names, bytes and hashes.
            foreach (var old in entries.Except(retained))
            {
                try { string path = Path.Combine(backups, old.File); if (Matches(path, old)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { throw new IOException("버전별 설정 백업을 준비하지 못했습니다. 기존 settings.json은 변경하지 않았습니다. 백업 폴더와 저장 공간·권한을 확인한 후 다시 실행하세요.", ex); }
        finally { gate.Release(); }
    }
    private static bool Matches(string path, Entry entry)
    {
        if (!File.Exists(path)) return false;
        var bytes = Read(path, SettingsStore.MaxFileBytes); return bytes.LongLength == entry.Bytes && Hash(bytes) == entry.Sha256;
    }
}
