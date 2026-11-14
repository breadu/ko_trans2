using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public sealed class SettingsStore(string directory, string? releaseIdentity = null)
{
    public const int MaxFileBytes = 1_048_576;
    public string DirectoryPath => directory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(directory, "settings.json");
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private bool invalidFile;
    public bool MigratedRetiredOcrModel { get; private set; }
    private readonly SettingsReleaseBackup? releaseBackup = releaseIdentity is null ? null : new(directory, releaseIdentity);
    public async Task<AppSettings> LoadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            MigratedRetiredOcrModel = false;
            if (releaseBackup is not null) await releaseBackup.EnsureAsync(token);
            if (!File.Exists(path)) { var defaults = new AppSettings(); defaults.Validate(); return defaults; }
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("설정 파일이 너무 큽니다.");
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var settings = document.Deserialize<AppSettings>(json)
                ?? throw new InvalidDataException("설정 파일이 비어 있습니다.");
            // Old schemas enabled a selected dictionary by its path, before the checkbox existed.
            // Preserve that behavior without enabling dictionaries in new profiles or overriding an explicit false.
            if (settings.SchemaVersion < 3 && settings.Profiles is not null
                && document.RootElement.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array) {
                int i = 0;
                foreach (var profile in profiles.EnumerateArray()) {
                    if (i < settings.Profiles.Count && settings.Profiles[i] is { } target && profile.ValueKind == JsonValueKind.Object
                        && !profile.TryGetProperty("CharacterDictionaryEnabled", out _) && !string.IsNullOrWhiteSpace(target.CharacterDictionaryPath))
                        target.CharacterDictionaryEnabled = true;
                    i++;
                }
            }
            if (settings.SchemaVersion == 3 && settings.DefaultProfileId == Guid.Empty)
                throw new InvalidDataException("기본 프로필 식별자가 없는 설정입니다.");
            settings.Validate();
            foreach (var profile in settings.Profiles!) {
                if (profile.OcrEngineId == "meiki" ||
                    (profile.OcrEngineId == "paddle" && profile.OcrModelPresetId == "meiki-tiny-det-v6-medium-rec")) {
                    MigratedRetiredOcrModel = true;
                    profile.OcrEngineId = "paddle";
                    profile.OcrModelPresetId = "v6-medium-full";
                }
            }
            if (settings.SchemaVersion < 3)
            {
                // Preserve a dedicated migration backup; ordinary .bak files rotate on save.
                var backup = path + $".v{settings.SchemaVersion}.bak";
                if (!File.Exists(backup)) File.Copy(path, backup, false);
                settings.SchemaVersion = 3;
            }
            invalidFile = false;
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            invalidFile = true;
            throw new InvalidDataException("설정을 읽을 수 없습니다. 원본을 보존했습니다. settings.json을 백업·수정한 후 다시 실행하세요.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            invalidFile = true;
            throw new IOException("설정 백업 또는 읽기에 실패했습니다. 기존 설정을 보호하기 위해 저장을 중단했습니다. " + ex.Message, ex);
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken token = default)
    {
        settings.Validate();
        // Serialize before yielding so concurrent UI edits cannot produce a partial snapshot.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, json);
        // Never write a file that this same version will refuse to load next time.
        // Reject before touching the current file, backup, temporary files or save gate.
        if (bytes.Length > MaxFileBytes)
            throw new InvalidDataException("설정 크기가 1 MiB 제한을 초과했습니다. 프로필 또는 모델별 설정을 줄인 뒤 다시 저장하세요. 기존 파일은 보존했습니다.");
        await gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            if (invalidFile) throw new InvalidOperationException("읽기·백업 문제가 있는 설정 원본을 보호하기 위해 저장을 중단했습니다. 문제를 해결한 후 다시 실행하세요.");
            if (releaseBackup is not null) {
                try { await releaseBackup.EnsureAsync(token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { invalidFile = true; throw; }
            }
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token);
                file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
            temporary = null;
        }
        finally { FinishSave(temporary, File.Delete, gate); }
    }
    // The small delegate seam lets tests reproduce an OS cleanup failure without races.
    // Preserve the original save/cancellation exception; never strand the next save on gate.
    internal static void FinishSave(string? temporary, Action<string> delete, SemaphoreSlim gate)
    {
        try
        {
            if (temporary is not null)
                try { delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        finally { gate.Release(); }
    }
}
