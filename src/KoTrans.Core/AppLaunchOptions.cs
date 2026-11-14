namespace KoTrans.Core;

// Pure argument validation: no directory, settings, desktop or credential access.
public sealed record AppLaunchOptions(string DataDirectory, bool MockAi, bool DebugOverlay = false)
{
    public static AppLaunchOptions Parse(string[] args, string defaultDirectory)
    {
        bool mock = args.Contains("--mock-ai");
        int index = Array.IndexOf(args, "--test-data");
        if (index < 0)
        {
            if (mock) throw new ArgumentException("모의 AI는 --test-data <별도 폴더>와 함께 실행하세요.");
            return new(defaultDirectory, false, args.Contains("--debug-overlay"));
        }
        if (args.Count(a => a == "--test-data") != 1 || index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("--test-data 뒤에 별도 테스트 폴더 경로를 하나 지정하세요.");
        string directory, production;
        try
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[index + 1]));
            production = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("테스트 폴더 경로가 올바르지 않습니다."); }
        if (string.Equals(directory, production, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("테스트 폴더로 실제 사용자 설정 폴더를 지정할 수 없습니다.");
        return new(directory, mock, args.Contains("--debug-overlay"));
    }
}
