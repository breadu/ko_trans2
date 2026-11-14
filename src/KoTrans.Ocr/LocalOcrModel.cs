using System.Security.Cryptography;
using KoTrans.Core;

namespace KoTrans.Ocr;

// Engine-neutral local-file integrity; preprocessing/output contracts stay with the engine.
public sealed record LocalOcrModel(string Role, string FileName, string Version, long Bytes, string Sha256)
{
    internal byte[] ReadVerified(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) throw new OcrInitializationException($"OCR 모델 '{FileName}'이 없습니다. 모델 폴더를 확인하세요. 자동 다운로드는 하지 않습니다.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != Bytes || Bytes > 128_000_000) throw new OcrInitializationException($"OCR 모델 '{FileName}'의 크기가 올바르지 않습니다.");
        var bytes = new byte[checked((int)Bytes)]; stream.ReadExactly(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new OcrInitializationException($"OCR 모델 '{FileName}'의 SHA-256이 다릅니다. 손상되었거나 지원하지 않는 버전입니다.");
        return bytes;
    }
    public OcrModelIdentity Identity(string contract) => new(FileName, Version, Sha256.ToUpperInvariant(), "", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contract))));
}
