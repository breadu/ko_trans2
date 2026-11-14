using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using KoTrans.App;
using KoTrans.Core;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class DesktopDrag
{
    internal static void Run()
    {
        var window = Track(new TranslationWindow(new Profile { OverlayClickThrough = true }));
        var oldFocus = GetForegroundWindow(); GetCursorPos(out var oldCursor);
        try
        {
            window.Show(); Pump(); window.SetClickThrough(true); Layout(window,720,280);
            var handle=Elements<Thumb>(window.Chrome).Single(x=>x.Name=="OverlayMoveHandle");
            var point=handle.PointToScreen(new Point(handle.ActualWidth/2,handle.ActualHeight/2));
            if(!SetForegroundWindow(new WindowInteropHelper(window).Handle) || !SetCursorPos((int)point.X,(int)point.Y))
                throw new DesktopEnvironmentException("Cannot target the overlay input surface. Win32="+Marshal.GetLastWin32Error());
            Pump(); var left=window.Left;var top=window.Top;
            var drag=Task.Run(async()=>{
                try{mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(150);SetCursorPos((int)point.X+60,(int)point.Y+35);await Task.Delay(150);}
                finally{mouse_event(4,0,0,0,UIntPtr.Zero);}
            });
            Complete(drag);Pump();
            Check(Math.Abs(window.Left-left)>10 && Math.Abs(window.Top-top)>5,"Physical drag did not move overlay");
            Console.WriteLine($"Physical overlay drag: moved {window.Left-left:F0}, {window.Top-top:F0} DIP. No game input, capture or API.");
        }
        finally{mouse_event(4,0,0,0,UIntPtr.Zero);Close(window);SetCursorPos(oldCursor.X,oldCursor.Y);SetForegroundWindow(oldFocus);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct CursorPoint{public int X,Y;}
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetCursorPos(out CursorPoint p);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
