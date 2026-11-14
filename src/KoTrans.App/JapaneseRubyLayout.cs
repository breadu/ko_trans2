using System.Collections.Generic;
using System.Globalization;

namespace KoTrans.App;

// A single bottom-aligned line for kanji-with-ruby and ordinary kana. WPF
// inline element baselines placed ruby words an entire row ABOVE plain text.
internal sealed class JapaneseRubyLinePanel : Panel
{
    internal int ArrangedLineCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var lines = MakeLines(availableSize.Width);
        double height = 0, width = 0;
        foreach (var line in lines)
        {
            width = Math.Max(width, line.Width);
            height += line.Height + 3;
        }
        return new Size(width, Math.Max(0, height - 3));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var lines = MakeLines(finalSize.Width);
        // Explicit breaks have no visual size but still need a completed
        // WPF Arrange pass to keep repeated layout updates stable.
        foreach (UIElement child in InternalChildren)
            if (child is JapaneseRubyBreak) child.Arrange(new Rect(0, 0, 0, 0));
        double y = 0;
        foreach (var line in lines)
        {
            double x = 0;
            foreach (var child in line.Children)
            {
                var size = child.DesiredSize;
                // The ruby and plain-text BASE GLYPHS share the same bottom.
                child.Arrange(new Rect(x, y + line.Height - size.Height,
                    size.Width, size.Height));
                x += size.Width;
            }
            y += line.Height + 3;
        }
        ArrangedLineCount = lines.Count;
        return finalSize;
    }

    private List<RubyLine> MakeLines(double width)
    {
        double limit = double.IsFinite(width) ? Math.Max(1, width) : double.PositiveInfinity;
        var result = new List<RubyLine>();
        var line = new RubyLine();
        foreach (UIElement child in InternalChildren)
        {
            if (child is JapaneseRubyBreak)
            {
                result.Add(line);
                line = new RubyLine();
                continue;
            }
            var size = child.DesiredSize;
            if (line.Children.Count > 0 && line.Width + size.Width > limit)
            {
                result.Add(line);
                line = new RubyLine();
            }
            line.Children.Add(child);
            line.Width += size.Width;
            line.Height = Math.Max(line.Height, size.Height);
        }
        if (line.Children.Count > 0 || result.Count == 0) result.Add(line);
        return result;
    }

    private sealed class RubyLine
    {
        internal readonly List<UIElement> Children = [];
        internal double Width;
        internal double Height = 32;
    }
}

internal sealed class JapaneseRubyBreak : FrameworkElement
{
    protected override Size MeasureOverride(Size availableSize) => new(0, 0);
}

// Japanese morphological reading tokens already contain accurate source spans.
// Never infer a missing per-kanji reading or modify the original OCR text.
internal static class JapaneseRubyLayout
{
    internal static bool TryRender(JapaneseRubyLinePanel host, string source,
        IReadOnlyList<ReadingToken> tokens, double baseFontSize,
        double rubyFontSize, Brush baseBrush, Brush rubyBrush)
    {
        if (tokens.Count == 0 || source.Length == 0) return false;
        int offset = 0, rubyCount = 0;
        var fragments = new List<UIElement>();
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token.Surface)) continue;
            int index = source.IndexOf(token.Surface, offset, StringComparison.Ordinal);
            if (index < offset) return false;
            if (index > offset)
                AddPlain(fragments, source[offset..index], baseFontSize, baseBrush);
            if (token.HasKanji && CanShowRuby(token.Surface, token.Hiragana))
            {
                fragments.Add(CreateRubyWord(token.Surface, token.Hiragana!,
                    baseFontSize, rubyFontSize, baseBrush, rubyBrush));
                rubyCount++;
            }
            else AddPlain(fragments, token.Surface, baseFontSize, baseBrush);
            offset = index + token.Surface.Length;
        }
        if (rubyCount == 0) return false;
        if (offset < source.Length)
            AddPlain(fragments, source[offset..], baseFontSize, baseBrush);

        // Validate all input before replacing the currently displayed result.
        host.Children.Clear();
        foreach (var item in fragments) host.Children.Add(item);
        return true;
    }

    private static void AddPlain(List<UIElement> children, string text,
        double fontSize, Brush color)
    {
        var iterator = StringInfo.GetTextElementEnumerator(text);
        while (iterator.MoveNext())
        {
            var element = (string)iterator.Current!;
            if (element.Contains('\n'))
            {
                children.Add(new JapaneseRubyBreak());
                continue;
            }
            if (element == "\r") continue;
            children.Add(new TextBlock
            {
                Name = "JapaneseRubyPlain", Text = element,
                FontFamily = new FontFamily("Yu Gothic UI"),
                FontSize = fontSize, Foreground = color,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }
    }

    internal static bool CanShowRuby(string surface, string? kana)
    {
        if (string.IsNullOrWhiteSpace(surface) || string.IsNullOrWhiteSpace(kana)
            || !surface.Any(IsKanji)) return false;
        return kana.All(c => c is >= 'ぁ' and <= 'ゖ'
            or >= 'ァ' and <= 'ヺ' or 'ー' or '・' or ' ');
    }

    internal static FrameworkElement CreateRubyWord(string word, string kana,
        double baseFontSize, double rubyFontSize, Brush baseBrush, Brush rubyBrush)
    {
        var pair = new StackPanel
        {
            Name = "JapaneseRubyPair", Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        pair.Children.Add(new TextBlock
        {
            Name = "JapaneseRubyKana", Text = kana,
            FontFamily = new FontFamily("Yu Gothic UI"), FontSize = rubyFontSize,
            Foreground = rubyBrush, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 0, 0, 1)
        });
        pair.Children.Add(new TextBlock
        {
            Name = "JapaneseRubyBase", Text = word,
            FontFamily = new FontFamily("Yu Gothic UI"), FontSize = baseFontSize,
            Foreground = baseBrush, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        });
        return pair;
    }

    private static bool IsKanji(char c) =>
        c is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';
}
