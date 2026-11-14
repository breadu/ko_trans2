using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace KoTrans.App;

// The file lock arbitrates ownership across DEV-RUN/release. The current-user pipe
// carries only a fixed activation command; it never accepts paths, settings or code.
internal sealed class InstanceActivation : IDisposable
{
    private readonly FileStream ownedLock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private NamedPipeServerStream? connection;
    private bool disposed;
    internal Task Completion { get; }
    internal static string PipeName(string directory) => "KoTrans2.Activate." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd('\\', '/').ToUpperInvariant())));
    internal static InstanceActivation? TryOwn(string directory, Action activate)
    {
        Directory.CreateDirectory(directory);
        FileStream owned;
        try { owned = new(Path.Combine(directory, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        return new(owned, PipeName(directory), activate);
    }
    private InstanceActivation(FileStream owned, string name, Action activate)
    { ownedLock = owned; Completion = Task.Run(() => ListenAsync(name, activate)); }
    private async Task ListenAsync(string name, Action activate)
    {
        while (!lifetime.IsCancellationRequested) {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            lock (sync) { if (disposed) return; connection = pipe; }
            try {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), request.Token).ConfigureAwait(false);
                var command = new byte[1]; await pipe.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                if (command[0] == 1 && !lifetime.IsCancellationRequested) {
                    activate();
                    await pipe.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { lock (sync) { if (ReferenceEquals(connection, pipe)) connection = null; } }
        }
    }
    internal static async Task<bool> RequestAsync(string directory, Action<int>? allowForeground = null, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        try {
            using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var pid = new byte[4]; await pipe.ReadExactlyAsync(pid, deadline.Token).ConfigureAwait(false);
            int owner = BitConverter.ToInt32(pid); if (owner <= 0) return false;
            allowForeground?.Invoke(owner);
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            var accepted = new byte[1]; await pipe.ReadExactlyAsync(accepted, deadline.Token).ConfigureAwait(false);
            return accepted[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); connection?.Dispose(); ownedLock.Dispose();
        }
        _ = Completion.ContinueWith(task => { _ = task.Exception; lifetime.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
