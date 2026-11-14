using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public sealed record MarkerLearningDocument(int Version, string Key, IReadOnlyList<GameMarkerPattern> Patterns);
public sealed class MarkerLearningStore(string directory, string key)
{
    public const int MaxBytes=64*1024;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly string path=SafePath(directory,key);
    private bool invalid;
    private static string SafePath(string directory,string key)
    {
        if(key.Length!=64 || !key.All(Uri.IsHexDigit))throw new ArgumentException("학습 저장 식별자가 올바르지 않습니다.");
        return Path.Combine(directory,key+".json");
    }
    public static string Key(Profile profile,GameWindowIdentity game)
    {
        if(string.IsNullOrWhiteSpace(game.ExecutablePath) || string.IsNullOrWhiteSpace(game.WindowClass))
            throw new InvalidDataException("게임 실행 파일과 창 종류를 확인할 수 없어 학습하지 않습니다.");
        var o=profile.OcrOptions();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            "marker-v1",OcrAlgorithmRevision.Current,profile.Id.ToString("N"),game.ExecutablePath.ToUpperInvariant(),game.WindowClass,
            o.EngineId,o.ModelPresetId,o.Language,o.ReadingMode,o.Vertical.ToString()))));
    }
    private void Validate(MarkerLearningDocument doc)
    {
        if(key.Length!=64 || !key.All(Uri.IsHexDigit) || doc.Version!=1 || doc.Key!=key || doc.Patterns is null || doc.Patterns.Count>12)
            throw new InvalidDataException("학습 정보의 형식이 올바르지 않습니다.");
        if(!GameMarkerGeometry.ValidPatterns(doc.Patterns))throw new InvalidDataException("학습한 표시의 크기·모양 정보가 올바르지 않습니다.");
    }
    public async Task<MarkerLearningDocument> LoadAsync(CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try {
            var empty=new MarkerLearningDocument(1,key,[]);Validate(empty);
            if(!File.Exists(path))return empty;
            if(new FileInfo(path).Length>MaxBytes)throw new InvalidDataException("학습 파일이 너무 큽니다.");
            var doc=JsonSerializer.Deserialize<MarkerLearningDocument>(await File.ReadAllBytesAsync(path,token))
                ?? throw new InvalidDataException("학습 정보가 비어 있습니다.");
            Validate(doc);invalid=false;return doc;
        } catch(Exception ex) when(ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) {
            invalid=true;throw new InvalidDataException("학습 정보를 읽지 못했습니다. 원본을 보존했습니다. 학습 초기화 후 다시 시작하세요.",ex);
        } finally {gate.Release();}
    }
    public async Task SaveAsync(IReadOnlyList<GameMarkerPattern> patterns,CancellationToken token=default)
    {
        var doc=new MarkerLearningDocument(1,key,patterns);Validate(doc);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(doc);if(bytes.Length>MaxBytes)throw new InvalidDataException("학습 파일이 너무 큽니다.");
        await gate.WaitAsync(token);string? temporary=null;
        try {
            if(invalid)throw new InvalidDataException("읽지 못한 학습 원본을 보호하고 있습니다.");
            Directory.CreateDirectory(directory);temporary=Path.Combine(directory,key+"."+Guid.NewGuid().ToString("N")+".tmp");
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {
                await stream.WriteAsync(bytes,token);stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            temporary=null;
        } finally {
            try {if(temporary is not null)File.Delete(temporary);} finally {gate.Release();}
        }
    }
    public async Task ResetAsync(CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try {File.Delete(path);invalid=false;} finally {gate.Release();}
    }
}
