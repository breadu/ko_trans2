namespace KoTrans.Ocr;

// Common location policy only; engines own their manifests and model contracts.
public static class OcrModelPaths
{
    public static string FindDirectory() => FindDirectory(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("KO_TRANS2_MODELS"));
    public static string FindDirectory(string applicationDirectory, string localDataDirectory, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        var besideExe = Path.Combine(Path.GetFullPath(applicationDirectory), "models");
        if (Directory.Exists(besideExe)) return besideExe;
        for (var dir = new DirectoryInfo(applicationDirectory); dir is not null; dir = dir.Parent)
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "KoTrans.slnx"))) {
                var local = Path.Combine(dir.FullName,"models");
                // A linked checkout does not contain Git-ignored models. Reuse only its own
                // common repository's prepared directory, never a guessed sibling project.
                return Directory.Exists(local) ? local : FindCommonCheckoutModels(dir.FullName) ?? local;
            }
        return Path.Combine(localDataDirectory, "KoTrans2", "models");
    }
    private static string? FindCommonCheckoutModels(string checkout)
    {
        try {
            string marker=Path.Combine(checkout,".git");
            if(!File.Exists(marker) || new FileInfo(marker).Length>4096)return null;
            string link=File.ReadAllText(marker).Trim();
            if(!link.StartsWith("gitdir:",StringComparison.OrdinalIgnoreCase))return null;
            string gitDirectory=Path.GetFullPath(link[7..].Trim(),checkout);
            string commonFile=Path.Combine(gitDirectory,"commondir");
            if(!File.Exists(commonFile) || new FileInfo(commonFile).Length>4096)return null;
            string relative=File.ReadAllText(commonFile).Trim();if(relative.Length==0)return null;
            var common=new DirectoryInfo(Path.GetFullPath(relative,gitDirectory));
            if(!common.Name.Equals(".git",StringComparison.OrdinalIgnoreCase) || common.Parent is not {} root
                || !File.Exists(Path.Combine(root.FullName,"KoTrans.slnx")))return null;
            var models=Path.Combine(root.FullName,"models");return Directory.Exists(models)?models:null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {return null;}
    }
}
