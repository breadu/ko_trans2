using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KoTrans.Infrastructure;

// One folder per player. A shared read handle prevents an index write during parsing.
// Only the index is cached: OpenAudio still validates links, final handle path and size each time.
public sealed class VoiceIndexCache
{
    private readonly object sync = new();
    private string? directory;
    private Stamp stamp;
    private VoiceIndex? index;
    private long generation;
    public VoiceIndex Get(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        long version; lock (sync) version = generation;
        try
        {
            using var input = VoiceIndex.OpenIndex(full);
            if (!GetFileInformationByHandle(input.SafeFileHandle, out var info)) throw new IOException("발음 인덱스 상태를 확인할 수 없습니다.");
            var next = new Stamp(info.Volume, info.IndexHigh, info.IndexLow, info.SizeHigh, info.SizeLow, info.WriteHigh, info.WriteLow);
            lock (sync)
            {
                if (version == generation && index is not null && string.Equals(directory, full, StringComparison.OrdinalIgnoreCase) && stamp == next) return index;
                if (version == generation) { index = null; directory = null; }
            }
            // Parsing may be substantial; UI folder invalidation must not wait for this lock.
            var loaded = VoiceIndex.Load(full, input);
            lock (sync) { if (version == generation) { directory = full; stamp = next; index = loaded; } }
            return loaded;
        }
        catch { lock (sync) { if (version == generation) { index = null; directory = null; } } throw; }
    }
    public void Invalidate() { lock (sync) { generation++; index = null; directory = null; } }
    private readonly record struct Stamp(uint Volume, uint High, uint Low, uint SizeHigh, uint SizeLow, uint WriteHigh, uint WriteLow);
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
