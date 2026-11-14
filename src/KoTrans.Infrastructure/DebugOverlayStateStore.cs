using System.Text.Json;
using KoTrans.Core;
namespace KoTrans.Infrastructure;

public sealed record DebugOverlayState
{
    public bool OcrExpanded { get; init; } = true;
    public bool TranslationExpanded { get; init; }
    public bool EventsExpanded { get; init; }
    public WindowPlacement Placement { get; init; } = new() { Width = 400, Height = 600 };
    public double DpiScale { get; init; } = 1;
    public void Validate()
    {
        var p = Placement;
        if (p is null || !double.IsFinite(p.Width) || !double.IsFinite(p.Height) || p.Width is < 320 or > 4000 || p.Height is < 240 or > 4000
            || !double.IsFinite(DpiScale) || DpiScale is < .5 or > 8 || p.MonitorId is null || p.MonitorDevice is null || p.MonitorId.Length > 512 || p.MonitorDevice.Length > 256)
            throw new InvalidDataException("Invalid debug placement.");
    }
}
public sealed class DebugOverlayStateStore(string directory)
{
    private readonly string path = Path.Combine(directory, "debug-overlay.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<DebugOverlayState> LoadAsync()
    {
        try {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return new();
            var value = JsonSerializer.Deserialize<DebugOverlayState>(await File.ReadAllTextAsync(path).ConfigureAwait(false)) ?? new();
            value.Validate(); return value;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { return new(); }
    }
    public async Task SaveAsync(DebugOverlayState state)
    {
        state.Validate(); string json = JsonSerializer.Serialize(state); // snapshot before any await
        await gate.WaitAsync().ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
            File.Move(temporary, path, true);
        } finally { try { if (File.Exists(temporary)) File.Delete(temporary); } finally { gate.Release(); } }
    }
}
