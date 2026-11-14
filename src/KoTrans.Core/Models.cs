using System.Text.Json.Serialization;

namespace KoTrans.Core;

// Desktop coordinates are always physical pixels; WPF window dimensions are DIPs.
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0 && Width <= 16384 && Height <= 16384
        && (long)Width * Height <= 64_000_000 && (long)X + Width <= int.MaxValue && (long)Y + Height <= int.MaxValue;
    public override string ToString() => $"{X}, {Y} · {Width} × {Height} px";
}

public enum FramePixelFormat { Bgrx32, Bgra32 }
public sealed class ImageFrame
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public FramePixelFormat PixelFormat { get; }
    // Ownership transfers to this frame. Callers must not subsequently mutate the buffer.
    public ImageFrame(int width, int height, int stride, byte[] pixels, FramePixelFormat pixelFormat = FramePixelFormat.Bgrx32)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (!new PixelRect(0, 0, width, height).IsValid || stride < (long)width * 4
            || (long)stride * height != pixels.LongLength || !Enum.IsDefined(pixelFormat))
            throw new ArgumentException("이미지 크기, stride 또는 버퍼 길이가 올바르지 않습니다.");
        Width = width; Height = height; Stride = stride; Pixels = pixels; PixelFormat = pixelFormat;
    }
}

public sealed class WindowPlacement
{
    public int X { get; set; } = 100;
    public int Y { get; set; } = 100;
    public double Width { get; set; } = 720;
    public double Height { get; set; } = 280;
    // X/Y and monitor work origins are physical pixels. Width/Height remain WPF DIPs.
    public string MonitorId { get; set; } = "";
    public string MonitorDevice { get; set; } = "";
    public int? MonitorWorkX { get; set; }
    public int? MonitorWorkY { get; set; }
}

public sealed class Profile : System.ComponentModel.INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string name = "기본 프로필";
    public string Name
    {
        get => name;
        set { if (name == value) return; name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Language { get; set; } = "영어";
    public string ReadingMode { get; set; } = "ADV";
    public string OcrEngineId { get; set; } = "paddle";
    public bool OcrGpuAcceleration { get; set; }
    private string? ocrModelPresetId;
    private bool ocrModelPresetSpecified;
    // Fresh/missing Paddle selections use v6. Explicit saved IDs and other engines keep their contracts.
    public string OcrModelPresetId {
        get => ocrModelPresetSpecified ? ocrModelPresetId! : OcrEngineId == "paddle" ? "v6-medium-full" : "default";
        set { ocrModelPresetId = value; ocrModelPresetSpecified = true; }
    }
    public bool Vertical { get; set; }
    public bool ShowJapaneseFurigana { get; set; }
    public PixelRect CaptureArea { get; set; } = new(0, 0, 800, 300);
    // Existing files always contained a usable rectangle. New app defaults explicitly opt out.
    public bool CaptureAreaConfigured { get; set; } = true;
    public PixelRect? WindowCaptureRegion { get; set; }
    public bool OriginalCaptureWhenScaled { get; set; }
    public double OverlayOpacity { get; set; } = 0.9;
    public double OverlayFontSize { get; set; } = 24;
    public string OverlayColor { get; set; } = "#FFFFFF";
    public double OverlayOriginalFontSize { get; set; } = 21;
    public string OverlayOriginalColor { get; set; } = "#D3D3D3";
    public bool ShowOriginal { get; set; } = true;
    public bool ShowTranslation { get; set; } = true;
    public bool CopyOcrToClipboard { get; set; }
    public WindowPlacement OverlayWindow { get; set; } = new();
    public CaptureMode CaptureMode { get; set; }
    public TranslationOptions Translation { get; set; } = new();
    public TriggerOptions Triggers { get; set; } = new();
    public string CharacterDictionaryPath { get; set; } = "";
    public bool OverlayClickThrough { get; set; } = true;
    public bool WordDictionaryEnabled { get; set; }
    public WordDictionaryOptions WordDictionary { get; set; } = new();
    public WindowPlacement? DictionaryPopupWindow { get; set; }
    public WindowPlacement? StudyWindow { get; set; }
    public bool CharacterDictionaryEnabled { get; set; }
    public int TriggerDelayMs { get; set; } = 0;
    public bool AutomaticWatch { get; set; } = true;
    public GameWindowIdentity? GameWindow { get; set; }

    public OcrOptions OcrOptions() => new(Language == "일본어" ? "ja" : "en", ReadingMode, Vertical && Language == "일본어", EngineId: OcrEngineId, ModelPresetId: OcrModelPresetId, UseDirectMl: OcrGpuAcceleration);
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 3;
    public Guid DefaultProfileId { get; set; }
    public HotkeyOptions Hotkeys { get; set; } = new();
    public string VoiceDirectory { get; set; } = "";
    public bool StudyTopmost { get; set; } = true;
    public List<Profile> Profiles { get; set; } = [new() { CaptureAreaConfigured = false, CaptureMode = CaptureMode.Window }];
    public Guid ActiveProfileId { get; set; }
    public WindowPlacement MainWindow { get; set; } = new() { Width = 1080, Height = 760 };
    public WindowPlacement FirstRunWizardWindow { get; set; } = new() { X = 30, Y = 30, Width = 620, Height = 760 };
    public WindowPlacement StudyWindow { get; set; } = new() { X = 220, Y = 150, Width = 640, Height = 640 };

    public void Validate()
    {
        if (SchemaVersion is not (1 or 2 or 3) || Profiles is null || Profiles.Count is < 1 or > 200
            || Profiles.Any(p => p is null) || Profiles.Select(p => p.Id).Distinct().Count() != Profiles.Count)
            throw new InvalidDataException("지원하지 않거나 손상된 설정 파일입니다.");
        if (DefaultProfileId == Guid.Empty) DefaultProfileId = Profiles[0].Id; // Legacy first profile, once only.
        if (!Profiles.Any(p => p.Id == DefaultProfileId)) throw new InvalidDataException("기본 프로필 식별자가 올바르지 않습니다.");
        foreach (var p in Profiles)
        {
            if (!p.ShowOriginal && !p.ShowTranslation)
                throw new InvalidDataException("원문 또는 한국어 번역 표시 중 하나 이상을 켜세요.");
            if (p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80
                || string.IsNullOrWhiteSpace(p.OcrEngineId) || p.OcrEngineId.Length > 80
                || string.IsNullOrWhiteSpace(p.OcrModelPresetId) || p.OcrModelPresetId.Length > 80
                || p.Language is not ("영어" or "일본어") || p.ReadingMode is not ("ADV" or "NVL")
                || !p.CaptureArea.IsValid || !double.IsFinite(p.OverlayOpacity) || p.OverlayOpacity is < 0.2 or > 1
                || !double.IsFinite(p.OverlayFontSize) || p.OverlayFontSize is < 12 or > 60
                || !double.IsFinite(p.OverlayOriginalFontSize) || p.OverlayOriginalFontSize is < 12 or > 60
                || p.OverlayColor is null || !System.Text.RegularExpressions.Regex.IsMatch(p.OverlayColor, "^#[0-9a-fA-F]{6}$")
                || p.OverlayOriginalColor is null || !System.Text.RegularExpressions.Regex.IsMatch(p.OverlayOriginalColor, "^#[0-9a-fA-F]{6}$"))
                throw new InvalidDataException("프로필 설정 값이 올바르지 않습니다.");
            if (p.WindowCaptureRegion is { } region && (!region.IsValid || region.X < 0 || region.Y < 0))
                throw new InvalidDataException("게임 창의 대사 영역 설정이 올바르지 않습니다.");
            ValidatePlacement(p.OverlayWindow);
            if (p.DictionaryPopupWindow is not null) ValidatePlacement(p.DictionaryPopupWindow);
            if (p.StudyWindow is not null) ValidatePlacement(p.StudyWindow);
            if (!Enum.IsDefined(p.CaptureMode) || p.Translation is null || p.Triggers is null || p.CharacterDictionaryPath is null)
                throw new InvalidDataException("번역 설정이 올바르지 않습니다.");
            if (p.TriggerDelayMs is < 0 or > 5000 || p.CharacterDictionaryPath.Length > 1024)
                throw new InvalidDataException("트리거 지연·사전 경로를 확인하세요.");
            p.GameWindow?.Validate();
            if (p.WordDictionary is null) throw new InvalidDataException("단어 사전 설정이 올바르지 않습니다.");
            p.WordDictionary.Validate();
            p.Translation.Validate(); p.Triggers.Validate();
        }
        ValidatePlacement(MainWindow); ValidatePlacement(FirstRunWizardWindow); ValidatePlacement(StudyWindow);
        if (VoiceDirectory is null || VoiceDirectory.Length > 1024) throw new InvalidDataException("발음 데이터 경로를 확인하세요.");
        if (Hotkeys is null) throw new InvalidDataException("단축키 설정이 올바르지 않습니다.");
        Hotkeys.UpgradeGameQaKey(); Hotkeys.UpgradeDefaultWindowKeys(); Hotkeys.UpgradeDefaultStudyKeys(); Hotkeys.Validate();
        if (!Profiles.Any(p => p.Id == ActiveProfileId)) ActiveProfileId = DefaultProfileId;
    }
    [JsonIgnore] public bool CanEditGlobal => ActiveProfileId == DefaultProfileId;
    [JsonIgnore] public Profile DefaultProfile => Profiles.Single(p => p.Id == DefaultProfileId);
    // Stage only membership and selection. SettingsStore captures field values before its first await.
    public AppSettings CopyProfileMembership()
    {
        var proposal = (AppSettings)MemberwiseClone();
        proposal.Profiles = [.. Profiles];
        return proposal;
    }
    public Profile CreateGameProfile(string name)
    {
        if (Profiles.Count >= 200 || string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new InvalidDataException("프로필 이름 또는 최대 개수(200개)를 확인하세요.");
        // Serializing the known settings contract avoids accidentally sharing newly added nested settings.
        var copy = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(DefaultProfile))!;
        copy.Id = Guid.NewGuid(); copy.Name = GetUniqueProfileName(name.Trim(), copy.Id); copy.GameWindow = null;
        // New games use the recommended window mode; do not change saved profile choices.
        copy.CaptureMode = CaptureMode.Window;
        copy.WindowCaptureRegion = null;
        copy.CaptureArea = new(0, 0, 800, 300);
        copy.CaptureAreaConfigured = false;
        Profiles.Add(copy); return copy;
    }
    public string GetUniqueProfileName(string name, Guid profileId)
    {
        const int maxLength = 80;
        var taken = Profiles.Where(p => p.Id != profileId).Select(p => p.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate = name;
        for (int number = 2; taken.Contains(candidate); number++)
        {
            string suffix = FormattableString.Invariant($" ({number})");
            int length = Math.Min(name.Length, maxLength - suffix.Length);
            if (length < name.Length && char.IsHighSurrogate(name[length - 1])) length--;
            candidate = name[..length].TrimEnd() + suffix;
        }
        return candidate;
    }
    public bool DeleteProfile(Guid id)
    {
        if (id == DefaultProfileId) return false;
        var removed = Profiles.RemoveAll(p => p.Id == id) > 0;
        if (ActiveProfileId == id) ActiveProfileId = DefaultProfileId;
        return removed;
    }
    private static void ValidatePlacement(WindowPlacement p)
    {
        if (p is null || p.MonitorId is null || p.MonitorId.Length > 2048 || p.MonitorDevice is null || p.MonitorDevice.Length > 512
            || p.MonitorWorkX.HasValue != p.MonitorWorkY.HasValue || !double.IsFinite(p.Width) || !double.IsFinite(p.Height)
            || p.Width is < 200 or > 10000 || p.Height is < 100 or > 10000)
            throw new InvalidDataException("창 위치 설정이 올바르지 않습니다.");
    }
}

public sealed record StudyResult(
    [property: JsonPropertyName("full_translation")] string? FullTranslation,
    [property: JsonPropertyName("sentence_explanation")] string? SentenceExplanation,
    [property: JsonPropertyName("vocabulary")] IReadOnlyList<VocabularyItem> Vocabulary);
public sealed record VocabularyItem(
    [property: JsonPropertyName("word")] string Word,
    [property: JsonPropertyName("pronunciation")] string Pronunciation,
    [property: JsonPropertyName("meaning_src")] string MeaningSource,
    [property: JsonPropertyName("meaning_ko")] string MeaningKorean,
    [property: JsonPropertyName("examples")] IReadOnlyList<StudyExample> Examples,
    [property: JsonPropertyName("tip")] string Tip);
public sealed record StudyExample(
    [property: JsonPropertyName("en_or_jp")] string Source,
    [property: JsonPropertyName("ko")] string Korean);
