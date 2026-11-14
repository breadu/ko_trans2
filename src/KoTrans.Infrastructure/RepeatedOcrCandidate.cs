using System.Collections.Generic;
using System.Diagnostics;

namespace KoTrans.Infrastructure;

// A bounded alternative to pixel quiet: A/B/A OCR jitter may confirm the latest A,
// but unique typewriter prefixes and repeated processing of one capture cannot.
internal sealed class RepeatedOcrCandidate
{
    private readonly List<(string Text, long Captured)> samples = [];
    private long lastCapture;
    public void Reset() { samples.Clear(); lastCapture = Stopwatch.GetTimestamp(); }

    public bool Observe(string text, long captured, TimeSpan settle)
    {
        if (captured <= lastCapture) return false;
        lastCapture = captured;
        samples.RemoveAll(s => Stopwatch.GetElapsedTime(s.Captured, captured) > TimeSpan.FromSeconds(10));
        bool repeated = samples.Any(s => s.Text == text && Stopwatch.GetElapsedTime(s.Captured, captured) >= settle);
        samples.Add((text, captured));
        if (samples.Count > 8) samples.RemoveAt(0);
        return repeated;
    }
}
