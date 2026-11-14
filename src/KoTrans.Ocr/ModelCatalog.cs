using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoTrans.Ocr;

public sealed record ModelTensorContract(string InputName, string OutputName, int[] InputShape, int[] OutputShape);

public sealed class ModelSpec
{
    // Omit absent metadata to preserve legacy QA contract hashes exactly.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelTensorContract? TensorContract { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Version { get; set; } = "PP-OCRv5";
    public string[] Languages { get; set; } = [];
    public string Source { get; set; } = "";
    public string License { get; set; } = "Apache-2.0";
    public string File { get; set; } = "model.onnx";
    public string Sha256 { get; set; } = "";
    public string Dictionary { get; set; } = "dictionary.txt";
    public string DictionarySha256 { get; set; } = "";
    public string ChannelOrder { get; set; } = "BGR";
    public string Layout { get; set; } = "NCHW";
    public string Decoder { get; set; } = "CTC";
    public int Height { get; set; } = 48;
    public int MinWidth { get; set; } = 320;
    public int MaxWidth { get; set; } = 3200;
    public int DetectionLongSide { get; set; } = 960;
    public float[] Mean { get; set; } = [0.5f, 0.5f, 0.5f];
    public float[] Std { get; set; } = [0.5f, 0.5f, 0.5f];
    public float Threshold { get; set; } = .3f;
    public float BoxThreshold { get; set; } = .6f;
    public float UnclipRatio { get; set; } = 1.5f;
}

public static class ModelCatalog
{
    // Optional, explicitly prepared CPU mobile set. Verified by Load; original precision models remain intact.
    public static OcrExecutionOptions PreferredExecution(string root)
    {
        var fast = Path.Combine(root, "fast");
        return new OcrExecutionOptions(File.Exists(Path.Combine(fast, "det", "manifest.json")) ? fast : null,
            RecognizerRoot: File.Exists(Path.Combine(fast, "ja", "manifest.json")) ? fast : null);
    }
    public static string FindDirectory() => OcrModelPaths.FindDirectory();
    public static string FindDirectory(string applicationDirectory, string localDataDirectory, string? explicitPath)
        => OcrModelPaths.FindDirectory(applicationDirectory, localDataDirectory, explicitPath);
    public static ModelSpec Inspect(string root, string folder)
    {
        var dir = Path.Combine(root, folder); var manifest = Path.Combine(dir, "manifest.json");
        if (!System.IO.File.Exists(manifest)) throw new FileNotFoundException("OCR 모델이 없습니다. 프로그램과 함께 제공된 models 폴더를 복원하세요. 별도 모델 경로를 지정했다면 해당 폴더도 확인하세요.", manifest);
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException("모델 manifest 크기가 비정상입니다.");
        ModelSpec spec;
        try { spec = JsonSerializer.Deserialize<ModelSpec>(System.IO.File.ReadAllText(manifest)) ?? throw new InvalidDataException("모델 manifest가 비어 있습니다."); }
        catch (JsonException ex) { throw new InvalidDataException("모델 manifest가 손상되었습니다. 배포 패키지의 모델 폴더를 복원하세요.", ex); }
        static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        if (string.IsNullOrWhiteSpace(spec.File) || !Hash(spec.Sha256)
            || spec.Kind == "recognition" && (string.IsNullOrWhiteSpace(spec.Dictionary) || !Hash(spec.DictionarySha256))
            || spec.ChannelOrder != "BGR" || spec.Layout != "NCHW" || spec.Mean is not { Length: 3 } || spec.Std is not { Length: 3 } || spec.Languages is null
            || spec.Std.Any(x => x <= 0 || !float.IsFinite(x)) || spec.Mean.Any(x => !float.IsFinite(x))
            || spec.Height is < 8 or > 128 || spec.MinWidth < 16 || spec.MaxWidth > 3200 || spec.MinWidth > spec.MaxWidth
            || spec.DetectionLongSide is < 32 or > 1920 || spec.Kind is not ("recognition" or "detection")
            || spec.Decoder != (spec.Kind == "detection" ? "DB" : "CTC")
            || spec.Kind != (folder == "det" ? "detection" : "recognition")
            || !float.IsFinite(spec.Threshold) || spec.Threshold is <= 0 or >= 1
            || !float.IsFinite(spec.BoxThreshold) || spec.BoxThreshold is <= 0 or > 1
            || !float.IsFinite(spec.UnclipRatio) || spec.UnclipRatio is <= 0 or > 5) throw new InvalidDataException("지원하지 않는 모델 전처리/디코더 계약입니다.");
        if (spec.TensorContract is { } tensor && (string.IsNullOrWhiteSpace(tensor.InputName) || string.IsNullOrWhiteSpace(tensor.OutputName)
            || tensor.InputShape is not { Length: 4 } || tensor.InputShape[1] != 3 || tensor.OutputShape is null
            || tensor.OutputShape.Length != (spec.Kind == "detection" ? 4 : 3)))
            throw new InvalidDataException("ONNX tensor 계약이 올바르지 않습니다.");
        return spec;
    }
    public static string FilePath(string root, string folder, string relative)
    {
        var dir = Path.GetFullPath(Path.Combine(root, folder));
        var path = Path.GetFullPath(Path.Combine(dir, relative));
        if (!path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("모델 경로가 캐시 밖을 가리킵니다.");
        return path;
    }
    public static (ModelSpec Spec, string Path, string[] Characters) Load(string root, string folder)
    {
        var spec = Inspect(root, folder);
        string Verify(string relative, string sha)
        {
            var path = FilePath(root, folder, relative);
            using var stream = System.IO.File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("모델 또는 사전 SHA-256이 manifest와 다릅니다. 모델을 다시 준비하세요.");
            return path;
        }
        var model = Verify(spec.File, spec.Sha256);
        var characters = spec.Kind == "recognition" ? System.IO.File.ReadAllLines(Verify(spec.Dictionary, spec.DictionarySha256)) : [];
        return (spec, model, characters);
    }
}
