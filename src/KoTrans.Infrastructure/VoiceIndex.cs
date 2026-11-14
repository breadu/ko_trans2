using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using KoTrans.Core;
using System.Buffers.Binary;

namespace KoTrans.Infrastructure;

public sealed class VoiceIndex
{
    private readonly Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;
    public int Count => entries.Count;
    public int RejectedEntries { get; private set; }
    private VoiceIndex(string directory) => root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    public static VoiceIndex Load(string directory)
    {
        using var input = OpenIndex(directory);
        return Load(directory, input);
    }
    internal static FileStream OpenIndex(string directory) => new VoiceIndex(directory).OpenRelative("word.idx", 64 * 1024 * 1024, false);
    internal static VoiceIndex Load(string directory, FileStream input)
    {
        var index = new VoiceIndex(directory);
        using var buffer = new MemoryStream(); input.CopyTo(buffer); var data = buffer.ToArray();
        int start = 0;
        if (data.AsSpan().StartsWith("LNVI"u8)) {
            // The original voice data has a 12-byte header and little-endian
            // payload-relative offsets before its UTF-16LE word/MP3 records.
            if (data.Length < 12) throw Invalid();
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            long payload = 12L + count * 4L;
            if (count is 0 or > 500000 || payload + length != data.Length) throw Invalid();
            uint previous = 0;
            for (int i = 0; i < count; i++) {
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12 + i * 4));
                if ((i == 0 && offset != 0) || (i > 0 && offset <= previous) || offset >= length) throw Invalid();
                previous = offset;
            }
            start = (int)payload;
        }
        while (start < data.Length)
        {
            int separator = -1;
            for (int i = start; i + 2 < data.Length; i++) if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0) { separator = i; break; }
            if (separator < 0) { index.RejectedEntries++; break; }
            int pathStart = separator + 3;
            // A UTF-16 ASCII character's high zero can be the first delimiter byte in legacy indexes.
            if (pathStart < data.Length && data[pathStart] == 0) pathStart++;
            int end = Array.IndexOf(data, (byte)0, pathStart);
            if (end < 0) { index.RejectedEntries++; break; }
            try
            {
                int length = separator - start;
                if (length is < 1 or > 800 || end - pathStart is < 5 or > 1024) throw Invalid();
                var wordBytes = new byte[length + (length % 2)]; Array.Copy(data, start, wordBytes, 0, length);
                var word = new UnicodeEncoding(false, false, true).GetString(wordBytes).Trim();
                if (word.Length == 0 || word.Any(char.IsControl) || data.AsSpan(pathStart, end - pathStart).ContainsAnyInRange((byte)128, byte.MaxValue)) throw Invalid();
                var path = Encoding.ASCII.GetString(data, pathStart, end - pathStart); ValidateRelative(path, true);
                index.entries.TryAdd(word, path); if (index.entries.Count > 500000) throw new TranslationException("발음 인덱스 항목이 너무 많습니다.");
            }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException) { index.RejectedEntries++; }
            start = end + 1;
        }
        if (index.Count == 0) throw new TranslationException("읽을 수 있는 LNVI 발음 항목이 없습니다. word.idx 형식을 확인하세요.");
        return index;
    }
    public bool Contains(string word) => entries.ContainsKey(word.Trim());
    public FileStream OpenAudio(string word)
    {
        if (!entries.TryGetValue(word.Trim(), out var path)) throw new TranslationException("이 단어의 로컬 발음이 없습니다.");
        return OpenRelative(path, 16 * 1024 * 1024, true);
    }
    private FileStream OpenRelative(string relative, long maximum, bool audio)
    {
        ValidateRelative(relative, audio);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        CheckLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // Check the opened handle as well, closing the path-validation/open race.
            var final = new StringBuilder(32768); uint size = GetFinalPathNameByHandle(stream.SafeFileHandle, final, (uint)final.Capacity, 0);
            if (size == 0 || size >= final.Capacity) throw Invalid();
            var actual = final.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (!actual.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || stream.Length is <= 0 || stream.Length > maximum) throw Invalid();
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private void CheckLinks(string path)
    {
        for (string? cursor = path; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new TranslationException("발음 데이터의 심볼릭 링크·junction 경로는 지원하지 않습니다. 실제 폴더를 선택하세요.");
    }
    private static void ValidateRelative(string path, bool audio)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\0') || (audio && !path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))) throw Invalid();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("발음 인덱스 또는 파일 경로가 올바르지 않습니다.");
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
