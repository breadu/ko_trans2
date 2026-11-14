using System.Reflection;
namespace KoTrans.Infrastructure;

public static class ReleaseIdentity
{
    public static string For(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString() ?? throw new InvalidOperationException("프로그램 버전을 확인할 수 없습니다.");
}
