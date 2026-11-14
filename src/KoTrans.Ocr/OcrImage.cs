using System.Runtime.InteropServices;
using KoTrans.Core;
using OpenCvSharp;

namespace KoTrans.Ocr;

internal static class OcrImage
{
    internal static Mat ToBgr(ImageFrame frame)
    {
        using var pin = frame.Pixels.Pin();
        unsafe {
            using var input = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, (IntPtr)pin.Pointer, frame.Stride);
            var output = new Mat();
            try { Cv2.CvtColor(input, output, ColorConversionCodes.BGRA2BGR); return output; }
            catch { output.Dispose(); throw; }
        }
    }
}
