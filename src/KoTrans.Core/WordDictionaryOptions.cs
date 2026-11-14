namespace KoTrans.Core;

// Each game profile owns its URLs. %%SS matches MovieMalhae's dictionary templates.
public sealed class WordDictionaryOptions
{
    public const string DefaultEnglishUrl = "https://en.dict.naver.com/#/search?query=%%SS";
    public const string DefaultJapaneseUrl = "https://ja.dict.naver.com/#/search?query=%%SS";
    public string EnglishUrl { get; set; } = DefaultEnglishUrl;
    public string JapaneseUrl { get; set; } = DefaultJapaneseUrl;
    public void Validate() { ValidateTemplate(EnglishUrl); ValidateTemplate(JapaneseUrl); }
    public static void ValidateTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 2048 || template.Any(char.IsControl)
            || template.IndexOf("%%SS", StringComparison.Ordinal) < 0
            || !Uri.TryCreate(template.Replace("%%SS", "lookup", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0
            || !HasFixedAuthority(template))
            throw new InvalidDataException("단어 사전 주소는 http(s) 주소와 검색어 자리 %%SS를 포함해야 합니다. 예: https://en.dict.naver.com/#/search?query=%%SS");
    }
    private static bool HasFixedAuthority(string template)
    {
        int start = template.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return false;
        int end = template.IndexOfAny(['/', '?', '#'], start + 3);
        return end >= 0 && template.IndexOf("%%SS", StringComparison.Ordinal) >= end;
    }
    public Uri SearchUri(string word, string language)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length > 160 || word.Any(char.IsControl))
            throw new InvalidDataException("사전에서 검색할 단어가 올바르지 않습니다.");
        string template = language switch { "en" => EnglishUrl, "ja" => JapaneseUrl, _ => throw new InvalidDataException("영어·일본어 단어만 검색할 수 있습니다.") };
        ValidateTemplate(template);
        return new Uri(template.Replace("%%SS", Uri.EscapeDataString(word), StringComparison.Ordinal));
    }
}

public sealed record DictionaryWord(int Start, int Length, string Surface, string SearchText);
