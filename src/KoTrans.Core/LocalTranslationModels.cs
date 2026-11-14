namespace KoTrans.Core;

public enum LocalTranslationModel { Standard7B = 0, Lightweight18B = 1 }
public sealed record LocalTranslationModelInfo(LocalTranslationModel Id, string DisplayName, string Description, string DownloadSize, ulong RequiredCpuMemory);
public static class LocalTranslationModels
{
    public static IReadOnlyList<LocalTranslationModelInfo> All { get; } = Array.AsReadOnly(new[] {
        new LocalTranslationModelInfo(LocalTranslationModel.Standard7B, "표준 — Hy-MT2 7B (약 4.6GB)", "번역 품질 우선 · 기존 표준 모델", "약 4.6GB", 6UL * 1024 * 1024 * 1024),
        new LocalTranslationModelInfo(LocalTranslationModel.Lightweight18B, "경량 — Hy-MT2 1.8B (약 1.1GB)", "메모리 사용량과 대기 시간을 줄입니다. 표준보다 품질이 낮으며 부정문·명령·문맥을 잘못 번역할 수 있습니다.", "약 1.1GB", 3UL * 1024 * 1024 * 1024)
    });
    public static LocalTranslationModelInfo Get(LocalTranslationModel id) => All.SingleOrDefault(x => x.Id == id)
        ?? throw new InvalidDataException("지원되지 않는 로컬 AI 모델입니다. 모델을 직접 선택해 주세요.");
}
