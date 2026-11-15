// Adapted independently from breadu/MovieMalhae 88dfc708; word dictionaries only.
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace KoTrans.App;

// Only connects to the app's dedicated dictionary profile, never the user's normal browser.
public sealed class DictionaryConnection : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private int sequence;
    public static async Task<DictionaryConnection?> TryConnectAsync(string profile, CancellationToken token)
    {
        var connection = new DictionaryConnection();
        try
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(profile, "DevToolsActivePort"), token);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535 ||
                !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)) throw new IOException("Invalid dictionary endpoint.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await connection.socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), timeout.Token);
            return connection;
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or UriFormatException)
        { connection.Dispose(); token.ThrowIfCancellationRequested(); return null; }
    }
    // Calls are serialized by DictionaryBrowser. Events are skipped; matching responses are cloned.
    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken token, string? session = null)
    {
        var id = ++sequence;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { }, sessionId = session }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        var buffer = new byte[8192];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (part.MessageType == WebSocketMessageType.Close) throw new DictionaryBrowserException("사전 창과의 연결이 종료되었습니다. 다시 단어를 클릭해 주세요.");
                message.Write(buffer, 0, part.Count);
                if (message.Length > 4 * 1024 * 1024) throw new DictionaryBrowserException("사전 브라우저 응답이 너무 큽니다.");
            } while (!part.EndOfMessage);
            using var json = JsonDocument.Parse(message.ToArray());
            var root = json.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out _)) throw new DictionaryBrowserException("사전 브라우저가 요청을 처리하지 못했습니다. 사전 창을 닫은 뒤 다시 단어를 클릭해 주세요.");
            return root.GetProperty("result").Clone();
        }
    }
    public void Dispose() => socket.Dispose();
}
