using Microsoft.ML.OnnxRuntime;

namespace KoTrans.Ocr;

// Explicit OcrLab diagnostics only. The app never enables profiling or writes
// input/output tensors. Finalize after the first request, outside warm timing.
internal static class DirectMlTrace
{
    internal static bool Enabled;
    private static readonly List<InferenceSession> pending = [];
    private static readonly List<string> paths = [];
    internal static void Prepare(SessionOptions options)
    {
        if (!Enabled) return;
        string directory = Path.Combine(Path.GetTempPath(), "KoTrans2.OcrProfiles"); Directory.CreateDirectory(directory);
        options.EnableProfiling = false;
        options.ProfileOutputPathPrefix = Path.Combine(directory, Guid.NewGuid().ToString("N")); options.EnableProfiling = true;
    }
    internal static void Register(InferenceSession session) { if (Enabled) pending.Add(session); }
    internal static IReadOnlyList<string>? Finish()
    {
        if (!Enabled) return null;
        foreach (var session in pending) paths.Add(session.EndProfiling());
        pending.Clear(); return paths.ToArray();
    }
}
