using System.Security.Cryptography;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Ocr;

public static class OcrIdentity
{
    // Captured from the verified, loaded manifest, not a later reread of files on disk.
    public static OcrModelIdentity From(ModelSpec spec) => new(spec.Name, spec.Version, spec.Sha256.ToUpperInvariant(),
        spec.Kind == "recognition" ? spec.DictionarySha256.ToUpperInvariant() : "",
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(spec))));
    public static void RequireSame(OcrModelIdentity expected, ModelSpec actual)
    {
        if (expected != From(actual)) throw new InvalidDataException("저장 당시 모델/사전/전처리 계약과 다릅니다. 동일 모델을 준비하거나 명시적인 비교 옵션을 사용하세요.");
    }
}
