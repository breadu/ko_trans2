using KoTrans.Core;

namespace KoTrans.Ocr;

// App-owned sessions. A retired reference can never be rebound to a new profile/engine.
// Native resources are disposed only after every in-flight (including queued) call exits.
public sealed class OcrServiceManager(OcrServiceFactory factory) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly List<Task> retired = [];
    private Session? active;
    private (Guid Profile, string Engine, string Preset, string Selection) key;
    private bool disposed;
    public IOcrService Select(Guid profile, OcrOptions options)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); factory.Validate(options);
            var selected = (profile, options.EngineId, options.ModelPresetId, factory.SelectionKey(options) + "|gpu=" + options.UseDirectMl);
            if (active is not null && key == selected) return active;
            Retire(); key = selected;
            return active = new(factory.Create(options));
        }
    }
    public void Retire()
    {
        lock (sync)
        {
            if (active is null) return;
            var old = active; active = null; retired.Add(old.DisposeAsync().AsTask());
            retired.RemoveAll(t => t.IsCompletedSuccessfully);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (sync) { disposed = true; Retire(); pending = retired.ToArray(); }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
    private sealed class Session(IOcrService inner) : IOcrService
    {
        public OcrExecutionStatus ExecutionStatus => inner.ExecutionStatus;
        public event Action<OcrExecutionStatus>? ExecutionChanged { add => inner.ExecutionChanged += value; remove => inner.ExecutionChanged -= value; }
        private readonly object sync = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool retiring;
        private int calls;
        private Task? disposal;
        public OcrState State => inner.State;
        public OcrCapabilities Capabilities => inner.Capabilities;
        public bool CollectAttemptDiagnostics { set => inner.CollectAttemptDiagnostics = value; }
        private async Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
        {
            CancellationTokenSource linked;
            lock (sync) {
                if (retiring) throw new OperationCanceledException("OCR 프로필/엔진이 변경되었습니다.");
                linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); calls++;
            }
            try { linked.Token.ThrowIfCancellationRequested(); var value = await action(linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); return value; }
            finally { linked.Dispose(); lock (sync) if (--calls == 0 && retiring) drained.TrySetResult(); }
        }
        public async Task InitializeAsync(OcrOptions options, CancellationToken token) => await Run(async t => { await inner.InitializeAsync(options, t).ConfigureAwait(false); return true; }, token).ConfigureAwait(false);
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token) => Run(t => inner.RecognizeAsync(frame, options, t), token);
        public ValueTask DisposeAsync()
        {
            lock (sync) {
                if (disposal is not null) return new(disposal);
                retiring = true; lifetime.Cancel(); if (calls == 0) drained.TrySetResult();
                // Native session destruction can be expensive; never run it on the WPF caller.
                return new(disposal = Task.Run(Cleanup));
            }
        }
        private async Task Cleanup()
        {
            await drained.Task.ConfigureAwait(false);
            try { await inner.DisposeAsync().ConfigureAwait(false); } finally { lifetime.Dispose(); }
        }
    }
}
