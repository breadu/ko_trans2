using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class GamepadCaptureTests
{
    internal static void CaptureAndMapping()
    {
        var editor = new GamepadTriggerEditor(); var host = Track(new Window { Content = editor }); int changes = 0;
        editor.ValueChanged += (_, _) => changes++;
        try {
            Check(new Profile().TriggerDelayMs == 0 && new TriggerOptions().GamepadButtons == "Joy1,Joy2", "Delay/A/B defaults changed incorrectly");
            editor.BeginCapture(); Check(editor.IsCapturing && !editor.IsPolling, "Background capture started a native device timer");
            Check(!editor.ObserveState(0, 0x4000) && !editor.ObserveState(0, 0x4000) && changes == 0,
                "A button already held before capture was registered");
            editor.ObserveState(0, 0); Check(editor.ObserveState(0, 0x4000) && editor.Value == "Joy1,Joy2,Joy3" && !editor.IsCapturing,
                "Fresh button edge did not add X and stop capture");
            editor.BeginCapture(); editor.ObserveState(0, 0);
            Check(!editor.ObserveState(0, 0x1000) && editor.IsCapturing && changes == 1, "Duplicate A was added or canceled capture");
            editor.ObserveState(0, 0); Check(!editor.ObserveState(0, 0xC000) && editor.IsCapturing, "Simultaneous X/Y press became an unintended chord");
            editor.ObserveState(0, null); Check(!editor.ObserveState(0, 0x8000), "Reconnect with held Y generated a fresh press");
            editor.ObserveState(0, 0); Check(editor.ObserveState(0, 0x8000) && editor.Value.EndsWith("Joy4"), "Capture did not recover after disconnect/release");
            editor.BeginCapture(); editor.ObserveState(3, 0);
            Check(editor.ObserveState(3, 0x200) && editor.Value.EndsWith("RightShoulder"), "A different controller slot could not add RB");
            Check(!editor.IsPolling && !editor.IsCapturing, "Successful registration leaked polling");
            editor.BeginCapture(); Click(Elements<Button>(editor).Single(b => b.Name == "AddGamepadButton"));
            Check(!editor.IsCapturing && !editor.IsPolling, "Cancel button left registration running");
            editor.BeginCapture(); editor.IsEnabled = false;
            Check(!editor.IsCapturing && !editor.IsPolling, "Disabling trigger fields left capture running"); editor.IsEnabled = true;
            editor.BeginCapture(); editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Check(!editor.IsCapturing && !editor.IsPolling, "Leaving the editor did not stop capture");
            int before = changes; editor.Value = "Joy2,Joy1,Joy9";
            Check(changes == before && editor.Value == "Joy2,Joy1,Joy9", "Loading legacy values silently changed or dropped configuration");
            editor.RemoveButton("Joy9"); Check(editor.Value == "Joy2,Joy1", "User could not remove an unsupported legacy token");
            editor.Value = "";
            foreach (var button in GamepadInput.Buttons) { editor.BeginCapture(); Check(editor.CaptureButton(button.Mask), "Supported gamepad button rejected: " + button.Label); }
            var parsed = DesktopInput.ParseTriggers(new TriggerOptions { Keys = "", GamepadButtons = editor.Value });
            Check(parsed.Mask == 0x3F3FF && editor.Value.Length <= 200, "Captured full button set did not map to live trigger bits");
            Check(GamepadInput.Decode(0xFFFF, 30, 30) == 0xF3FF && GamepadInput.Decode(0, 31, 31) == 0x30000,
                "Reserved button bits or analog trigger threshold decoded incorrectly");
            Check(GamepadInput.Rising(0x1000, 0x1000) == 0 && GamepadInput.Rising(0x1000, 0x3000) == 0x2000,
                "Live trigger edge decoding repeated held buttons");
            Check(DesktopInput.ParseTriggers(new TriggerOptions { Keys = "", GamepadButtons = "joy1,Joy2,LeftTrigger,RightTrigger" }).Mask == 0x33000,
                "Legacy case-insensitive face buttons or LT/RT mapping changed");
        } finally { editor.CancelCapture(); Close(host); }
    }
}
