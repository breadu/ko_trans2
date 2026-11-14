using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KoTrans.Core;
using OpenCvSharp;

namespace KoTrans.Infrastructure;

public sealed class GameQaWriter
{
    private int saving;
    public Task<string> SaveAsync(string root, GameQaSnapshot snapshot, CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref saving, 1, 0) != 0) throw new InvalidOperationException("Game QA 저장 중입니다. 완료 후 다시 누르세요.");
        return Task.Run(async () =>
        {
            string? pending = null;
            try
            {
                token.ThrowIfCancellationRequested(); root = Path.GetFullPath(root); GameQaFiles.NoLinks(root);
                Directory.CreateDirectory(root); GameQaFiles.NoLinks(root);
                var name = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N");
                pending = Path.Combine(root, ".pending-" + name); Directory.CreateDirectory(pending);
                GameQaFiles.Validate(snapshot.Record);
                if (snapshot.Frame.Width != snapshot.Record.Input.Width || snapshot.Frame.Height != snapshot.Record.Input.Height || snapshot.Frame.PixelFormat != snapshot.Record.Input.PixelFormat)
                    throw new InvalidDataException("Game QA 입력과 결과의 이미지 계약이 다릅니다.");
                var bytes = GameQaFiles.Encode(snapshot.Frame);
                if (bytes.Length > 256_000_000) throw new InvalidDataException("Game QA PNG 크기 제한을 초과했습니다.");
                token.ThrowIfCancellationRequested();
                var record = snapshot.Record with { InputSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
                var json = JsonSerializer.SerializeToUtf8Bytes(record, GameQaFiles.Json);
                if (json.Length > GameQaFiles.MaxJsonBytes) throw new InvalidDataException("Game QA 결과가 너무 큽니다.");
                await File.WriteAllBytesAsync(Path.Combine(pending, "input.png"), bytes, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(pending, "result.json"), json, token).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(pending, "note.txt"),
                    "문제 설명: \n기대 동작: \n정답 원문(선택): expected.txt를 UTF-8로 작성하세요.\n\n"
                    + "input.png는 이 요청의 실제 OCR 입력입니다. ROI인 경우 result.json의 Crop을 확인하세요.\n"
                    + "pending/canceled/failed는 OCR 완료 결과가 아닙니다. 번역 pending은 저장 시점에 미완료입니다.\n"
                    + "게임 화면·대사에 개인정보가 포함될 수 있습니다. 공유 전 직접 확인하세요. 자동 업로드되지 않습니다.\n",
                    new UTF8Encoding(false), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); GameQaFiles.NoLinks(pending);
                var destination = Path.Combine(root, name); Directory.Move(pending, destination); pending = null;
                return destination;
            }
            finally
            {
                if (pending is not null)
                {
                    // Only this save's exact three files, never recursive deletion or other sessions.
                    try { GameQaFiles.NoLinks(pending); foreach (var name in new[] { "input.png", "result.json", "note.txt" }) File.Delete(Path.Combine(pending, name)); Directory.Delete(pending); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                Volatile.Write(ref saving, 0);
            }
        });
    }
}

public static class GameQaFiles
{
    public const int MaxJsonBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() } };
    public static string FindDirectory(string applicationDirectory, string localDataDirectory)
    {
        for (var dir = new DirectoryInfo(applicationDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "KoTrans.slnx"))) return Path.Combine(dir.FullName, "work", "game-qa");
        return Path.Combine(localDataDirectory, "KoTrans2", "work", "game-qa");
    }
    internal static void NoLinks(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Game QA 경로의 링크/junction은 사용할 수 없습니다.");
    }
    internal static byte[] Encode(ImageFrame frame)
    {
        var copy = frame.Pixels.ToArray();
        // GDI BGRX's unused byte is often zero. PNG must be visible while preserving every OCR RGB byte.
        if (frame.PixelFormat == FramePixelFormat.Bgrx32)
            for (int y = 0; y < frame.Height; y++) for (int x = 0; x < frame.Width; x++) copy[y * frame.Stride + x * 4 + 3] = 255;
        var pin = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try { using var mat = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, pin.AddrOfPinnedObject(), frame.Stride); return mat.ImEncode(".png"); }
        finally { pin.Free(); }
    }
    public static void Validate(GameQaRecord record)
    {
        if (record.SchemaVersion != 1 || record.RequestId == Guid.Empty || record.Options is null || record.Profile is null || record.Input is null || record.Translation is null
            || record.Options.Language is not ("en" or "ja") || record.Options.ReadingMode is not ("ADV" or "NVL")
            || record.OcrStatus is not ("pending" or "completed" or "canceled" or "failed")
            || (record.OcrStatus == "completed") != (record.Ocr is not null && record.CompletedUtc is not null)
            || record.OcrStatus != "completed" && (record.Ocr is not null || record.CompletedUtc is not null)
            || record.Translation.Status is not ("not_requested" or "pending" or "completed" or "canceled" or "failed")
            || record.Translation.Status == "completed" && (record.OcrStatus != "completed" || record.Translation.Text is null)
            || record.Translation.Status != "completed" && record.Translation.Text is not null
            || !new PixelRect(0, 0, record.Input.Width, record.Input.Height).IsValid
            || !new PixelRect(0, 0, record.Input.SourceWidth, record.Input.SourceHeight).IsValid
            || !Enum.IsDefined(record.Input.PixelFormat)) throw new InvalidDataException("Game QA 파일 계약이 올바르지 않습니다.");
        var input = record.Input;
        if (record.NvlPrefix is null || record.NvlPrefix.Count > 10000
            || record.NvlPrefix.Count > 0 && (record.Options.ReadingMode != "NVL" || record.Options.Vertical || record.Options.SearchArea is null || input.Crop is not null)
            || record.NvlPrefix.Any(l => l is null || l.Text is null || l.Text.Length > 16000 || !float.IsFinite(l.Confidence)
                || !l.Bounds.IsValid || l.Bounds.X < 0 || l.Bounds.Y < 0 || l.Bounds.X+(long)l.Bounds.Width > input.Width || l.Bounds.Y+(long)l.Bounds.Height > input.Height))
            throw new InvalidDataException("Game QA NVL 재사용 영역 정보가 올바르지 않습니다.");
        if (string.IsNullOrWhiteSpace(record.Options.EngineId) || record.Options.EngineId.Length > 80
            || string.IsNullOrWhiteSpace(record.Options.ModelPresetId) || record.Options.ModelPresetId.Length > 80
            || record.Ocr?.Models is { } identity && (identity.EngineId != record.Options.EngineId
                || identity.ModelPresetId != record.Options.ModelPresetId
                || string.IsNullOrWhiteSpace(identity.ImplementationVersion) || identity.AdditionalModels is null || identity.AdditionalModels.Count > 16))
            throw new InvalidDataException("Game QA OCR 엔진 정보가 올바르지 않습니다.");
        if (input.Crop is { } crop ? !crop.IsValid || crop.X < 0 || crop.Y < 0 || crop.Width != input.Width || crop.Height != input.Height
            || (long)crop.X + crop.Width > input.SourceWidth || (long)crop.Y + crop.Height > input.SourceHeight
            : input.Width != input.SourceWidth || input.Height != input.SourceHeight) throw new InvalidDataException("Game QA ROI 좌표가 올바르지 않습니다.");
        if (record.Ocr is { } ocr && (ocr.Lines is null || ocr.DetectedBoxes is null || ocr.Characters is null || ocr.Characters.Count > 100000
            || ocr.Characters.Any(c => c is null || c.Text is null || c.Text.Length > 2 || !float.IsFinite(c.Confidence) || !c.Bounds.IsValid)
            || ocr.Lines.Count > 10000 || ocr.DetectedBoxes.Count > 10000
            || ocr.DetectionPasses is < 1 or > 2
            || ocr.DetectorPadding is { } pad && (new[] {pad.Left,pad.Top,pad.Right,pad.Bottom}.Any(p => p is < 0 or > 64) || pad.Left+pad.Top+pad.Right+pad.Bottom == 0)
            || ocr.Lines.Any(l => l is null || l.Text is null))) throw new InvalidDataException("Game QA OCR 결과가 올바르지 않습니다.");
    }
    // Single-image replay. No path supplied by JSON is ever opened.
    public static async Task<GameQaSnapshot> ReadAsync(string inputPath, CancellationToken token = default)
    {
        inputPath = Path.GetFullPath(inputPath);
        if (!string.Equals(Path.GetFileName(inputPath), "input.png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("저장한 input.png를 지정하세요.");
        var jsonPath = Path.Combine(Path.GetDirectoryName(inputPath)!, "result.json");
        if (new FileInfo(jsonPath).Length > MaxJsonBytes || new FileInfo(inputPath).Length > 256_000_000) throw new InvalidDataException("Game QA 파일 크기 제한을 초과했습니다.");
        var record = JsonSerializer.Deserialize<GameQaRecord>(await File.ReadAllBytesAsync(jsonPath, token), Json) ?? throw new InvalidDataException("빈 Game QA 결과입니다.");
        Validate(record);
        var bytes = await File.ReadAllBytesAsync(inputPath, token);
        if (record.InputSha256 != Convert.ToHexString(SHA256.HashData(bytes))) throw new InvalidDataException("input.png와 result.json의 SHA-256이 일치하지 않습니다.");
        // Inspect PNG IHDR before native allocation; decoded size/type is also checked.
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != record.Input.Width
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != record.Input.Height)
            throw new InvalidDataException("PNG 입력 크기/형식이 결과와 다릅니다.");
        using var mat = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (mat.Empty() || mat.Width != record.Input.Width || mat.Height != record.Input.Height || mat.Type() != MatType.CV_8UC4) throw new InvalidDataException("Game QA PNG를 읽을 수 없습니다.");
        var pixels = new byte[checked(mat.Width * mat.Height * 4)]; Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new(new(mat.Width, mat.Height, mat.Width * 4, pixels, record.Input.PixelFormat), record);
    }
}
