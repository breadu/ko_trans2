using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// A synchronous foreign-window renderer can hang. At most one background render is
// outstanding; cancellation/timeout detaches the caller, never disposes the renderer's DC.
// A detached result is never reused as a later capture, and cannot publish subtitles.
public sealed class WindowPrintCapture
{
    private readonly object sync=new();
    private readonly Func<WindowTarget,PixelRect?,ImageFrame> render;
    private readonly TimeSpan timeout;
    private Task<ImageFrame>? pending;
    public WindowPrintCapture(Func<WindowTarget,PixelRect?,ImageFrame>? render=null,TimeSpan? timeout=null)
    {this.render=render ?? Render;this.timeout=timeout ?? TimeSpan.FromSeconds(1);}
    public async Task<ImageFrame> CaptureAsync(WindowTarget target,PixelRect? relative,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();Task<ImageFrame> task;
        lock(sync) {
            if(pending is {IsCompleted:false}) throw Busy();
            task=pending=Task.Run(()=>render(target,relative));
            _=task.ContinueWith(t=>{
                if(t.IsFaulted)_=t.Exception;
                lock(sync) {if(ReferenceEquals(pending,t))pending=null;}
            },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        try {return await task.WaitAsync(timeout,token).ConfigureAwait(false);}
        catch(TimeoutException) {throw Busy();}
    }
    private static CaptureUnavailableException Busy()=>new("원본 게임 창의 캡처 응답을 기다리고 있습니다.",CaptureWaitReason.WindowBusy);
    private static ImageFrame Render(WindowTarget target,PixelRect? relative)
    {
        var client=WindowTargets.Resolve(target,false); // Exact HWND/PID/start/class validation; no desktop pixels.
        var area=WindowCaptureBounds.Resolve(new(0,0,client.Width,client.Height),relative);
        if(IsHungAppWindow(target.Handle)) throw Busy();
        using var bitmap=new Bitmap(client.Width,client.Height,PixelFormat.Format32bppRgb);
        using(var graphics=Graphics.FromImage(bitmap)) {
            var dc=graphics.GetHdc();
            try {if(!PrintWindow(target.Handle,dc,3)) throw new CaptureUnavailableException("게임 창의 원본 화면을 읽을 수 없습니다.");}
            finally {graphics.ReleaseHdc(dc);}
        }
        var after=WindowTargets.Resolve(target,false);
        if(after.Width!=client.Width || after.Height!=client.Height)
            throw new CaptureUnavailableException("게임 창 크기가 바뀌었습니다. 다음 프레임을 확인합니다.",CaptureWaitReason.ScalingTransition);
        int stride=checked(area.Width*4);var pixels=new byte[checked(stride*area.Height)];
        var bits=bitmap.LockBits(new Rectangle(area.X,area.Y,area.Width,area.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);
        try {for(int y=0;y<area.Height;y++) Marshal.Copy(bits.Scan0+y*bits.Stride,pixels,y*stride,stride);}
        finally {bitmap.UnlockBits(bits);}
        return new(area.Width,area.Height,stride,pixels);
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
}

public static class LosslessCaptureDiscovery
{
    // Detection only selects original-window capture; output geometry is never inferred.
    public static bool IsCovering(WindowTarget target)
    {
        var output=FindWindow("LosslessScaling",null);
        if(output==IntPtr.Zero || !IsWindowVisible(output) || IsIconic(output) || !WindowTargets.IsAbove(output,target.Handle))return false;
        GetWindowThreadProcessId(output,out uint pid);
        try {using var process=Process.GetProcessById((int)pid);if(!process.ProcessName.Equals("LosslessScaling",StringComparison.OrdinalIgnoreCase))return false;}
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {return false;}
        return WindowTargets.Bounds(output) is {} surface && WindowTargets.Bounds(target.Handle) is {} source && WindowTargets.Overlaps(surface,source);
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string name,string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
}
