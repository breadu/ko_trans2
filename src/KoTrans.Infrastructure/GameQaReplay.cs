using KoTrans.Core;
using KoTrans.Ocr;

namespace KoTrans.Infrastructure;

public static class GameQaReplay
{
    public static IOcrService Create(GameQaSnapshot snapshot, OcrServiceFactory factory, string? comparisonDetectorRoot = null, bool useCurrentAlgorithm = false)
    {
        return factory.CreateReplay(snapshot.Record.Options, ReplayIdentity(snapshot.Record, useCurrentAlgorithm), comparisonDetectorRoot);
    }
    private static void ValidateRevision(GameQaRecord saved, bool useCurrentAlgorithm)
    {
        // AlgorithmVersion belongs to the shared layout/pipeline contract. Engine implementation
        // versions and all weight hashes are checked separately by the registered replay loader.
        if (saved.AlgorithmVersion is { } revision && revision != OcrAlgorithmRevision.Current) {
            bool known = revision is "ocr-layout-v17-confirmed-row-20261115" or "ocr-layout-v16-dialogue-row-recovery-20261115" or "ocr-layout-v15-overlapping-row-20261115" or "ocr-layout-v14-recognized-ruby-20261114" or "ocr-layout-v13-static-label-recovery-20261114" or "ocr-layout-v12-terminal-marker-tracking-20261106"
                or "ocr-layout-v9-terminal-stability-20261106" or "ocr-layout-v8-numeric-marker-20261106" or "ocr-layout-v7-ftz-20261102"
                or "ocr-layout-v6-20261031" or "ocr-layout-v5-20261030" or "ocr-layout-v4-20261030" or "ocr-layout-v3-20261030" || revision == "ocr-layout-v2-20261030" && saved.Options.SearchArea is null && saved.Options.DetectionLongSide == 0 && !saved.Options.KeepLocalParagraph;
            if (!useCurrentAlgorithm || !known) throw new InvalidDataException("OCR 알고리즘 버전이 다릅니다. 같은 버전을 사용하거나 알려진 이전 자료를 --current-algorithm으로 비교하세요.");
        }
    }
    private static OcrRunIdentity ReplayIdentity(GameQaRecord saved, bool useCurrentAlgorithm)
    {
        ValidateRevision(saved,useCurrentAlgorithm);
        var identity=saved.Ocr?.Models ?? throw new InvalidDataException("완료된 OCR 모델 식별자가 없습니다.");
        // An explicit comparison may use the current Paddle layout, while keeping
        // every model/dictionary/contract hash intact. Never alias unknown versions.
        return useCurrentAlgorithm && saved.AlgorithmVersion is { } revision && revision!=OcrAlgorithmRevision.Current
            && identity.EngineId=="paddle" && identity.ImplementationVersion==revision
            ? identity with {ImplementationVersion=OcrAlgorithmRevision.Current, FlushDenormalsToZero=true} : identity;
    }
    public static OcrExecutionOptions Prepare(GameQaSnapshot snapshot, string modelsRoot, string? comparisonDetectorRoot = null, bool useCurrentAlgorithm = false)
    {
        var saved = snapshot.Record;
        if (saved.Options.EngineId != "paddle" || saved.Ocr?.Models?.EngineId != "paddle")
            throw new InvalidDataException("이전 Paddle 전용 재현 경로입니다. 공통 OCR 팩토리를 사용하세요.");
        var identity = ReplayIdentity(saved,useCurrentAlgorithm);
        var execution = OcrServiceFactory.PreparePaddleReplay(modelsRoot, saved.Options, identity, comparisonDetectorRoot);
        execution.Validate(); return execution;
    }

    public static async Task<OcrResult> RecognizeAsync(GameQaSnapshot snapshot, IOcrService service, bool compareDetector, CancellationToken token, bool useCurrentAlgorithm = false)
    {
        var identity = ReplayIdentity(snapshot.Record,useCurrentAlgorithm);
        if (identity.EngineId != snapshot.Record.Options.EngineId || identity.ModelPresetId != snapshot.Record.Options.ModelPresetId) throw new InvalidDataException("QA 요청과 결과 엔진이 다릅니다.");
        var result = await service.RecognizeAsync(snapshot.Frame, snapshot.Record.Options, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.Models?.ModelPresetId != identity.ModelPresetId || result.Models?.EngineId != identity.EngineId || result.Models.ImplementationVersion != identity.ImplementationVersion
            || result.Models.FlushDenormalsToZero != identity.FlushDenormalsToZero || result.Models.Threads != identity.Threads || result.Models.AllowUpscale != identity.AllowUpscale
            || !result.Models.AdditionalModels.SequenceEqual(identity.AdditionalModels)
            || result.Models.Recognizer != identity.Recognizer || (!compareDetector && result.Models.Detector != identity.Detector))
            throw new InvalidDataException("실제 로딩 모델이 저장 당시와 다릅니다.");
        return result;
    }
}
