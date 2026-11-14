using KoTrans.Infrastructure;
using System.Collections.Generic;

namespace KoTrans.App;

internal interface IPronunciationPlayer : IAsyncDisposable
{
    event Action<string>? Status;
    event Action<string>? Error;
    Task<IReadOnlySet<string>> FindWordsAsync(string directory, IReadOnlyList<string> words, CancellationToken token);
    Task PlayAsync(string directory, string word);
    void Stop();
}
internal sealed class PronunciationPlayer : IPronunciationPlayer
{
    private MediaPlayer? player;
    private string? temporary;
    private CancellationTokenSource? current;
    private Task pending = Task.CompletedTask;
    private Task cleanup = Task.CompletedTask;
    private readonly VoiceIndexCache indexes = new();
    private readonly AudioTemporaryFiles files = new();
    private long version;
    public event Action<string>? Status;
    public event Action<string>? Error;
    public Task<IReadOnlySet<string>> FindWordsAsync(string directory, IReadOnlyList<string> words, CancellationToken token) =>
        Task.Run<IReadOnlySet<string>>(() => {
            token.ThrowIfCancellationRequested();
            var index = indexes.Get(directory);
            token.ThrowIfCancellationRequested();
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase)) {
                token.ThrowIfCancellationRequested();
                if (!index.Contains(word)) continue;
                try { using var file = index.OpenAudio(word); available.Add(word); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TranslationException) { }
            }
            return available;
        }, token);
    public Task PlayAsync(string directory, string word)
    {
        Stop(); var serial = version; var cancellation = new CancellationTokenSource(); current = cancellation;
        return pending = PrepareAsync(directory, word, serial, cancellation, pending);
    }
    private async Task PrepareAsync(string directory, string word, long serial, CancellationTokenSource cancellation, Task previous)
    {
        string? path = null;
        try
        {
            await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            path = await Task.Run(async () =>
            {
                var index = indexes.Get(directory); using var audio = index.OpenAudio(word);
                return await files.CopyAsync(audio, cancellation.Token);
            }, cancellation.Token);
            if (serial != version) return;
            temporary = path; path = null; player = new MediaPlayer(); var active = player;
            active.MediaEnded += (_, _) => { if (player == active) { Stop(); Status?.Invoke("발음 재생 완료"); } };
            active.MediaFailed += (_, _) => { if (player == active) { Stop(); Error?.Invoke("MP3를 재생할 수 없습니다. 파일과 Windows 미디어 지원을 확인하세요."); } };
            active.Open(new Uri(temporary)); active.Play(); Status?.Invoke("로컬 발음 재생 중");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (serial == version) { Stop(); Error?.Invoke(ex is TranslationException ? ex.Message
            : ex is FileNotFoundException or DirectoryNotFoundException ? "이 단어의 발음 파일이 없습니다. voice 폴더의 MP3 파일을 확인하세요."
            : "발음 인덱스·파일을 읽을 수 없습니다. 경로와 파일 상태를 확인하세요."); } }
        finally { files.Release(path); if (current == cancellation) current = null; cancellation.Dispose(); ScheduleCleanup(); }
    }
    public void Stop()
    {
        version++; current?.Cancel(); player?.Close(); player = null;
        files.Release(temporary); temporary = null; ScheduleCleanup();
    }
    // Active playback is never a cleanup candidate; only Release marks a file for retry.
    private void ScheduleCleanup() { if (cleanup.IsCompleted && temporary is null) cleanup = files.RetryAsync(); }
    public void ChangeDirectory() { Stop(); indexes.Invalidate(); }
    public async ValueTask DisposeAsync()
    {
        Stop(); await pending; await cleanup; indexes.Invalidate(); await files.DisposeAsync();
        if (files.PendingCount > 0) Error?.Invoke("Windows가 임시 발음 파일을 계속 사용 중입니다. 원본 음성 데이터는 변경하지 않았습니다.");
    }
}
