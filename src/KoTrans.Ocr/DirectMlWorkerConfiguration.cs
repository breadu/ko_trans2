using KoTrans.Core;

namespace KoTrans.Ocr;

public sealed record DirectMlWorkerConfiguration(string? Executable = null, TimeSpan? StartupTimeout = null,
    TimeSpan? RequestTimeout = null, bool Profile = false)
{
    internal Action<DirectMlWorkerMetrics>? Observe { get; init; }
}
internal sealed record DirectMlWorkerMetrics(int ProcessId, double RoundTripMs, DirectMlReply Reply, long PrivateBytes, int Handles);
