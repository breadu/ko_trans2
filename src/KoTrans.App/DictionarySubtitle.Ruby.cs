using System.Collections.Generic;
using System.Globalization;
using System.Windows.Documents;
using System.Windows.Data;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed partial class DictionarySubtitle
{
    internal const double RubyFontRatio = .6;
    internal sealed record RubyLabel(string Text, Rect Bounds, Rect SourceBounds, double FontSize);
    private sealed record RubySpan(int Start, int Length, string Reading);
    private readonly JapaneseReadingService rubyReader = new();
    private readonly Func<string, CancellationToken, Task<JapaneseReading>> readJapanese;
    private readonly List<RubySpan> rubySpans = [];
    private List<(RubyLabel Label, FormattedText Text)>? rubyDrawing;
    private CancellationTokenSource? rubyCancellation;
    private int rubyRevision;
    private bool furiganaEnabled, rubyMetricsActive, updatingRubyMetrics, rubyAttempted;
    private double plainLineHeight;
    private Thickness plainPadding;
    private Thickness plainMargin;
    private RubyLayer? rubyLayer;
    static DictionarySubtitle()
    {
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, ForegroundProperty, FontWeightProperty,
            FontStyleProperty, PaddingProperty, LineHeightProperty, TextProperty })
            property.OverrideMetadata(typeof(DictionarySubtitle), new FrameworkPropertyMetadata(
                property.GetMetadata(typeof(TextBlock)).DefaultValue, RubyStyleChanged));
    }
    internal FrameworkElement CreateRubyLayer()
    {
        if (rubyLayer is not null) return rubyLayer;
        rubyLayer = new RubyLayer(this) { IsHitTestVisible = false, Focusable = false };
        rubyLayer.SetBinding(MarginProperty, new Binding(nameof(Margin)) { Source = this });
        rubyLayer.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = this });
        return rubyLayer;
    }
    internal bool FuriganaEnabled => furiganaEnabled;
    internal int RubySpanCount => rubySpans.Count;
    private bool RubyActive => furiganaEnabled && language == "ja";
    private JapaneseReading ReadRubyLocally(string text, CancellationToken token)
    {
        var tokens = new List<ReadingToken>();
        // Analyze lines independently and omit unsupported surrogate spans, as
        // the existing dictionary tokenizer does. Never rewrite the OCR text.
        foreach (var line in text.Split('\n')) {
            int start = 0;
            for (int i = 0; i <= line.Length; i++) {
                if (i < line.Length && !char.IsSurrogate(line[i])) continue;
                if (i > start) {
                    string part = line[start..i];
                    if (!string.IsNullOrWhiteSpace(part) && part.Length <= 2000) tokens.AddRange(rubyReader.Read(part, token).Tokens);
                }
                if (i < line.Length && char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) i++;
                start = i + 1;
            }
        }
        return new(text, tokens, "");
    }

    internal void SetFuriganaEnabled(bool enabled)
    {
        if (furiganaEnabled == enabled) return;
        furiganaEnabled = enabled; rubyRevision++;
        rubyCancellation?.Cancel(); rubyCancellation?.Dispose(); rubyCancellation = null;
        if (!rubyAttempted || rubySpans.Count == 0) rubyAttempted = false;
        UpdateRubyMetrics(); InvalidateRubyDrawing(); StartRubyAnalysis();
    }
    private void RubySourceChanged()
    {
        rubyRevision++; rubySpans.Clear(); rubyAttempted = false;
        rubyCancellation?.Cancel(); rubyCancellation?.Dispose(); rubyCancellation = null;
        UpdateRubyMetrics(); InvalidateRubyDrawing(); StartRubyAnalysis();
    }
    private async void StartRubyAnalysis()
    {
        if (disposed || !RubyActive || rubyAttempted || string.IsNullOrWhiteSpace(source)
            || !source.Any(c => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff')) return;
        rubyAttempted = true; rubyCancellation = new();
        var token = rubyCancellation.Token; int request = rubyRevision; string text = source;
        try {
            var reading = await readJapanese(text, token);
            if (disposed || token.IsCancellationRequested || request != rubyRevision || text != source || !RubyActive) return;
            var spans = new List<RubySpan>(); int offset = 0;
            foreach (var part in reading.Tokens) {
                if (string.IsNullOrEmpty(part.Surface)) continue;
                int index = text.IndexOf(part.Surface, offset, StringComparison.Ordinal);
                if (index < offset) throw new InvalidDataException("후리가나 위치가 원문과 일치하지 않습니다.");
                if (part.HasKanji && JapaneseRubyLayout.CanShowRuby(part.Surface, part.Hiragana))
                    spans.Add(new(index, part.Surface.Length, part.Hiragana!));
                offset = index + part.Surface.Length;
            }
            rubySpans.Clear(); rubySpans.AddRange(spans); InvalidateRubyDrawing();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (!disposed && request == rubyRevision && !token.IsCancellationRequested) {
                rubyAttempted = false;
                AnalysisFailed?.Invoke(AnalysisFailureMessage("한자 읽기를 표시하지 못했습니다.", ex));
            }
        }
    }
    private static void RubyStyleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var owner = (DictionarySubtitle)sender;
        if (owner.updatingRubyMetrics) return;
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty) owner.UpdateRubyMetrics();
        owner.InvalidateRubyDrawing();
    }
    private void UpdateRubyMetrics()
    {
        if (updatingRubyMetrics) return;
        updatingRubyMetrics = true;
        try {
            if (RubyActive) {
                if (!rubyMetricsActive) { plainPadding = Padding; plainMargin = Margin; plainLineHeight = LineHeight; rubyMetricsActive = true; }
                double space = FontFamily.LineSpacing * FontSize * RubyFontRatio + 4;
                Padding = new(plainPadding.Left, plainPadding.Top + space, plainPadding.Right, plainPadding.Bottom);
                LineHeight = (double.IsNaN(plainLineHeight) ? FontFamily.LineSpacing * FontSize : plainLineHeight) + space;
                // LineHeight reserves ruby space after the final line as well.
                // Remove only that trailing blank, keeping the body-to-Korean gap unchanged.
                Margin = new(plainMargin.Left, plainMargin.Top, plainMargin.Right, plainMargin.Bottom - space);
            } else if (rubyMetricsActive) { Padding = plainPadding; Margin = plainMargin; LineHeight = plainLineHeight; rubyMetricsActive = false; }
        } finally { updatingRubyMetrics = false; }
    }
    private void InvalidateRubyDrawing() { rubyDrawing = null; rubyLayer?.InvalidateVisual(); }
    internal IReadOnlyList<RubyLabel> RubyLabels() { BuildRubyDrawing(); return rubyDrawing?.Select(p => p.Label).ToArray() ?? []; }
    private void DrawRuby(DrawingContext drawingContext)
    {
        BuildRubyDrawing();
        if (rubyDrawing is not null) foreach (var part in rubyDrawing)
            drawingContext.DrawText(part.Text, part.Label.Bounds.TopLeft);
    }
    private sealed class RubyLayer(DictionarySubtitle owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) { base.OnRender(drawingContext); owner.DrawRuby(drawingContext); }
    }
    private void BuildRubyDrawing()
    {
        if (rubyDrawing is not null || !IsMeasureValid || !IsArrangeValid) return;
        rubyDrawing = [];
        if (!RubyActive || disposed || ActualWidth <= 0) return;
        var candidates = new List<(RubySpan Span, Rect Bounds, FormattedText Text)>();
        foreach (var span in rubySpans) {
            var start = SourcePosition(span.Start); var end = SourcePosition(span.Start + span.Length);
            if (start is null || end is null) continue;
            var first = start.GetCharacterRect(LogicalDirection.Forward); var last = end.GetCharacterRect(LogicalDirection.Backward);
            // Do not invent per-character readings when an analyzer token wraps.
            if (first.IsEmpty || last.IsEmpty || Math.Abs(first.Top - last.Top) > .5) continue;
            var bounds = Rect.Union(first, last);
            double size = FontSize * RubyFontRatio;
            var text = new FormattedText(span.Reading, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), size, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            candidates.Add((span, bounds, text));
        }
        for (int i = 0; i < candidates.Count; i++) {
            var (span, bounds, text) = candidates[i];
            double center = bounds.Left + bounds.Width / 2, left = 0, right = ActualWidth;
            // Allow natural overhang into kana-only space. Fit down only when
            // two adjacent ruby readings would otherwise collide.
            if (i > 0 && Math.Abs(candidates[i - 1].Bounds.Top - bounds.Top) < .5)
                left = (center + candidates[i - 1].Bounds.Left + candidates[i - 1].Bounds.Width / 2) / 2;
            if (i + 1 < candidates.Count && Math.Abs(candidates[i + 1].Bounds.Top - bounds.Top) < .5)
                right = (center + candidates[i + 1].Bounds.Left + candidates[i + 1].Bounds.Width / 2) / 2;
            double size = FontSize * RubyFontRatio, available = Math.Max(0, right - left);
            if (available <= 0) continue;
            if (text.Width > available) { size *= available / text.Width; text.SetFontSize(size); }
            double x = Math.Clamp(center - text.Width / 2, left, Math.Max(left, right - text.Width));
            double y = bounds.Top - text.Height - 1;
            if (y < 0) continue;
            rubyDrawing.Add((new(span.Reading, new(x, y, text.Width, text.Height), bounds, size), text));
        }
    }
    private TextPointer? SourcePosition(int index)
    {
        var pointer = ContentStart;
        while (pointer is not null && pointer.CompareTo(ContentEnd) < 0) {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text) {
                int length = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (index <= length) return pointer.GetPositionAtOffset(index);
                index -= length; pointer = pointer.GetPositionAtOffset(length);
            } else pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return null;
    }
}
