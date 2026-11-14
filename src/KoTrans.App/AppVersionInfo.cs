using System.Reflection;

namespace KoTrans.App;

// Display only: the authoritative version comes from <Version> in KoTrans.App.csproj.
// SourceRevisionId (when provided by the release script) remains in assembly metadata.
internal static class AppVersionInfo
{
    internal static string InformationalVersion =>
        typeof(AppVersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersionInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    internal static string StatusLabel(bool devRun) => FormatStatus(InformationalVersion, devRun);

    internal static string StatusTooltip(bool devRun) => FormatTooltip(InformationalVersion, devRun);

    internal static string FormatStatus(string? informationalVersion, bool devRun)
    {
        string version = Normalize(informationalVersion);
        int metadataAt = version.IndexOf('+');
        string release = metadataAt >= 0 ? version[..metadataAt] : version;
        int suffixAt = release.IndexOf('-');
        string numeric = suffixAt >= 0 ? release[..suffixAt] : release;
        string suffix = suffixAt >= 0 ? release[suffixAt..] : "";
        var parts = numeric.Split('.');
        // Keep patch versions visible (1.0.1), but make the initial 1.0.0 concise.
        if (parts.Length == 3 && parts[2] == "0") numeric = parts[0] + "." + parts[1];
        if (devRun && suffix.Length == 0) suffix = "-dev";
        return "Ver " + numeric + suffix;
    }

    internal static string FormatTooltip(string? informationalVersion, bool devRun)
    {
        string version = Normalize(informationalVersion);
        int metadataAt = version.IndexOf('+');
        string release = metadataAt >= 0 ? version[..metadataAt] : version;
        string result = "KO Trans 2 · Version " + release + (devRun ? "\nDEV-RUN" : "");
        if (metadataAt >= 0 && metadataAt + 1 < version.Length)
            result += "\nBuild: " + version[(metadataAt + 1)..];
        return result;
    }

    private static string Normalize(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim();
}
