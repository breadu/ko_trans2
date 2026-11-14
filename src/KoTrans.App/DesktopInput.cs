using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed class DesktopInput : IDisposable
{
    private readonly IntPtr hwnd;
    private readonly HwndSource source;
    private readonly Hook keyboardCallback, mouseCallback;
    private IntPtr keyboardHook, mouseHook;
    private readonly DispatcherTimer gamepad = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly uint[] previousButtons = new uint[4];
    private HashSet<int> keys = [];
    private uint gamepadMask;
    private string mouse = "None";
    private Func<bool> allowed = () => false;
    private long lastTrigger;
    private bool clipboard;
    public bool CanShowSettings { get; private set; }
    public event Action? ShowSettings, ToggleTranslation, Trigger, ClipboardChanged, Explain, StudyScrollUp, StudyScrollDown;
    public DesktopInput(Window window)
    {
        hwnd = new WindowInteropHelper(window).Handle; source = HwndSource.FromHwnd(hwnd); source.AddHook(Message);
        keyboardCallback = Keyboard; mouseCallback = Mouse;
        gamepad.Tick += PollGamepads;
    }
    public static (uint Modifiers, uint Key) ParseHotkey(string value)
    {
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint modifiers = 0;
        for (int i = 0; i < parts.Length - 1; i++) modifiers |= parts[i].ToUpperInvariant() switch
        { "SHIFT" => 4u, "CTRL" or "CONTROL" => 2u, "ALT" => 1u, _ => throw new TranslationException("단축키 수식어는 Ctrl, Shift, Alt만 지원합니다.") };
        int key = ParseKey(parts.LastOrDefault() ?? "");
        return (modifiers, (uint)key);
    }
    internal static (uint Modifiers, uint Key) ParseGameQaHotkey(string value)
    {
        var key = ParseHotkey(value);
        if ((key.Modifiers & 2) != 0) throw new TranslationException("게임 스킵 방지를 위해 문제 장면 저장키에 Ctrl을 사용할 수 없습니다. 예: F8");
        return key;
    }
    private static int ParseKey(string key)
    {
        if (Enum.TryParse<Key>(key, true, out var value) && value is not (Key.None or Key.LWin or Key.RWin or Key.System)
            && KeyInterop.VirtualKeyFromKey(value) is > 0 and < 256) return KeyInterop.VirtualKeyFromKey(value);
        throw new TranslationException("키 이름을 확인하세요. 예: Enter, Space, F12, A");
    }
    internal static (HashSet<int> Keys, uint Mask) ParseTriggers(TriggerOptions settings)
    {
        settings.Validate();
        var parsed = settings.Keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseKey).ToHashSet();
        uint mask = GamepadInput.Parse(settings.GamepadButtons);
        return (parsed, mask);
    }
    public string Register(HotkeyOptions settings)
    {
        var names = RegisteredHotkeys(settings);
        var values = names.Select(ParseHotkey).ToArray();
        for (int id = 1; id <= values.Length; id++) UnregisterHotKey(hwnd, id);
        var failures = new List<string>();
        var registeredKeys = new HashSet<(uint Modifiers, uint Key)>();
        for (int i = 0; i < values.Length; i++)
        {
            bool unique = registeredKeys.Add(values[i]);
            bool registered = unique && RegisterHotKey(hwnd, i + 1, values[i].Modifiers | 0x4000, values[i].Key);
            int error = !registered && unique ? Marshal.GetLastWin32Error() : 0;
            if (i == 0) CanShowSettings = registered;
            if (!registered) failures.Add(HotkeyFailure(names[i], values[i].Key, !unique, error));
        }
        return failures.Count == 0 ? "" : "단축키 등록 실패: " + string.Join(", ", failures) + " · 입력 설정에서 변경하세요.";
    }
    // Keep the QA recorder/writer and legacy preference, but do not bind its
    // former F8 (or a saved alternate chord) in the normal application.
    internal static string[] RegisteredHotkeys(HotkeyOptions settings) =>
        [settings.ShowSettings, settings.ToggleTranslation, settings.Explain, settings.StudyScrollUp, settings.StudyScrollDown];
    internal static string HotkeyFailure(string name, uint key, bool duplicate, int error) => duplicate
        ? name + " (앱 내부 단축키 중복)"
        : name + $" (Win32 {error}" + (key == 0x7B ? " · F12는 Windows 디버거 예약키입니다. F1 등 다른 키를 선택하세요." : " · Windows 또는 다른 프로그램에서 사용 중일 수 있습니다.") + ")";
    public void Start(TriggerOptions settings, Func<bool> canTrigger, RecognitionInputPlan plan)
    {
        Stop(); allowed = canTrigger;
        if (plan.WatchClipboard)
        {
            if (!AddClipboardFormatListener(hwnd)) throw new TranslationException("클립보드 변경 감시를 시작할 수 없습니다.");
            clipboard = true; return;
        }
        if (!plan.UseTriggers || !settings.Enabled) return;
        (keys, gamepadMask) = ParseTriggers(settings); mouse = settings.MouseButton;
        try
        {
            if (keys.Count > 0) { keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0); if (keyboardHook == IntPtr.Zero) throw new TranslationException("키보드 트리거를 시작할 수 없습니다."); }
            if (mouse != "None") { mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0); if (mouseHook == IntPtr.Zero) throw new TranslationException("마우스 트리거를 시작할 수 없습니다."); }
            if (gamepadMask != 0)
            {
                for (uint i = 0; i < 4; i++) previousButtons[i] = GamepadInput.Read(i) ?? 0;
                gamepad.Start();
            }
        }
        catch { Stop(); throw; }
    }
    private IntPtr Message(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x312)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is HotkeyCaptureBox or TriggerKeyCaptureBox)
            {
                // Registered chords bypass WPF KeyDown. Route them into the
                // focused key editor, never into application commands.
                long data = lParam.ToInt64();
                uint modifiers = (uint)data & 0xFFFF;
                uint virtualKey = ((uint)(data >> 16)) & 0xFFFF;
                if (System.Windows.Input.Keyboard.FocusedElement is HotkeyCaptureBox editor)
                    editor.CaptureRegisteredHotkey(modifiers, virtualKey);
                else if (System.Windows.Input.Keyboard.FocusedElement is TriggerKeyCaptureBox trigger)
                    trigger.CaptureRegisteredHotkey(modifiers, virtualKey);
                handled = true;
                return IntPtr.Zero;
            }
            if ((int)wParam == 1) ShowSettings?.Invoke();
            if ((int)wParam == 2) ToggleTranslation?.Invoke();
            if ((int)wParam == 3) Explain?.Invoke();
            if ((int)wParam == 4) StudyScrollUp?.Invoke();
            if ((int)wParam == 5) StudyScrollDown?.Invoke();
            handled = true;
        }
        if (message == 0x31D && clipboard) ClipboardChanged?.Invoke();
        return IntPtr.Zero;
    }
    private void Fire()
    {
        if (System.Windows.Input.Keyboard.FocusedElement is HotkeyCaptureBox or TriggerKeyCaptureBox) return;
        long now = Environment.TickCount64;
        if (now - lastTrigger < 400 || !allowed()) return;
        lastTrigger = now; source.Dispatcher.BeginInvoke(() => Trigger?.Invoke());
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (System.Windows.Input.Keyboard.FocusedElement is HotkeyCaptureBox or TriggerKeyCaptureBox)
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        if (code >= 0 && (int)message is 0x101 or 0x105)
        {
            var input = Marshal.PtrToStructure<KeyboardData>(data);
            if ((input.Flags & 0x10) == 0 && keys.Contains((int)input.Key)) Fire();
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data); // Never swallow game input.
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<MouseHookData>(data);
            string button = (int)message switch { 0x202 => "Left", 0x205 => "Right", 0x208 => "Middle", 0x20C => (input.MouseData >> 16) == 1 ? "X1" : "X2", _ => "None" };
            if ((input.Flags & 1) == 0 && button != "None" && button == mouse) Fire();
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    private void PollGamepads(object? sender, EventArgs e)
    {
        for (uint i = 0; i < 4; i++)
        {
            uint buttons = GamepadInput.Read(i) ?? 0;
            if ((GamepadInput.Rising(previousButtons[i], buttons) & gamepadMask) != 0) Fire(); previousButtons[i] = buttons;
        }
    }
    public void Stop()
    {
        if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        keyboardHook = mouseHook = IntPtr.Zero; gamepad.Stop(); gamepadMask = 0; keys.Clear();
        if (clipboard) RemoveClipboardFormatListener(hwnd); clipboard = false; allowed = () => false;
    }
    public void Dispose() { Stop(); for (int id = 1; id <= 5; id++) UnregisterHotKey(hwnd, id); source.RemoveHook(Message); }
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
