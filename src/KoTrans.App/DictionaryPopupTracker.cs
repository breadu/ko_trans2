using System.ComponentModel;
using System.Windows.Threading;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed record DictionaryPopupSnapshot(WindowPlacement Placement, DictionaryPopupBounds Bounds);

// One reused browser popup, but its position belongs to the profile that opened it.
// Hooks are installed only after an explicit word lookup creates a browser HWND.
internal sealed class DictionaryPopupTracker : IDisposable
{
    private readonly Func<IntPtr, DictionaryPopupSnapshot?> remember;
    private readonly Func<IntPtr, Action<bool>, IDisposable> observe;
    private readonly Func<Profile, WindowPlacement, Task> save;
    private IDisposable? observation;
    private Profile? profile;
    private IntPtr handle;
    private DictionaryPopupSnapshot? latest, initial, committed;
    private bool preserveFallback, disposed;
    private Task pending = Task.CompletedTask;
    internal event Action<string>? Failed;
    internal DictionaryPopupTracker(Func<Profile, WindowPlacement, Task> save,
        Func<IntPtr, DictionaryPopupSnapshot?>? remember = null, Func<IntPtr, Action<bool>, IDisposable>? observe = null)
    {
        this.save = save; this.remember = remember ?? DictionaryPopupPlacement.Remember;
        this.observe = observe ?? ((window, changed) => new NativeObservation(window, changed));
    }
    internal async Task PrepareAsync(Profile next)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await pending;
        if (profile == next) return;
        await FlushAsync();
        observation?.Dispose(); observation = null; handle = IntPtr.Zero;
        profile = next; latest = initial = committed = null; preserveFallback = false;
    }
    internal DictionaryPopupBounds? CurrentBounds()
    {
        if (handle == IntPtr.Zero) return null;
        var current = remember(handle);
        if (current is null) return latest?.Bounds; // Minimized/maximized windows retain their last normal rectangle.
        latest = current;
        return current.Bounds;
    }
    internal void Attach(IntPtr window, bool temporaryFallback)
    {
        if (disposed || profile is null) return;
        if (handle == window && observation is not null) { latest = remember(handle) ?? latest; return; }
        observation?.Dispose(); observation = null; handle = window;
        latest = initial = remember(handle); preserveFallback = temporaryFallback;
        try { observation = observe(window, Changed); }
        catch { handle = IntPtr.Zero; throw; }
    }
    private void Changed(bool closed)
    {
        if (disposed) return;
        try { latest = remember(handle) ?? latest; }
        catch (Exception ex) { Failed?.Invoke("단어 사전 위치 확인 실패: " + ex.Message); if (!closed) return; }
        if (!closed) return;
        observation?.Dispose(); observation = null; handle = IntPtr.Zero;
        pending = SaveClosedAsync();
    }
    private async Task SaveClosedAsync()
    {
        try { await SaveSnapshotAsync(); }
        catch (Exception ex) { Failed?.Invoke("단어 사전 위치 저장 실패: " + ex.Message); }
    }
    internal async Task FlushAsync()
    {
        await pending;
        await SaveSnapshotAsync();
    }
    private async Task SaveSnapshotAsync()
    {
        if (handle != IntPtr.Zero) latest = remember(handle) ?? latest;
        var owner = profile; var snapshot = latest;
        if (owner is null || snapshot is null || Same(snapshot.Placement, committed?.Placement)
            || preserveFallback && Same(snapshot.Placement, initial?.Placement)) return;
        await save(owner, snapshot.Placement);
        committed = snapshot; preserveFallback = false;
    }
    private static bool Same(WindowPlacement a, WindowPlacement? b) => b is not null &&
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height
        && a.MonitorId == b.MonitorId && a.MonitorDevice == b.MonitorDevice;
    public void Dispose() { if (disposed) return; disposed = true; observation?.Dispose(); observation = null; handle = IntPtr.Zero; }

    private sealed class NativeObservation : IDisposable
    {
        private readonly IntPtr window;
        private readonly Action<bool> changed;
        private readonly WinEvent callback;
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private PixelRect? lastBounds;
        private uint lastDpi;
        private IntPtr hook;
        private bool disposed;
        internal NativeObservation(IntPtr window, Action<bool> changed)
        {
            this.window = window; this.changed = changed; callback = Event;
            lastBounds = WindowTargets.Bounds(window); lastDpi = GetDpiForWindow(window);
            GetWindowThreadProcessId(window, out var process);
            if (process == 0) throw new Win32Exception("사전 창 프로세스를 확인하지 못했습니다.");
            // Location updates retain the last normal rectangle BEFORE HWND destruction/minimization.
            hook = SetWinEventHook(0x8001, 0x800B, IntPtr.Zero, callback, process, 0, 0);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            timer.Tick += Tick; timer.Start();
        }
        private void Event(IntPtr hookId, uint type, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
        {
            if (disposed || hwnd != window || objectId != 0 || childId != 0 || type is not (0x8001 or 0x800B)) return;
            try { if (type == 0x8001) changed(true); else Tick(null, EventArgs.Empty); }
            catch (Exception) { /* Never let a managed exception cross the native callback. Poll retries. */ }
        }
        private void Tick(object? sender, EventArgs e)
        {
            if (disposed) return;
            if (!IsWindow(window)) { changed(true); return; }
            var bounds = WindowTargets.Bounds(window); uint dpi = GetDpiForWindow(window);
            if (bounds == lastBounds && dpi == lastDpi) return;
            lastBounds = bounds; lastDpi = dpi; changed(false);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; timer.Stop(); timer.Tick -= Tick;
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
            GC.KeepAlive(callback); GC.KeepAlive(dispatcher);
        }
        private delegate void WinEvent(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    }
}
