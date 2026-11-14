namespace KoTrans.Infrastructure;

public static class VoiceDataPaths
{
    public static string? FindDirectory(string applicationDirectory, string? configuredDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory)) return Path.GetFullPath(configuredDirectory);
        var application = Path.GetFullPath(applicationDirectory);
        var besideExe = Path.Combine(application, "voice");
        if (File.Exists(Path.Combine(besideExe, "word.idx"))) return besideExe;
        // A checkout's voice directory is development data, never a packaged resource.
        for (var dir = new DirectoryInfo(application); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "KoTrans.slnx"))) {
                var local = Path.Combine(dir.FullName, "voice");
                return File.Exists(Path.Combine(local, "word.idx")) ? local : null;
            }
        return null;
    }
}
