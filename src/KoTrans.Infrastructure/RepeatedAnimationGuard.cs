using KoTrans.Core;

namespace KoTrans.Infrastructure;

internal enum AnimationFrameDecision { Unknown, Reuse, Verify }

// Learns a small changing decoration only after real OCR repeatedly confirms the same
// text and geometry. No game names, fixed screen coordinates or marker templates.
internal sealed class RepeatedAnimationGuard(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private ImageFrame? baseline;
    private OcrLine[] lines = [];
    private PixelRect? decoration;
    private string source = "";
    private int confirmations, scale;
    private long verifiedAt;
    internal static readonly TimeSpan VerificationInterval = TimeSpan.FromSeconds(2);

    internal void Reset()
    { baseline = null; lines = []; decoration = null; source = ""; confirmations = scale = 0; verifiedAt = 0; }

    internal void Observe(ImageFrame frame, OcrResult result, string text)
    {
        var observed = result.Lines.ToArray();
        if (text.Length == 0 || observed.Length == 0 || observed.Any(l => !(l.Confidence >= .8f) || !Inside(l.Bounds, frame))) { Reset(); return; }
        if (baseline is null || !Compatible(baseline, frame) || source != text || !SameGeometry(observed)) {
            Reset(); lines = observed; source = text;
            var sizes = lines.Select(l => l.Vertical ? l.Bounds.Width : l.Bounds.Height).Order().ToArray();
            scale = Math.Clamp(sizes[sizes.Length / 2], 8, 96);
        }
        else {
            var change = FindChange(frame);
            if (change is { } changed && Compact(changed)) {
                var combined = decoration is { } previous ? Union(previous, changed) : changed;
                if (Compact(combined)) { decoration = combined; confirmations++; }
                else { decoration = changed; confirmations = 1; }
            }
            else if (change is { Width: < 0 }) { decoration = null; confirmations = 0; }
        }
        baseline = frame; verifiedAt = clock.GetTimestamp();
    }

    internal AnimationFrameDecision Evaluate(ImageFrame frame)
    {
        if (baseline is null) return AnimationFrameDecision.Unknown;
        if (!Compatible(baseline, frame)) { Reset(); return AnimationFrameDecision.Unknown; }
        if (confirmations < 2 || decoration is not { } area) return AnimationFrameDecision.Unknown;
        var changed = FindChange(frame);
        if (changed is null || !Compact(changed.Value) || !Contains(Inflate(area, Math.Max(2, scale / 2), frame), changed.Value))
            return AnimationFrameDecision.Unknown;
        // A new small character appearing inside a learned decoration can be ambiguous.
        // Force a real full search, bypassing the exact-frame cache, at this deadline.
        return clock.GetElapsedTime(verifiedAt) >= VerificationInterval ? AnimationFrameDecision.Verify : AnimationFrameDecision.Reuse;
    }

    private bool SameGeometry(OcrLine[] observed) => observed.Length == lines.Length
        && observed.Zip(lines).All(p => p.First.Bounds == p.Second.Bounds && p.First.Vertical == p.Second.Vertical);
    private static bool Compatible(ImageFrame a, ImageFrame b) => a.Width == b.Width && a.Height == b.Height && a.PixelFormat == b.PixelFormat;
    private static bool Inside(PixelRect r, ImageFrame f) => r.IsValid && r.X >= 0 && r.Y >= 0 && (long)r.X + r.Width <= f.Width && (long)r.Y + r.Height <= f.Height;
    private bool Compact(PixelRect r) => r.IsValid && r.Width <= scale * 2 && r.Height <= scale * 2;
    private static PixelRect Union(PixelRect a, PixelRect b)
    { int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y); return new(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y); }
    private static bool Contains(PixelRect a, PixelRect b) => b.X >= a.X && b.Y >= a.Y && b.X + (long)b.Width <= a.X + (long)a.Width && b.Y + (long)b.Height <= a.Y + (long)a.Height;
    private static PixelRect Inflate(PixelRect r, int pad, ImageFrame frame)
    { int x = Math.Max(0, r.X - pad), y = Math.Max(0, r.Y - pad); return new(x, y, Math.Min(frame.Width, r.X + r.Width + pad) - x, Math.Min(frame.Height, r.Y + r.Height + pad) - y); }

    // Allocation-free grayscale comparison. Exit immediately for changed text or a
    // large/dispersed scene change; a failed classification always falls back to OCR.
    private PixelRect? FindChange(ImageFrame frame)
    {
        var before = baseline!.Pixels.Span; var after = frame.Pixels.Span;
        int left = frame.Width, top = frame.Height, right = -1, bottom = -1, count = 0;
        for (int y = 0; y < frame.Height; y++) for (int x = 0; x < frame.Width; x++) {
            int a = y * baseline.Stride + x * 4, b = y * frame.Stride + x * 4;
            int oldGray = (before[a] * 29 + before[a + 1] * 150 + before[a + 2] * 77) >> 8;
            int newGray = (after[b] * 29 + after[b + 1] * 150 + after[b + 2] * 77) >> 8;
            if (Math.Abs(newGray - oldGray) <= 18) continue;
            foreach (var line in lines) {
                var r = line.Bounds; int pad = Math.Max(2, scale / 10);
                if (x >= r.X - pad && x < r.X + r.Width + pad && y >= r.Y - pad && y < r.Y + r.Height + pad) return new(0, 0, -1, -1);
            }
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            if (right - left + 1 > scale * 2 || bottom - top + 1 > scale * 2) return new(0, 0, -1, -1);
            count++;
        }
        return count < 5 ? null : new(left, top, right - left + 1, bottom - top + 1);
    }
}
