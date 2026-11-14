namespace KoTrans.App;

// The first child keeps the existing wrapping action group. The second
// child owns the trailing edge, with a separate row when it cannot fit.
internal sealed class LauncherActionPanel : Panel
{
    private bool stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        var primary = InternalChildren[0];
        var help = InternalChildren[1];
        primary.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        help.Measure(availableSize);
        double combinedWidth = primary.DesiredSize.Width + help.DesiredSize.Width;
        stacked = combinedWidth > availableSize.Width;
        if (stacked)
            primary.Measure(new Size(availableSize.Width, availableSize.Height));

        return stacked
            ? new Size(Math.Max(primary.DesiredSize.Width, help.DesiredSize.Width),
                primary.DesiredSize.Height + help.DesiredSize.Height)
            : new Size(combinedWidth, Math.Max(primary.DesiredSize.Height, help.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var primary = InternalChildren[0];
        var help = InternalChildren[1];
        var helpSize = help.DesiredSize;
        primary.Arrange(new Rect(0, 0, stacked ? finalSize.Width : primary.DesiredSize.Width,
            primary.DesiredSize.Height));
        help.Arrange(new Rect(Math.Max(0, finalSize.Width - helpSize.Width),
            stacked ? primary.DesiredSize.Height : 0, Math.Min(helpSize.Width, finalSize.Width), helpSize.Height));
        return finalSize;
    }
}
