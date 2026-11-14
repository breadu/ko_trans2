using System.Runtime.InteropServices;
using System.Windows.Interop;
using KoTrans.App;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

// Explicit Desktop suite: real hidden HWNDs and a unique test-only broadcast.
// No shown window, game input, real Magpie message, capture, API or user settings.
internal static class MagpieNotificationDesktopTests
{
    internal static void Run()
    {
        int id = (int)RegisterWindowMessage("KoTrans.MagpieReceiver.Test." + Guid.NewGuid().ToString("N"));
        Check(id is >= 0xC000 and <= 0xFFFF, "Test notification registration failed");
        var ownerParameters = MagpieNotificationReceiver.Parameters(); ownerParameters.WindowName = "Synthetic hidden owner";
        using var owner = new HwndSource(ownerParameters);
        var oldParameters = MagpieNotificationReceiver.Parameters();
        oldParameters.WindowName = "Synthetic owned subtitle"; oldParameters.ParentWindow = owner.Handle;
        oldParameters.WindowStyle = 0x06080000;
        using var legacy = new HwndSource(oldParameters);
        SetWindowLongPtr(legacy.Handle, -16, new IntPtr(0x06080000));
        int oldReceived = 0, received = 0;
        legacy.AddHook((nint h, int m, nint w, nint l, ref bool handled) => { if (m == id) oldReceived++; return 0; });
        Check(GetWindow(legacy.Handle, 4) == owner.Handle, "Legacy reproduction did not preserve an owned subtitle HWND");
        for (int cycle = 0; cycle < 3; cycle++) {
            using var receiver = new MagpieNotificationReceiver(id, (m, w, l) => {
                Check(m == id && w == 1 && l == 42, "Broadcast payload was not forwarded intact"); received++;
            });
            nint handle = receiver.Handle;
            Check(handle != 0 && GetWindow(handle, 4) == 0 && !IsWindowVisible(handle), "Receiver is owned or visible");
            Check((GetWindowLongPtr(handle, -20).ToInt64() & 0x08000080) == 0x08000080, "Receiver could activate or appear in task switcher");
            Check(PostMessage(new IntPtr(0xffff), (uint)id, 1, 42), "Test broadcast failed");
            int expected = cycle + 1;
            Until(() => received == expected, "Unowned receiver missed HWND_BROADCAST");
            receiver.Dispose(); receiver.Dispose();
            Check(receiver.Handle == 0 && !IsWindow(handle), "Receiver HWND survived disposal");
            Check(PostMessage(new IntPtr(0xffff), (uint)id, 1, 42), "Post-dispose test broadcast failed"); Pump();
            Check(received == expected, "Disposed receiver invoked callback");
        }
        Console.WriteLine($"Magpie native broadcast: independent receiver={received}/3; legacy owned receiver={oldReceived}. No visible windows, game input or focus changes requested.");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint h, uint message, nint w, nint l);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint h, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
}
