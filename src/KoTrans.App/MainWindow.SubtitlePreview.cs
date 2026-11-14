namespace KoTrans.App;

public partial class MainWindow
{
    private void RefreshSubtitlePreview()
    {
        if (!ready || SubtitlePreviewSurface is null) return;
        var p = Current;
        SubtitlePreviewSurface.Opacity = p.OverlayOpacity;
        SubtitlePreviewOriginal.Text = p.Language == "일본어" ? "これは日本語のプレビューです。" : "This is a preview of the original text.";
        SubtitlePreviewOriginal.FontSize = p.OverlayOriginalFontSize;
        SubtitlePreviewOriginal.Foreground = (Brush)new BrushConverter().ConvertFromString(p.OverlayOriginalColor)!;
        SubtitlePreviewOriginal.Visibility = p.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        SubtitlePreviewTranslation.FontSize = p.OverlayFontSize;
        SubtitlePreviewTranslation.Foreground = (Brush)new BrushConverter().ConvertFromString(p.OverlayColor)!;
        SubtitlePreviewTranslation.Visibility = p.ShowTranslation ? Visibility.Visible : Visibility.Collapsed;
    }
}
