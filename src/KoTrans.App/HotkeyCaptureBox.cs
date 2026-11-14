using System.Windows.Input;

namespace KoTrans.App;

// Shows an existing shortcut and captures actual key chords, never freeform text.
internal sealed class HotkeyCaptureBox : TextBox
{
    internal bool AllowControl { get; set; } = true;

    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        MinHeight = 38;
        Cursor = Cursors.Hand;
        ToolTip = "클릭 후 새 단축키를 누르세요. Esc는 취소, Tab은 다음 항목.";
        PreviewTextInput += (_, e) => e.Handled = true;
        DataObject.AddPastingHandler(this, (_, e) => e.CancelCommand());
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Keep keyboard navigation and preserve the current shortcut on Escape.
        if (e.Key is Key.Tab or Key.Escape) { base.OnPreviewKeyDown(e); return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (TryFormatHotkey(key, Keyboard.Modifiers, AllowControl, out var shortcut))
            Text = shortcut;
    }

    // RegisterHotKey delivers already-registered shortcuts as WM_HOTKEY instead of
    // PreviewKeyDown. Forward that chord here while the editor owns keyboard focus.
    internal void CaptureRegisteredHotkey(uint winModifiers, uint virtualKey)
    {
        var mods = ModifierKeys.None;
        if ((winModifiers & 1) != 0) mods |= ModifierKeys.Alt;
        if ((winModifiers & 2) != 0) mods |= ModifierKeys.Control;
        if ((winModifiers & 4) != 0) mods |= ModifierKeys.Shift;
        if ((winModifiers & 8) != 0) mods |= ModifierKeys.Windows;
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        if (TryFormatHotkey(key, mods, AllowControl, out var shortcut))
            Text = shortcut;
    }

    internal static bool TryFormatHotkey(Key key, ModifierKeys modifiers, bool allowControl, out string shortcut)
    {
        shortcut = "";
        if (key is Key.None or Key.System or Key.Tab or Key.Escape or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            || (modifiers & ModifierKeys.Windows) != 0
            || (!allowControl && (modifiers & ModifierKeys.Control) != 0))
            return false;
        var parts = new System.Collections.Generic.List<string>(4);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        parts.Add(key.ToString());
        var value = string.Join("+", parts);
        try
        {
            if (allowControl) DesktopInput.ParseHotkey(value);
            else DesktopInput.ParseGameQaHotkey(value);
        }
        catch (TranslationException) { return false; }
        shortcut = value;
        return true;
    }
}
