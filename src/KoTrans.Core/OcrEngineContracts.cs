namespace KoTrans.Core;

public sealed record OcrCapabilities(bool Roi, bool LocalParagraph, bool AdaptiveDetection, bool AdvRecovery, bool TrackedRoi = false)
{
    public static OcrCapabilities Paddle { get; } = new(true, true, true, true, true);
    public static OcrCapabilities Basic { get; } = new(false, false, false, false, false);
}
public sealed record OcrEngineDescriptor(string Id, string DisplayName, string ImplementationVersion,
    IReadOnlyList<string> Languages, bool Vertical, bool VerticalBeta, OcrCapabilities Capabilities,
    bool LocalModels, bool QaReplay, string Description)
{
    public void Validate(OcrOptions options)
    {
        if (options.EngineId != Id) throw new OcrInitializationException("OCR 엔진과 요청의 식별자가 다릅니다.");
        if (!Languages.Contains(options.Language)) throw new OcrInitializationException($"{DisplayName}은 선택한 언어({options.Language})를 지원하지 않습니다. 프로필의 언어 또는 OCR 엔진을 변경하세요.");
        if (options.Vertical && !Vertical) throw new OcrInitializationException($"{DisplayName}은 세로쓰기를 지원하지 않습니다.");
        if (options.SearchArea is not null && !Capabilities.Roi || options.KeepLocalParagraph && !Capabilities.LocalParagraph
            || options.DetectionLongSide != 0 && !Capabilities.AdaptiveDetection)
            throw new OcrInitializationException($"{DisplayName}에서 지원하지 않는 OCR 영역/확대 옵션입니다.");
        if (options.ReadingMode is not ("ADV" or "NVL")) throw new OcrInitializationException("지원하지 않는 읽기 모드입니다.");
    }
}
public sealed record OcrModelReadiness(bool Ready, string Message);
