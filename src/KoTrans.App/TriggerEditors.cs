using System.Collections.Generic;
using System.Windows.Data;
using System.Windows.Input;

namespace KoTrans.App;

// The capture surface must be a keyboard-focusable text control, not a Button:
// Space and Enter must be captured as game keys instead of clicking an action.
internal sealed class TriggerKeyCaptureBox : TextBox
{
    internal Action<Key, ModifierKeys>? Captured { get; set; }
    internal Action? Canceled { get; set; }

    internal TriggerKeyCaptureBox()
    {
        Name = "TriggerKeyCapture";
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        MinHeight = 38;
        Visibility = Visibility.Collapsed;
        ToolTip = "키 하나를 누르세요. Esc로 취소합니다.";
        PreviewTextInput += (_, e) => e.Handled = true;
        DataObject.AddPastingHandler(this, (_, e) => e.CancelCommand());
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true; // Includes Tab, Enter and Space while capturing.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) Canceled?.Invoke();
        else Captured?.Invoke(key, System.Windows.Input.Keyboard.Modifiers);
    }

    // WM_HOTKEY is delivered to the owner HWND rather than through KeyDown.
    internal void CaptureRegisteredHotkey(uint modifiers, uint virtualKey)
    {
        if (Visibility != Visibility.Visible) return;
        var mods = ModifierKeys.None;
        if ((modifiers & 1) != 0) mods |= ModifierKeys.Alt;
        if ((modifiers & 2) != 0) mods |= ModifierKeys.Control;
        if ((modifiers & 4) != 0) mods |= ModifierKeys.Shift;
        if ((modifiers & 8) != 0) mods |= ModifierKeys.Windows;
        Captured?.Invoke(KeyInterop.KeyFromVirtualKey((int)virtualKey), mods);
    }
}

// Multi-value editor with removable chips. The user never needs to type a
// WPF Key name or comma; the existing TriggerOptions.Keys contract remains.
internal sealed class KeyboardTriggerEditor : UserControl
{
    private readonly List<string> configured = [];
    private readonly WrapPanel chips = new() { Name = "TriggerKeyChips", Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button add = new()
    {
        Name = "AddTriggerKey", Content = "+ 키 추가", MinHeight = 36,
        Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 0, 0, 5)
    };
    private readonly TriggerKeyCaptureBox capture = new();
    private readonly TextBlock note = new() { Name = "TriggerKeyStatus", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 5, 0, 4) };
    private bool dark;

    internal event EventHandler? ValueChanged;
    internal IReadOnlyList<string> Keys => configured.AsReadOnly();
    internal bool IsCapturing => capture.Visibility == Visibility.Visible;
    internal string Value
    {
        get => string.Join(",", configured);
        set
        {
            CancelCapture();
            configured.Clear();
            configured.AddRange((value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            RenderChips(); // Loading an existing profile does not mark it dirty.
        }
    }

    internal KeyboardTriggerEditor()
    {
        var content = new StackPanel();
        content.Children.Add(chips);
        content.Children.Add(capture);
        content.Children.Add(note);
        Content = content;
        // Match the gamepad editor: empty status text must not reserve a blank row.
        // Show it only while capturing a key or reporting a capture error.
        var compactStatus = new Style(typeof(TextBlock));
        var empty = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        compactStatus.Triggers.Add(empty);
        note.Style = compactStatus;
        add.Click += (_, _) => { if (IsCapturing) CancelCapture(); else BeginCapture(); };
        capture.Captured += (key, mods) => CaptureKey(key, mods);
        capture.Canceled += CancelCapture;
        // Clicking the same button to cancel first moves keyboard focus out of
        // the capture TextBox. Keep capture active until the button Click runs.
        capture.LostKeyboardFocus += (_, e) =>
        {
            if (IsCapturing && !ReferenceEquals(e.NewFocus, add)) CancelCapture();
        };
        SetDarkTheme(false);
    }

    internal void SetDarkTheme(bool enabled)
    {
        dark = enabled;
        // WPF's default disabled Button template uses a light system palette
        // on this dark page. Use a local template in all interaction states.
        add.Style = AddButtonStyle(dark);
        capture.Background = dark ? Rgb(0x12, 0x22, 0x35) : Brushes.White;
        capture.Foreground = dark ? Brushes.White : Rgb(0x16, 0x28, 0x3B);
        capture.BorderBrush = dark ? Rgb(0x59, 0xAA, 0xF0) : Rgb(0x32, 0x8B, 0xD0);
        capture.BorderThickness = new Thickness(1);
        capture.Padding = new Thickness(11, 8, 11, 8);
        note.Foreground = dark ? Rgb(0xA5, 0xBC, 0xD1) : Rgb(0x52, 0x6C, 0x83);
        RenderChips();
    }

    internal void BeginCapture()
    {
        capture.Text = "키 입력 대기 · 키 하나를 누르세요 (Esc 취소)";
        capture.Visibility = Visibility.Visible;
        add.Content = "입력 취소";
        add.ToolTip = "키 등록을 취소합니다.";
        note.Text = "원하는 게임 키를 한 번 누르세요. 조합 키는 지원하지 않습니다.";
        capture.Focus();
        System.Windows.Input.Keyboard.Focus(capture);
    }

    internal void CancelCapture()
    {
        if (!IsCapturing) return;
        capture.Visibility = Visibility.Collapsed;
        add.Content = "+ 키 추가";
        add.ToolTip = "등록할 키를 직접 누르세요.";
        note.Text = "";
    }

    internal bool CaptureKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape) { CancelCapture(); return false; }
        if (!IsCapturing) return false;
        if (!TryNormalizeKey(key, modifiers, out var value))
        {
            note.Text = "단일 키만 등록할 수 있습니다. Ctrl/Alt/Shift/Win 조합 없이 누르세요.";
            return false;
        }
        if (ContainsKey(value))
        {
            note.Text = value + " 키는 이미 등록되어 있습니다.";
            return false;
        }
        if (string.Join(",", configured.Append(value)).Length > 200)
        {
            note.Text = "키보드 트리거는 최대 200자까지 설정할 수 있습니다.";
            return false;
        }
        configured.Add(value);
        RenderChips();
        ValueChanged?.Invoke(this, EventArgs.Empty);
        CancelCapture();
        return true;
    }

    internal static bool TryNormalizeKey(Key key, ModifierKeys modifiers, out string name)
    {
        name = "";
        if (modifiers != ModifierKeys.None || key is Key.None or Key.System or Key.Escape
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return false;
        string candidate = key is Key.Return or Key.Enter ? "Enter" : key.ToString();
        try
        {
            if (DesktopInput.ParseTriggers(new TriggerOptions { Keys = candidate, GamepadButtons = "" }).Keys.Count != 1)
                return false;
        }
        catch (Exception ex) when (ex is TranslationException or InvalidDataException) { return false; }
        name = candidate;
        return true;
    }

    internal bool RemoveKey(string value)
    {
        int i = configured.FindIndex(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        configured.RemoveAt(i);
        RenderChips();
        ValueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool ContainsKey(string value)
    {
        int? other = VirtualKey(value);
        return configured.Any(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase)
            || (other.HasValue && other == VirtualKey(k)));
    }

    private static int? VirtualKey(string value)
    {
        try
        {
            var keys = DesktopInput.ParseTriggers(new TriggerOptions { Keys = value, GamepadButtons = "" }).Keys;
            return keys.Count == 1 ? keys.Single() : null;
        }
        catch (Exception ex) when (ex is TranslationException or InvalidDataException) { return null; }
    }

    private void RenderChips()
    {
        chips.Children.Clear();
        foreach (var value in configured)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = value, Foreground = dark ? Brushes.White : Rgb(0x16, 0x28, 0x3B),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            });
            var remove = new Button
            {
                Name = "RemoveTriggerKey", Content = "×", Tag = value, ToolTip = value + " 제거",
                MinWidth = 22, MinHeight = 22, Padding = new Thickness(2, 0, 2, 0),
                Background = Brushes.Transparent, Foreground = dark ? Brushes.White : Rgb(0x16, 0x28, 0x3B),
                BorderThickness = new Thickness(0), Margin = new Thickness(0), Cursor = Cursors.Hand
            };
            remove.Click += (_, _) => RemoveKey(value);
            row.Children.Add(remove);
            chips.Children.Add(new Border
            {
                Background = dark ? Rgb(0x27, 0x59, 0x81) : Rgb(0xD8, 0xE8, 0xF5),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 7, 5),
                Margin = new Thickness(0, 0, 7, 7), Child = row
            });
        }
        // Match gamepad buttons: place Add after the final chip and wrap at narrow widths.
        chips.Children.Add(add);
    }

    // Explicit template keeps both the text and background legible regardless
    // of platform button theme, ancestor implicit Button styles or focus state.
    internal static Style AddButtonStyle(bool dark)
    {
        var background = dark ? Rgb(0x2A, 0x40, 0x56) : Rgb(0xE5, 0xED, 0xF4);
        var foreground = dark ? Brushes.White : Rgb(0x16, 0x28, 0x3B);
        var border = dark ? Rgb(0x48, 0x63, 0x7A) : Rgb(0xB8, 0xCB, 0xDB);

        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        chrome.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        chrome.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        chrome.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        chrome.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding))
        {
            RelativeSource = RelativeSource.TemplatedParent
        });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(content);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            new ControlTemplate(typeof(Button)) { VisualTree = chrome }));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,
            dark ? Rgb(0x35, 0x54, 0x6D) : Rgb(0xD5, 0xE5, 0xF2)));
        style.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty,
            dark ? Rgb(0x27, 0x59, 0x81) : Rgb(0xC7, 0xDB, 0xEB)));
        style.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.BackgroundProperty,
            dark ? Rgb(0x20, 0x35, 0x49) : Rgb(0xE0, 0xE8, 0xEE)));
        disabled.Setters.Add(new Setter(Control.ForegroundProperty,
            dark ? Rgb(0xB6, 0xCC, 0xDF) : Rgb(0x50, 0x69, 0x7F)));
        style.Triggers.Add(disabled);
        return style;
    }

    private static Brush Rgb(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
