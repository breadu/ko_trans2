using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// DPAPI CurrentUser, UI forbidden. No key or ciphertext is kept in settings.json.
public sealed class ApiKeyStore(string directory)
{
    private string CredentialPath(TranslationProvider provider, string field)
    {
        // Use only static catalog field IDs, never a user supplied path segment.
        var info = TranslationProviderCatalog.Get(provider);
        if (string.IsNullOrEmpty(field) || field.Length > 40 || field.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')
            || !info.Credentials.Any(c => c.Id == field))
            throw new TranslationException("이 제공자가 지원하지 않는 인증 항목입니다.");
        return Path.Combine(directory, field == "ApiKey" ? provider + ".key" : provider + "." + field + ".key");
    }
    public bool ContainsCredential(TranslationProvider provider, string field) => File.Exists(CredentialPath(provider, field));
    public void DeleteCredential(TranslationProvider provider, string field) => File.Delete(CredentialPath(provider, field));
    public IReadOnlyDictionary<string, string> ReadCredentials(TranslationProvider provider)
    {
        var result = new Dictionary<string, string>();
        foreach (var field in TranslationProviderCatalog.Get(provider).Credentials)
        {
            var value = ReadCredential(provider, field.Id);
            if (value is not null) result[field.Id] = value;
        }
        return result;
    }
    public bool Contains(TranslationProvider provider) =>
        TranslationProviderCatalog.Get(provider).Credentials.Any(c => c.Id == "ApiKey") && ContainsCredential(provider, "ApiKey");
    public void Delete(TranslationProvider provider)
    {
        if (TranslationProviderCatalog.Get(provider).Credentials.Any(c => c.Id == "ApiKey"))
            DeleteCredential(provider, "ApiKey");
    }
    public void Save(TranslationProvider provider, string key) => SaveCredential(provider, "ApiKey", key);
    public void SaveCredential(TranslationProvider provider, string field, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new TranslationException("API 키 형식을 확인하세요.");
        var clear = Encoding.UTF8.GetBytes(key.Trim());
        try
        {
            var encrypted = Protect(clear, false);
            Directory.CreateDirectory(directory);
            string path = CredentialPath(provider, field), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(encrypted); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public string? Read(TranslationProvider provider) => Contains(provider) ? ReadCredential(provider, "ApiKey") : null;
    public string? ReadCredential(TranslationProvider provider, string field)
    {
        var path = CredentialPath(provider, field);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new TranslationException("API 키 보호 파일이 손상되었습니다. 키를 다시 등록하세요.");
        var clear = Protect(File.ReadAllBytes(path), true);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    // Only called after an explicit file picker selection. Reads Settings keys; never writes the INI.
    public int ImportSelectedIni(string path)
    {
        if (new FileInfo(path).Length > 1_048_576) throw new TranslationException("INI 파일이 너무 큽니다.");
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        bool settings = false; int imported = 0;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { settings = line.Equals("[Settings]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!settings || line.StartsWith(';') || line.StartsWith('#')) continue;
            var parts = line.Split('=', 2); if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1])) continue;
            TranslationProvider? provider = parts[0].Trim().ToUpperInvariant() switch
            { "GEMINI_API_KEY" => TranslationProvider.Gemini, "OPENAI_API_KEY" => TranslationProvider.OpenAI, _ => null };
            if (provider.HasValue) { Save(provider.Value, parts[1]); imported++; }
        }
        return imported;
    }
    private static byte[] Protect(byte[] bytes, bool decrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool success = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new TranslationException("현재 Windows 계정으로 API 키를 보호/복호화할 수 없습니다. 키를 다시 등록하세요.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
