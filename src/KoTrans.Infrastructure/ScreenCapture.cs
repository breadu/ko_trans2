using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public static class ScreenCapture
{
    public static ImageFrame Capture(WindowCapturePlan plan, CancellationToken token = default)
    {
        var image=Capture(plan.ScreenArea,token);
        return Normalize(image,plan.OutputWidth,plan.OutputHeight,token);
    }
    // Only a mapped presentation is resized. Ordinary window/region capture keeps its existing pixels.
    public static ImageFrame Normalize(ImageFrame input,int width,int height,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(!new PixelRect(0,0,width,height).IsValid) throw new ArgumentException("OCR 입력 크기가 올바르지 않습니다.");
        if(input.Width==width && input.Height==height) return input;
        int stride=checked(width*4);var bytes=new byte[checked(stride*height)];var source=input.Pixels.Span;
        double sx=input.Width/(double)width,sy=input.Height/(double)height;
        for(int y=0;y<height;y++) {
            token.ThrowIfCancellationRequested();
            double fy=Math.Clamp((y+.5)*sy-.5,0,input.Height-1d);int y0=(int)fy,y1=Math.Min(y0+1,input.Height-1);fy-=y0;
            for(int x=0;x<width;x++) {
                double fx=Math.Clamp((x+.5)*sx-.5,0,input.Width-1d);int x0=(int)fx,x1=Math.Min(x0+1,input.Width-1);fx-=x0;
                int a=y0*input.Stride+x0*4,b=y0*input.Stride+x1*4,c=y1*input.Stride+x0*4,d=y1*input.Stride+x1*4,p=y*stride+x*4;
                for(int channel=0;channel<4;channel++) {
                    double upper=source[a+channel]*(1-fx)+source[b+channel]*fx,lower=source[c+channel]*(1-fx)+source[d+channel]*fx;
                    bytes[p+channel]=(byte)Math.Clamp((int)Math.Round(upper*(1-fy)+lower*fy),0,255);
                }
            }
        }
        return new(width,height,stride,bytes,input.PixelFormat);
    }
    public static ImageFrame Capture(PixelRect area, CancellationToken token = default)
    {
        if (!area.IsValid) throw new ArgumentException("캡처 영역 크기가 올바르지 않습니다.");
        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (!desktop.Contains(new Rectangle(area.X, area.Y, area.Width, area.Height)))
            throw new ArgumentException("캡처 영역이 현재 모니터 범위를 벗어났습니다. 영역을 다시 지정하세요.");
        token.ThrowIfCancellationRequested();
        using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(area.X, area.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        token.ThrowIfCancellationRequested();
        var stride = checked(area.Width * 4);
        var bytes = new byte[checked(stride * area.Height)];
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < area.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), bytes, y * stride, stride);
            }
        }
        finally { bitmap.UnlockBits(bits); }
        return new ImageFrame(area.Width, area.Height, stride, bytes);
    }
}
