using System.Runtime.InteropServices;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public enum WindowScaleProvider { Magpie }
public sealed record WindowScaleTransform(WindowScaleProvider Provider, IntPtr SourceWindow, IntPtr OutputWindow,
    PixelRect Source, PixelRect Destination)
{
    public PixelRect ToDisplay(PixelRect area) => Map(area, Source, Destination);
    public PixelRect ToSource(PixelRect area) => Map(area, Destination, Source);
    private static PixelRect Map(PixelRect area, PixelRect from, PixelRect to)
    {
        if (!area.IsValid || !from.IsValid || !to.IsValid) throw Invalid();
        // At most two source pixels tolerate a scaler's omitted native client border.
        long left=Math.Max(area.X,from.X),top=Math.Max(area.Y,from.Y);
        long right=Math.Min((long)area.X+area.Width,(long)from.X+from.Width),bottom=Math.Min((long)area.Y+area.Height,(long)from.Y+from.Height);
        if(left>=right || top>=bottom || left-area.X>2 || top-area.Y>2
            || (long)area.X+area.Width-right>2 || (long)area.Y+area.Height-bottom>2) throw Invalid();
        long x=to.X+(long)Math.Floor((left-from.X)*to.Width/(double)from.Width);
        long y=to.Y+(long)Math.Floor((top-from.Y)*to.Height/(double)from.Height);
        long r=to.X+(long)Math.Ceiling((right-from.X)*to.Width/(double)from.Width);
        long b=to.Y+(long)Math.Ceiling((bottom-from.Y)*to.Height/(double)from.Height);
        if(x<int.MinValue || y<int.MinValue || r>int.MaxValue || b>int.MaxValue || r-x>int.MaxValue || b-y>int.MaxValue) throw Invalid();
        var result=new PixelRect((int)x,(int)y,(int)(r-x),(int)(b-y));
        return result.IsValid ? result : throw Invalid();
    }
    private static CaptureUnavailableException Invalid() => new("확대 화면에 표시되는 게임 내부에 대사 영역을 지정하세요. 기존 영역은 보존했습니다.");
}
public sealed record WindowCapturePlan(PixelRect ScreenArea, int OutputWidth, int OutputHeight, WindowScaleTransform? Scale)
{
    public void ValidateCompletion(ImageFrame frame, PixelRect initialClient, PixelRect currentClient, WindowScaleTransform? currentScale)
    {
        // GDI reads a fixed screen rectangle. A move/resize/scale transition while
        // that read was in flight can return unrelated pixels with the expected size.
        if (initialClient != currentClient || frame.Width != OutputWidth || frame.Height != OutputHeight || Scale != currentScale)
            throw new CaptureUnavailableException("게임 화면의 위치나 크기가 바뀌었습니다. 다음 프레임을 확인합니다.", CaptureWaitReason.ScalingTransition);
    }
    public static WindowCapturePlan Create(IntPtr source, PixelRect window, PixelRect? relative, WindowScaleTransform? scale,bool originalPixels=false)
    {
        var original=WindowCaptureBounds.Resolve(window,relative);
        // No matching live presenter means the exact existing GDI rectangle and dimensions.
        if(scale is null || scale.SourceWindow!=source) return new(original,original.Width,original.Height,null);
        return new(originalPixels ? original : scale.ToDisplay(original),original.Width,original.Height,scale);
    }
}
public static class WindowScaleDiscovery
{
    // Magpie's published third-party window-property contract, v0.12.1 ScalingWindow.cpp.
    private const string MagpieClass="Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";
    public static WindowScaleTransform? Find(WindowTarget target)
    {
        var output=FindWindow(MagpieClass,null);
        if(output==IntPtr.Zero || !IsWindowVisible(output) || IsIconic(output)
            || GetProp(output,"Magpie.SrcHWND")!=target.Handle || !WindowTargets.IsAbove(output,target.Handle)) return null;
        var first=Read(target.Handle,output);
        // Properties are updated separately while moving/resizing. Never use a mixed snapshot.
        if(first!=Read(target.Handle,output) || GetProp(output,"Magpie.SrcHWND")!=target.Handle
            || !first.Source.IsValid || !first.Destination.IsValid)
            throw new CaptureUnavailableException("확대 화면의 위치와 크기를 확인하는 중입니다.",CaptureWaitReason.ScalingTransition);
        return first;
    }
    private static WindowScaleTransform Read(IntPtr source,IntPtr output)
        => new(WindowScaleProvider.Magpie,source,output,Rect(output,"Src"),Rect(output,"Dest"));
    private static PixelRect Rect(IntPtr h,string prefix)
    {
        long l=GetProp(h,"Magpie."+prefix+"Left").ToInt64(),t=GetProp(h,"Magpie."+prefix+"Top").ToInt64();
        long r=GetProp(h,"Magpie."+prefix+"Right").ToInt64(),b=GetProp(h,"Magpie."+prefix+"Bottom").ToInt64();
        if(l<int.MinValue || l>int.MaxValue || t<int.MinValue || t>int.MaxValue || r-l>int.MaxValue || b-t>int.MaxValue
            || r<=l || b<=t) return default;
        return new((int)l,(int)t,(int)(r-l),(int)(b-t));
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string name,string? title);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr h,string name);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
}
