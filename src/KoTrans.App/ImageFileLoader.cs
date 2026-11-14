using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KoTrans.Core;
namespace KoTrans.App;
internal static class ImageFileLoader
{
    internal static Task<ImageFrame> LoadAsync(string path) => Task.Run(() => {
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("이미지 파일은 64MiB 이하로 선택하세요.");
        var source = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if (!new PixelRect(0, 0, source.PixelWidth, source.PixelHeight).IsValid) throw new InvalidDataException("이미지가 너무 큽니다.");
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        var bytes = new byte[checked(stride * converted.PixelHeight)]; converted.CopyPixels(bytes, stride, 0);
        return new ImageFrame(converted.PixelWidth, converted.PixelHeight, stride, bytes);
    });
}
