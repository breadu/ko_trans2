using System.Collections.Generic;
using System.Windows.Documents;
using System.Windows.Input;
using KoTrans.Infrastructure;

namespace KoTrans.App;

// Native word runs: source text only. No browser, analyzer or network is started by hover.
internal sealed partial class DictionarySubtitle : TextBlock
{
    private readonly WordDictionaryTokenizer tokenizer = new();
    private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<DictionaryWord>>> analyze;
    private readonly List<(DictionaryWord Word, Run Run)> runs = [];
    private CancellationTokenSource? cancellation;
    private Task cleanup = Task.CompletedTask;
    private string source = "", language = "en", attemptedText = "", attemptedLanguage = "";
    private int revision;
    private bool disposed;
    private bool backgroundDirty, ownsBackground;
    private Brush? idleBackground;
    private (DictionaryWord Word, int Revision, Point Point)? pressed;
    internal bool LookupEnabled { get; private set; }
    internal DictionaryWord? HoveredWord { get; private set; }
    internal IReadOnlyList<DictionaryWord> Words => runs.Select(r => r.Word).ToArray();
    internal event Action<string>? WordRequested;
    internal event Action<string>? AnalysisFailed;
    internal DictionarySubtitle(Func<string, string, CancellationToken, Task<IReadOnlyList<DictionaryWord>>>? analyze = null,
        Func<string, CancellationToken, Task<JapaneseReading>>? readJapanese = null)
    {
        this.analyze = analyze ?? ((text, lang, token) => Task.Run(() => tokenizer.Tokenize(text, lang, token), token));
        this.readJapanese = readJapanese ?? ((text, token) => Task.Run(() => ReadRubyLocally(text, token), token));
        SizeChanged += (_, _) => InvalidateRubyDrawing();
        // A WPF tooltip is a separate popup HWND, outside the overlay's capture exclusion.
        // Word hover must stay entirely inside the excluded text window.
        ToolTipService.SetIsEnabled(this, false);
        MouseLeave += (_, _) => ResetInteraction();
        LostMouseCapture += (_, _) => ResetInteraction();
        SizeChanged += (_, _) => ResetInteraction();
        IsVisibleChanged += (_, _) => ResetInteraction();
        LayoutUpdated += (_, _) => { if (backgroundDirty && IsMeasureValid && IsArrangeValid) UpdateHoverBackground(); };
    }
    internal void SetSource(string text, string lang)
    {
        if (source == text && language == lang) return;
        source = text; language = lang; revision++; Cancel(); ResetInteraction();
        runs.Clear(); Text = text; attemptedText = ""; attemptedLanguage = "";
        RubySourceChanged();
        StartAnalysis();
    }
    internal void SetLookupEnabled(bool enabled)
    {
        if (LookupEnabled == enabled) return;
        LookupEnabled = enabled; revision++; ResetInteraction();
        if (!enabled) { Cancel(); if (runs.Count == 0) attemptedLanguage = ""; }
        foreach (var pair in runs) pair.Run.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
        if (enabled) StartAnalysis();
    }
    private void Cancel() { cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; }
    private void StartAnalysis()
    {
        if (disposed || !LookupEnabled || source.Length == 0 || attemptedText == source && attemptedLanguage == language) return;
        Cancel(); cancellation = new();
        attemptedText = source; attemptedLanguage = language;
        _ = AnalyzeAsync(source, language, revision, cancellation.Token);
    }
    private async Task AnalyzeAsync(string text, string lang, int version, CancellationToken token)
    {
        try {
            var words = await analyze(text, lang, token);
            if (disposed || token.IsCancellationRequested || version != revision || !LookupEnabled) return;
            int end = 0;
            foreach (var word in words) {
                if (word.Start < end || word.Length <= 0 || word.Start > text.Length - word.Length
                    || text.Substring(word.Start, word.Length) != word.Surface)
                    throw new InvalidDataException("단어 위치가 원문과 일치하지 않습니다.");
                end = word.Start + word.Length;
            }
            Inlines.Clear(); runs.Clear(); end = 0;
            foreach (var word in words) {
                Inlines.Add(new Run(text[end..word.Start]));
                var run = new Run(word.Surface) { Cursor = Cursors.Hand };
                runs.Add((word, run)); Inlines.Add(run); end = word.Start + word.Length;
                run.MouseEnter += (_, _) => Hover(word.Start);
                run.MouseLeave += (_, _) => ResetInteraction();
                run.MouseLeftButtonDown += (_, e) => { if (Press(word.Start, e.GetPosition(this))) e.Handled = true; };
                run.MouseLeftButtonUp += (_, e) => { if (Release(word.Start, e.GetPosition(this))) e.Handled = true; };
            }
            Inlines.Add(new Run(text[end..]));
            InvalidateRubyDrawing();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) {
            if (!disposed && version == revision && !token.IsCancellationRequested)
                AnalysisFailed?.Invoke(AnalysisFailureMessage("단어 정보를 표시하지 못했습니다.", ex));
        }
    }
    private static string AnalysisFailureMessage(string operation, Exception error) => operation + " " + (error switch {
        KoTrans.Core.TranslationException or InvalidDataException => error.Message,
        FileNotFoundException or DirectoryNotFoundException => "단어 분석에 필요한 파일을 찾을 수 없습니다. 프로그램의 사전 파일을 확인하세요.",
        UnauthorizedAccessException => "단어 분석 파일에 접근할 권한을 확인하세요.",
        _ => "원문은 계속 사용할 수 있습니다. 기능을 껐다 켜거나 다시 시작해 주세요."
    });
    internal void Hover(int position)
    {
        var target = LookupEnabled ? runs.Select(r => r.Word).FirstOrDefault(w => position >= w.Start && position < w.Start + w.Length) : null;
        if (HoveredWord == target) return;
        HoveredWord = target;
        foreach (var pair in runs) {
            pair.Run.ClearValue(TextElement.BackgroundProperty);
            if (pair.Word == HoveredWord) pair.Run.Foreground = Brushes.MidnightBlue;
            else pair.Run.ClearValue(TextElement.ForegroundProperty);
        }
        backgroundDirty = true;
        if (target is null || IsMeasureValid && IsArrangeValid) UpdateHoverBackground();
    }
    private void UpdateHoverBackground()
    {
        // Run backgrounds are painted per glyph in WPF, leaving hairline seams for Japanese
        // fallback fonts/fractional DPI. Paint a single rectangle per visual line instead.
        if (HoveredWord is not { } word) {
            backgroundDirty = false;
            if (ownsBackground) { SetCurrentValue(BackgroundProperty, idleBackground); ownsBackground = false; idleBackground = null; }
            return;
        }
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        if (!ownsBackground) { idleBackground = Background; ownsBackground = true; }
        var bounds = new Rect(RenderSize);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open()) {
            dc.DrawRectangle(idleBackground?.CloneCurrentValue() ?? Brushes.Transparent, null, bounds);
            var rectangles = runs.Where(pair => pair.Word == word).SelectMany(pair => ((IContentHost)this).GetRectangles(pair.Run))
                .Where(rect => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0).OrderBy(rect => rect.Top).ThenBy(rect => rect.Left);
            var line = Rect.Empty;
            foreach (var rect in rectangles) {
                if (!line.IsEmpty && Math.Abs(line.Top - rect.Top) < .01 && Math.Abs(line.Bottom - rect.Bottom) < .01) line.Union(rect);
                else { if (!line.IsEmpty) dc.DrawRectangle(Brushes.PaleGoldenrod, null, line); line = rect; }
            }
            if (!line.IsEmpty) dc.DrawRectangle(Brushes.PaleGoldenrod, null, line);
        }
        var brush = new DrawingBrush(drawing) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds, Stretch = Stretch.Fill };
        brush.Freeze(); backgroundDirty = false; SetCurrentValue(BackgroundProperty, brush);
    }
    internal bool Press(int position, Point point)
    {
        Hover(position);
        pressed = HoveredWord is { } word ? (word, revision, point) : null;
        return pressed is not null;
    }
    internal bool Release(int position, Point point)
    {
        var before = pressed; pressed = null; Hover(position);
        if (before is not { } down || down.Revision != revision || down.Word != HoveredWord
            || Math.Abs(down.Point.X - point.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(down.Point.Y - point.Y) > SystemParameters.MinimumVerticalDragDistance) return false;
        WordRequested?.Invoke(down.Word.SearchText); return true;
    }
    private void ResetInteraction() { pressed = null; Hover(-1); }
    internal Task DisposeAsync()
    {
        if (disposed) return cleanup;
        disposed = true; revision++; Cancel(); ResetInteraction();
        rubyRevision++; rubyCancellation?.Cancel(); rubyCancellation?.Dispose(); rubyCancellation = null;
        rubySpans.Clear(); InvalidateRubyDrawing();
        // Disposal waits on the tokenizer's native lock, not on a continuation on a closing Dispatcher.
        return cleanup = Task.Run(() => { tokenizer.Dispose(); rubyReader.Dispose(); });
    }
}
