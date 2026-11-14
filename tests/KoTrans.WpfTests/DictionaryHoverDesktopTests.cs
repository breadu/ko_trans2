using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

// Explicit Desktop only: real DWM/GDI pixels, no physical input or user/game settings.
internal static class DictionaryHoverDesktopTests
{
    internal static void Run()
    {
        var fixture = Track(new Window { Title = "KO Trans 2 · dictionary capture A/B", Width = 620, Height = 280,
            Left = 80, Top = 80, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, Background = Brushes.DarkSlateGray,
            Content = new TextBlock { Text = "Fixed synthetic game frame\nモニター　言葉　物語", Foreground = Brushes.White, FontSize = 24, Margin = new Thickness(20) } });
        using var tokenizer = new WordDictionaryTokenizer();
        var profile = new Profile { OverlayClickThrough = false, WordDictionaryEnabled = true,
            OverlayWindow = new() { X = 80, Y = 80, Width = 620, Height = 280 } };
        var overlay = Track(new TranslationWindow(profile,
            (text, _, token) => Task.FromResult(tokenizer.Tokenize(text, "en", token)))); Track(overlay.Chrome);
        var original = Named<DictionarySubtitle>(overlay, "OverlayOriginal");
        var tooltip = new ToolTip { Content = "사전: モニター", PlacementTarget = original, Placement = PlacementMode.Relative,
            HorizontalOffset = 10, VerticalOffset = 38 };
        try {
            fixture.Show(); overlay.SetResult("monitor story words", "합성 번역", ""); overlay.Show();
            Complete(Task.Delay(400));
            if (!overlay.CaptureExcluded) throw new DesktopEnvironmentException("Overlay SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) unavailable.");
            var bounds = WindowTargets.Bounds(new WindowInteropHelper(fixture).Handle)
                ?? throw new DesktopEnvironmentException("Synthetic fixture bounds unavailable.");
            ImageFrame Capture() { Complete(Task.Delay(200)); return ScreenCapture.Capture(bounds); }
            var baseline = Capture();
            for (int trial = 1; trial <= 2; trial++) {
                // Removed implementation: no popup. Real hover paint stays in the excluded HWND.
                original.Hover(0); var hoverOnly = Capture();
                original.Hover(8); var movedHover = Capture();
                Check(Changed(baseline, hoverOnly) == 0 && Changed(baseline, movedHover) == 0,
                    "Excluded overlay hover changed captured game pixels");
                int withoutTooltip = CountAdditionalOcr(baseline, movedHover);

                // Recreate precisely the old separate WPF tooltip, without moving the mouse.
                tooltip.Content = trial == 1 ? "사전: モニター" : "사전: 物語";
                tooltip.IsOpen = true; var withTooltip = Capture();
                long changed = Changed(baseline, withTooltip);
                int withTooltipOcr = CountAdditionalOcr(baseline, withTooltip);
                tooltip.IsOpen = false; original.Hover(-1); var restored = Capture();
                long remaining = Changed(baseline, restored);
                Console.WriteLine($"Dictionary capture A/B trial {trial}: tooltip OFF changed=0px, extra Mock OCR={withoutTooltip}; tooltip ON changed={changed}px, extra Mock OCR={withTooltipOcr}; removed again changed={remaining}px. Real GDI/DWM, no image files.");
                Check(changed > 0 && remaining == 0, "Tooltip ON/OFF did not reproduce a reversible captured pixel change");
                Check(withoutTooltip == 0 && withTooltipOcr > 0, "Tooltip A/B did not explain OCR retriggering");
            }
        } finally { tooltip.IsOpen = false; Close(overlay); Close(overlay.Chrome); Close(fixture); }
    }
    private static long Changed(ImageFrame a, ImageFrame b)
    {
        Check(a.Width == b.Width && a.Height == b.Height, "A/B capture size changed");
        long count = 0; var left = a.Pixels.Span; var right = b.Pixels.Span;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) {
            int offset = y * a.Stride + x * 4;
            if (!left.Slice(offset, 3).SequenceEqual(right.Slice(offset, 3))) count++;
        }
        return count;
    }
    private static int CountAdditionalOcr(ImageFrame baseline, ImageFrame candidate)
    {
        ImageFrame current = baseline;
        var ocr = new CountingOcr();
        var pipeline = new TranslationPipeline(ocr, null, _ => Task.FromResult(Volatile.Read(ref current)),
            new("en", "NVL", false), new(), new TranslationSession(), "", interval: TimeSpan.FromMilliseconds(40), settle: TimeSpan.Zero, translationEnabled: false);
        try {
            pipeline.Start(true);
            Until(() => pipeline.TryGetRecognized(baseline, out _), "A/B baseline OCR didn't settle");
            Complete(Task.Delay(180)); int before = ocr.Calls;
            Volatile.Write(ref current, candidate); Complete(Task.Delay(700));
            return ocr.Calls - before;
        } finally { Complete(pipeline.DisposeAsync().AsTask()); }
    }
    private sealed class CountingOcr : IOcrService
    {
        internal int Calls;
        public OcrState State => OcrState.Ready;
        public Task InitializeAsync(OcrOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new OcrResult([new("Fixed synthetic game dialogue.", 1, new(0, 0, frame.Width, frame.Height))], TimeSpan.Zero));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
