// Adapted independently from breadu/MovieMalhae 88dfc708; word dictionaries only.
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KoTrans.App;

public static partial class DictionaryBrowser
{
    public static string FindExecutable()
    {
        foreach (var browser in new[] { ("msedge.exe", @"Microsoft\Edge\Application\msedge.exe"), ("chrome.exe", @"Google\Chrome\Application\chrome.exe") })
        {
            foreach (var hive in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
            {
                var path = Registry.GetValue(hive + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + browser.Item1, "", null) as string;
                if (path != null && File.Exists(path.Trim('"'))) return path.Trim('"');
            }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                var path = Path.Combine(root, browser.Item2); if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException("사전 팝업에는 Microsoft Edge 또는 Google Chrome이 필요합니다. 브라우저를 설치한 뒤 다시 시도해 주세요.");
    }
    public static string StartPage(string profile) => new Uri(Path.Combine(profile, "dictionary-start.html")).AbsoluteUri;
    private static readonly SemaphoreSlim LookupGate = new(1, 1);
    public static ProcessStartInfo CreateStartInfo(string profile, DictionaryPopupBounds browserBounds)
    {
        var path = FindExecutable();
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !(Path.GetFileName(path).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("사전 브라우저에는 설치된 msedge.exe 또는 chrome.exe의 전체 경로를 지정해 주세요.");
        return BuildStartInfo(path, profile, browserBounds);
    }
    internal static ProcessStartInfo BuildStartInfo(string path, string profile, DictionaryPopupBounds browserBounds)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        info.ArgumentList.Add("--user-data-dir=" + profile);
        info.ArgumentList.Add("--remote-debugging-port=0");
        info.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--no-default-browser-check");
        info.ArgumentList.Add("--disable-extensions");
        info.ArgumentList.Add("--disable-sync");
        info.ArgumentList.Add("--app=" + StartPage(profile));
        info.ArgumentList.Add($"--window-size={browserBounds.Width},{browserBounds.Height}");
        info.ArgumentList.Add($"--window-position={browserBounds.Left},{browserBounds.Top}");
        return info;
    }
    internal static async Task OpenAsync(Uri uri, nint owner, CancellationToken token = default, string? profileRoot = null,
        DictionaryPopupTracker? tracker = null, Profile? gameProfile = null)
    {
        if (uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0) throw new InvalidDataException("사전 주소를 확인하세요.");
        profileRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoTrans2", "DictionaryBrowser");
        var executable = FindExecutable();
        var profile = Path.Combine(profileRoot, Path.GetFileNameWithoutExtension(executable));
        await LookupGate.WaitAsync(token);
        DictionaryConnection? connection = null;
        try
        {
            if (tracker is not null && gameProfile is not null) await tracker.PrepareAsync(gameProfile);
            var restored = DictionaryPopupPlacement.Resolve(gameProfile?.DictionaryPopupWindow, DictionaryPopupPlacement.IdentifiedDisplays(), DictionaryPopupPlacement.ForOwner(owner, 640));
            var bounds = tracker?.CurrentBounds() ?? restored.Bounds;
            var launchBounds = DictionaryBrowserCoordinates.Convert(bounds, DictionaryPopupPlacement.Displays());
            var launch = CreateStartInfo(profile, launchBounds);
            Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(Path.Combine(profile, "dictionary-start.html"), "<!doctype html><meta charset=\"utf-8\"><title>KO Trans 2 Dictionary</title><p>사전을 여는 중입니다...</p>", token);
            // Finish identifying a launched window before honoring a newer click, so rapid clicks
            // cannot create several windows while the first browser process is still starting.
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var targetFile = Path.Combine(profile, "dictionary-target.txt");
            var savedTarget = File.Exists(targetFile) ? await File.ReadAllTextAsync(targetFile, startup.Token) : "";
            connection = await DictionaryConnection.TryConnectAsync(profile, startup.Token);
            string? target = connection == null ? null : await FindTargetAsync(connection, savedTarget, StartPage(profile), startup.Token);
            if (target == null)
            {
                using var process = Process.Start(launch);
                if (process == null) throw new IOException("사전 브라우저를 시작하지 못했습니다.");
                while (target == null)
                {
                    await Task.Delay(150, startup.Token);
                    connection ??= await DictionaryConnection.TryConnectAsync(profile, startup.Token);
                    if (connection != null) target = await FindTargetAsync(connection, savedTarget, StartPage(profile), startup.Token);
                }
            }
            await File.WriteAllTextAsync(targetFile, target, startup.Token);
            token.ThrowIfCancellationRequested();
            using var navigation = CancellationTokenSource.CreateLinkedTokenSource(token);
            navigation.CancelAfter(TimeSpan.FromSeconds(20));
            var ct = navigation.Token;
            var browser = connection!;
            var processes = await browser.SendAsync("SystemInfo.getProcessInfo", null, ct);
            var processId = processes.GetProperty("processInfo").EnumerateArray().First(p => p.GetProperty("type").GetString() == "browser").GetProperty("id").GetInt32();
            await browser.SendAsync("Target.activateTarget", new { targetId = target }, ct);
            IntPtr popup;
            for (int attempt = 0; !DictionaryPopupPlacement.Present(processId, bounds, out popup); attempt++)
            {
                if (attempt >= 20) throw new IOException("사전 브라우저 창을 찾지 못했습니다. 창을 닫은 뒤 다시 시도해 주세요.");
                await Task.Delay(100, ct);
            }
            tracker?.Attach(popup, restored.Temporary);
            var attached = await browser.SendAsync("Target.attachToTarget", new { targetId = target, flatten = true }, ct);
            var session = attached.GetProperty("sessionId").GetString()!;
            var result = await browser.SendAsync("Page.navigate", new { url = uri.AbsoluteUri }, ct, session);
            if (result.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } message)
                throw new IOException("사전 페이지를 열지 못했습니다: " + message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("사전 브라우저 응답을 기다리다 시간이 초과되었습니다. 잠시 후 단어를 다시 클릭해 주세요."); }
        finally { connection?.Dispose(); LookupGate.Release(); }
    }
    private static async Task<string?> FindTargetAsync(DictionaryConnection connection, string savedTarget, string startPage, CancellationToken token)
    {
        var targets = (await connection.SendAsync("Target.getTargets", null, token)).GetProperty("targetInfos");
        foreach (var target in targets.EnumerateArray())
            if (target.GetProperty("type").GetString() == "page" && target.GetProperty("targetId").GetString() == savedTarget) return savedTarget;
        foreach (var target in targets.EnumerateArray())
            if (target.GetProperty("type").GetString() == "page" && target.GetProperty("url").GetString() == startPage)
                return target.GetProperty("targetId").GetString();
        return null;
    }
}
