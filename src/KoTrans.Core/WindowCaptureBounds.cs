namespace KoTrans.Core;

public static class WindowCaptureBounds
{
    public static PixelRect? FromScreen(PixelRect window, PixelRect selection)
        => selection.IsValid && selection.X >= window.X && selection.Y >= window.Y
            && (long)selection.X + selection.Width <= (long)window.X + window.Width
            && (long)selection.Y + selection.Height <= (long)window.Y + window.Height
            ? selection with { X = selection.X - window.X, Y = selection.Y - window.Y } : null;
    public static PixelRect Resolve(PixelRect window, PixelRect? relative)
    {
        if (relative is not { } crop) return window;
        if (!crop.IsValid || crop.X < 0 || crop.Y < 0 || (long)crop.X + crop.Width > window.Width || (long)crop.Y + crop.Height > window.Height)
            throw new InvalidDataException("대상 창 크기가 바뀌었습니다. 대사 영역을 다시 지정하세요.");
        return crop with { X = checked(window.X + crop.X), Y = checked(window.Y + crop.Y) };
    }
}
