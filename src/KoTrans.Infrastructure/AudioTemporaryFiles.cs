using System.Collections.Generic;

namespace KoTrans.Infrastructure;

// Keeps only paths created by this instance. Never enumerates/deletes somebody else's files.
public sealed class AudioTemporaryFiles : IAsyncDisposable
{
    private readonly string directory;
    private readonly HashSet<string> owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    public AudioTemporaryFiles(string? root = null) => directory = Path.Combine(root ?? Path.Combine(Path.GetTempPath(), "KoTrans2.Audio"), Guid.NewGuid().ToString("N"));
    public int PendingCount { get { lock (sync) return pending.Count; } }
    public async Task<string> CopyAsync(Stream source, CancellationToken token)
    {
        lock (sync) { if (owned.Count >= 64) throw new IOException("Windows가 이전 임시 발음 파일을 사용 중입니다. 잠시 후 다시 시도하세요."); }
        Directory.CreateDirectory(directory); CheckPath(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            lock (sync) owned.Add(path);
            await source.CopyToAsync(output, token);
            return path;
        }
        catch { Release(path); throw; }
    }
    public void Release(string? path)
    {
        if (path is null) return;
        lock (sync)
        {
            if (!owned.Contains(path)) return;
            pending.Add(path);
            try { CheckPath(path); File.Delete(path); owned.Remove(path); pending.Remove(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    // Delayed MediaPlayer handle release is expected. Retry without blocking the UI thread.
    public async Task RetryAsync()
    {
        foreach (var delay in new[] { 0, 50, 150, 500, 1500 })
        {
            if (PendingCount == 0) return;
            if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
            string[] paths; lock (sync) paths = pending.ToArray();
            foreach (var path in paths) Release(path);
        }
    }
    private static void CheckPath(string path)
    {
        for (string? cursor = path; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if (File.Exists(cursor) || Directory.Exists(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Temporary audio path contains a link.");
    }
    public async ValueTask DisposeAsync()
    {
        string[] paths; lock (sync) paths = owned.ToArray();
        foreach (var path in paths) Release(path);
        await RetryAsync().ConfigureAwait(false);
        if (PendingCount == 0)
            try { CheckPath(directory); if (Directory.Exists(directory)) Directory.Delete(directory, false); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
