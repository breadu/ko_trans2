using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KoTrans.Core;
using KoTrans.Infrastructure;
using KoTrans.Ocr;

internal static class PortableUpdateTests
{
    private static int count;
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); count++; }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { count++; return; } throw new Exception("Unsafe update accepted"); }
    private static AppSettings Rich(int profiles)
    {
        var data = new AppSettings(); data.Validate();
        for (int i = 1; i < profiles; i++) data.CreateGameProfile("Game " + i);
        for (int i = 0; i < data.Profiles.Count; i++) {
            var p = data.Profiles[i]; p.Name = "프로필 " + i; p.Language = i % 2 == 0 ? "영어" : "일본어";
            p.Translation.SwitchProvider((TranslationProvider)(i % 5)); if (TranslationProviderCatalog.Get(p.Translation.Provider).RequiresModel) p.Translation.Model = "fixture-model-" + i;
            p.OcrEngineId = i % 2 == 0 ? "paddle" : "future-engine"; p.OcrModelPresetId = i % 4 == 0 ? "v6-medium-full" : "default";
            p.OcrGpuAcceleration = i % 2 == 0;
            p.ReadingMode = i % 3 == 0 ? "NVL" : "ADV"; p.CaptureMode = (CaptureMode)(i % 3);
            p.CaptureArea = new(-1200 + i, 120, 750, 240); p.CaptureAreaConfigured = true;
            p.GameWindow = new("fixture", @"C:\games\game.exe", "GameClass", "Game Title");
            p.OverlayWindow.X = -800 + i; p.OverlayFontSize = 25; p.OverlayOriginalColor = "#AABBCC";
            p.Triggers.Keys = "Enter"; p.CharacterDictionaryPath = @"D:\Old App\MyDictionary.json";
        }
        data.ActiveProfileId = data.Profiles[^1].Id; data.VoiceDirectory = @"D:\Old App\voice"; data.Validate(); return data;
    }
    private static byte[] Bytes(AppSettings data) => JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true });
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KoTrans.PortableUpdate", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var data = Rich(10); byte[] original = Bytes(data);
            string live = Path.Combine(root, "settings.json"); await File.WriteAllBytesAsync(live, original);
            var keys = new ApiKeyStore(root); keys.Save(TranslationProvider.OpenAI, "synthetic-portable-key");
            byte[] protectedKey = await File.ReadAllBytesAsync(Path.Combine(root, "OpenAI.key"));
            string model = Path.Combine(root, "Models", "HyMT2", "fixture.gguf"); Directory.CreateDirectory(Path.GetDirectoryName(model)!); byte[] weights = "GGUF_fixture_keep"u8.ToArray(); await File.WriteAllBytesAsync(model, weights);
            var next = new SettingsStore(root, "2.0.0-validation+0123456789abcdef"); var loaded = await next.LoadAsync();
            Check(Bytes(loaded).SequenceEqual(original), "Update reset profile identity/language/provider/OCR/capture/subtitles/triggers/window/dictionary settings");
            string[] backups = Directory.GetFiles(Path.Combine(root, "Backups"), "*.json"); Check(backups.Length == 1 && (await File.ReadAllBytesAsync(backups[0])).SequenceEqual(original), "Backup was not exact pre-update bytes");
            loaded.Profiles[0].Name = "Edited after update"; await next.SaveAsync(loaded);
            await new SettingsStore(root, "2.0.0-validation+0123456789abcdef").LoadAsync();
            Check(Directory.GetFiles(Path.Combine(root, "Backups"), "*.json").Length == 1 && (await File.ReadAllBytesAsync(backups[0])).SequenceEqual(original), "Same release replaced/duplicated recovery point");
            Check((await File.ReadAllBytesAsync(Path.Combine(root, "OpenAI.key"))).SequenceEqual(protectedKey) && keys.Read(TranslationProvider.OpenAI) == "synthetic-portable-key", "Version update changed DPAPI key");
            Check((await File.ReadAllBytesAsync(model)).SequenceEqual(weights) && !Encoding.UTF8.GetString(await File.ReadAllBytesAsync(backups[0])).Contains("synthetic-portable-key"), "Update changed model or exposed key in backup");
            var spec = new LocalModelSpec("fixture", "fixture.gguf", weights.Length, Convert.ToHexString(SHA256.HashData(weights)), new("https://huggingface.co/fixture/model"));
            Check(await new LocalModelInstaller(Path.GetDirectoryName(model)!, spec).IsInstalledAsync(), "Existing local AI model cannot be reused");
            // Restoring the raw recovery point is sufficient for the old settings contract.
            await File.WriteAllBytesAsync(live, await File.ReadAllBytesAsync(backups[0]));
            Check(Bytes(await new SettingsStore(root).LoadAsync()).SequenceEqual(original), "Documented rollback lost settings");
            foreach (int schema in new[] { 1, 2, 3 }) {
                string dir = Path.Combine(root, "schema" + schema); Directory.CreateDirectory(dir);
                var node = JsonSerializer.SerializeToNode(Rich(10))!.AsObject(); node["SchemaVersion"] = schema;
                byte[] raw = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(node.ToJsonString())).ToArray();
                // JSON reader accepts UTF-8 BOM through stream; the backup must preserve it as well.
                await File.WriteAllBytesAsync(Path.Combine(dir, "settings.json"), raw);
                var migrated = await new SettingsStore(dir, "new-release").LoadAsync();
                Check(migrated.Profiles.Count == 10 && migrated.SchemaVersion == 3, "Legacy schema migration failed");
                Check((await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(dir, "Backups"), "*.json").Single())).SequenceEqual(raw), "Migration backup changed raw bytes/BOM");
            }
            string retired = Path.Combine(root, "retired-ocr"); Directory.CreateDirectory(retired);
            var old = Rich(4);
            old.Profiles[0].OcrEngineId = "meiki";
            old.Profiles[0].OcrModelPresetId = "default";
            old.Profiles[1].OcrEngineId = "paddle";
            old.Profiles[1].OcrModelPresetId = "meiki-tiny-det-v6-medium-rec";
            old.Profiles[1].Vertical = true;
            old.Profiles[1].ReadingMode = "NVL";
            old.Profiles[2].OcrModelPresetId = "default";
            old.Profiles[3].OcrModelPresetId = "meiki-tiny-det-v6-medium-rec";
            byte[] beforeMigration = Bytes(old);
            string retiredFile = Path.Combine(retired, "settings.json");
            await File.WriteAllBytesAsync(retiredFile, beforeMigration);
            foreach (var profile in old.Profiles.Take(2)) {
                profile.OcrEngineId = "paddle";
                profile.OcrModelPresetId = "v6-medium-full";
            }
            var retirementStore = new SettingsStore(retired, "retired-ocr-release");
            var updated = await retirementStore.LoadAsync();
            Check(retirementStore.MigratedRetiredOcrModel && Bytes(updated).SequenceEqual(Bytes(old)),
                "Retired OCR migration changed other options or affected a future engine");
            Check((await File.ReadAllBytesAsync(retiredFile)).SequenceEqual(beforeMigration)
                && (await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(retired, "Backups"), "*.json").Single())).SequenceEqual(beforeMigration),
                "Retired OCR load overwrote the original or backup");
            await retirementStore.SaveAsync(updated);
            Check(Bytes(await retirementStore.LoadAsync()).SequenceEqual(Bytes(updated)) && !retirementStore.MigratedRetiredOcrModel,
                "Retired OCR migration is not idempotent after save and reload");
            string fresh = Path.Combine(root, "fresh"); var first = new SettingsStore(fresh, "first"); await first.LoadAsync(); await first.SaveAsync(Rich(1)); await new SettingsStore(fresh, "first").LoadAsync();
            Check(!Directory.Exists(Path.Combine(fresh, "Backups")), "First install created a needless settings backup");
            foreach (string failure in new[] { "Backups", "update-state.json" }) {
                string dir = Path.Combine(root, "blocked-" + failure); Directory.CreateDirectory(dir); await File.WriteAllBytesAsync(Path.Combine(dir, "settings.json"), original);
                if (failure == "Backups") await File.WriteAllTextAsync(Path.Combine(dir, failure), "user-owned-file"); else Directory.CreateDirectory(Path.Combine(dir, failure));
                var blocked = new SettingsStore(dir, "new"); await Reject(() => blocked.LoadAsync()); await Reject(() => blocked.SaveAsync(Rich(1)));
                Check((await File.ReadAllBytesAsync(Path.Combine(dir, "settings.json"))).SequenceEqual(original), "Failed backup overwrote original");
            }
            foreach (string bad in new[] { "{broken", "{\"SchemaVersion\":999}" }) {
                string dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), bad);
                var store = new SettingsStore(dir, "new"); await Reject(() => store.LoadAsync()); await Reject(() => store.SaveAsync(Rich(1)));
                Check(await File.ReadAllTextAsync(Path.Combine(dir, "settings.json")) == bad, "Bad/future settings were silently reset");
            }
            string keep = Path.Combine(root, "Backups", "my-personal-copy.json"); await File.WriteAllTextAsync(keep, "keep");
            for (int i = 0; i < 8; i++) await new SettingsStore(root, "release-" + i).LoadAsync();
            Check(Directory.GetFiles(Path.Combine(root, "Backups"), "settings-before-*.json").Length == 5 && File.Exists(keep), "Retention deleted user file or failed to bound owned backups");
            var many = Rich(200); string large = Path.Combine(root, "maximum"); Directory.CreateDirectory(large); await File.WriteAllBytesAsync(Path.Combine(large, "settings.json"), Bytes(many));
            Check((await new SettingsStore(large, "new").LoadAsync()).Profiles.Count == 200, "200 profiles lost on update");
            string v1 = Path.Combine(root, "App_v1"), v2 = Path.Combine(root, "App_v2"); Directory.CreateDirectory(Path.Combine(v1, "models")); Directory.CreateDirectory(Path.Combine(v2, "models"));
            Directory.Delete(v1, true);
            Check(OcrModelPaths.FindDirectory(v2, root, null) == Path.Combine(v2, "models"), "Moved application depends on old OCR folder");
            Check(VoiceDataPaths.FindDirectory(v2, data.VoiceDirectory) == data.VoiceDirectory, "External voice path was rewritten");
            try { CharacterDictionary.Load(Path.Combine(v1, "missing.json")); throw new Exception("Missing dictionary accepted"); }
            catch (TranslationException ex) { Check(ex.Message.Contains("사전") && ex.Message.Contains("파일 위치"), "Missing dictionary error is opaque"); }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} portable update/backup/rollback/retention assertions. Temporary data and synthetic DPAPI key only.");
    }
}
