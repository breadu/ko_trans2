using System.Buffers.Binary;
using System.Text.Json;

namespace KoTrans.Core;

// Local, current-user named pipe. Pixels are binary, never JSON/base64 or a disk file.
public static class DirectMlProtocol
{
    public const int Version = 1;
    public const int MaxHeader = 64 * 1024, MaxResponse = 16 * 1024 * 1024, MaxPixels = 256_000_000;
    public static bool Supports(OcrOptions options) => options.EngineId == "paddle"
        && options.ModelPresetId is "default" or "v6-medium-full" or "v5-det-v6-medium-rec";
    public static void ValidateInput(ImageFrame frame, OcrOptions options)
    {
        if (frame.Pixels.Length > MaxPixels || options.Language is not ("en" or "ja") || options.ReadingMode is not ("ADV" or "NVL")
            || options.DetectionLongSide != 0 && options.DetectionLongSide is < 32 or > 1920
            || options.KeepLocalParagraph && options.SearchArea is null || !GameMarkerGeometry.ValidPatterns(options.MarkerPatterns)
            || !TerminalMarkerReference.Valid(options.MarkerReference) || !DialogueLayoutHint.Valid(options.DialogueHint))
            throw new ArgumentException("OCR 입력 또는 옵션이 올바르지 않습니다.");
        if (options.SearchArea is { } area && (!area.IsValid || area.X < 0 || area.Y < 0
            || (long)area.X + area.Width > frame.Width || (long)area.Y + area.Height > frame.Height))
            throw new ArgumentException("OCR 탐색 영역이 이미지 밖입니다.");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, int limit, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > limit) throw new InvalidDataException("GPU OCR 메시지가 허용 크기를 초과했습니다.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, int limit, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > limit) throw new InvalidDataException("GPU OCR 메시지 길이가 올바르지 않습니다.");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("GPU OCR 메시지가 비어 있습니다.");
    }
}

public sealed record DirectMlRequest(Guid Id, int Version, string Operation, OcrOptions Options,
    string? ModelsRoot = null, int Width = 0, int Height = 0, int Stride = 0, int Bytes = 0,
    FramePixelFormat Format = FramePixelFormat.Bgrx32, bool Profile = false, OcrRunIdentity? ReplayIdentity = null);
public sealed record DirectMlReply(Guid Id, int Version, string Error = "", string Detail = "",
    OcrResult? Result = null, string Device = "", string DeviceId = "", double ProcessingMs = 0, double LoadMs = 0,
    IReadOnlyList<string>? ProfileFiles = null);
