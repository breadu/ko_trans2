using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Threading;

namespace KoTrans.App;

internal sealed class GamepadTriggerEditor : UserControl
{
    private readonly List<string> configured = ["Joy1", "Joy2"];
    private readonly WrapPanel chips = new() { Name = "GamepadButtonChips", Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button add = new() { Name = "AddGamepadButton", Content = "+ 버튼 추가", MinHeight = 36,
        Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5) };
    private readonly TriggerKeyCaptureBox capture = new() { Name = "GamepadButtonCapture", ToolTip = null };
    private readonly TextBlock note = new() { Name = "GamepadButtonStatus", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 5, 0, 4) };
    private readonly TextBlock legacyWarning = new() { Name = "LegacyGamepadWarning", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly uint[] previous = new uint[4];
    private readonly bool[] initialized = new bool[4];
    private bool dark;
    internal event EventHandler? ValueChanged;
    internal bool IsCapturing => capture.Visibility == Visibility.Visible;
    internal bool IsPolling => timer.IsEnabled;
    internal string Value {
        get => string.Join(",", configured);
        set {
            CancelCapture(); configured.Clear();
            configured.AddRange((value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            RenderChips(); // Preserve legacy IDs/order, without marking the profile dirty.
        }
    }
    internal GamepadTriggerEditor()
    {
        var panel = new StackPanel();
        panel.Children.Add(chips); panel.Children.Add(capture); panel.Children.Add(note); panel.Children.Add(legacyWarning); Content = panel;
        var compactStatus = new Style(typeof(TextBlock));
        var empty = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); compactStatus.Triggers.Add(empty);
        note.Style = legacyWarning.Style = compactStatus;
        add.Click += (_, _) => { if (IsCapturing) CancelCapture(); else BeginCapture(); };
        capture.Canceled += CancelCapture;
        capture.Captured += (_, _) => note.Text = "게임패드 버튼을 누르세요. Esc로 취소할 수 있습니다.";
        capture.LostKeyboardFocus += (_, e) => { if (IsCapturing && !ReferenceEquals(e.NewFocus, add)) CancelCapture(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelCapture(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelCapture(); };
        Unloaded += (_, _) => CancelCapture();
        timer.Tick += (_, _) => Poll();
        SetDarkTheme(false);
    }
    internal void SetDarkTheme(bool value)
    {
        dark = value; add.Style = KeyboardTriggerEditor.AddButtonStyle(dark);
        capture.Background = Paint(dark ? 0x122131 : 0xFFFFFF); capture.Foreground = Paint(dark ? 0xFFFFFF : 0x16283B);
        capture.BorderBrush = Paint(dark ? 0x48637A : 0xB8CBDB);
        note.Foreground = Paint(dark ? 0xA5BCD1 : 0x40586D);
        legacyWarning.Foreground = dark ? Brushes.LightSalmon : Brushes.Maroon;
        RenderChips();
    }
    internal void BeginCapture()
    {
        if (!IsEnabled) return;
        Array.Clear(initialized); Array.Clear(previous);
        capture.Text = "패드 입력 대기 · 버튼 하나를 누르세요 (Esc 취소)"; capture.Visibility = Visibility.Visible;
        add.Content = "입력 취소";
        note.Text = "XInput 게임패드: A/B/X/Y, LB/RB, LT/RT, 방향 버튼, 스틱 누르기, 시작/뒤로를 등록할 수 있습니다.";
        capture.Focus(); System.Windows.Input.Keyboard.Focus(capture);
        // Background controls never query a real device or start polling.
        if (CanPoll()) { Poll(); if (IsCapturing) timer.Start(); }
    }
    internal void CancelCapture()
    {
        timer.Stop(); capture.Visibility = Visibility.Collapsed; add.Content = "+ 버튼 추가"; note.Text = "";
    }
    private bool CanPoll() => IsCapturing && IsVisible && IsEnabled && Window.GetWindow(this)?.IsActive == true;
    private void Poll()
    {
        if (!CanPoll()) { CancelCapture(); return; }
        int connected = 0;
        for (uint i = 0; i < 4 && IsCapturing; i++) {
            var buttons = GamepadInput.Read(i); if (buttons.HasValue) connected++;
            ObserveState(i, buttons);
        }
        if (IsCapturing && connected == 0) note.Text = "연결된 XInput 게임패드가 없습니다. 연결 후 버튼을 눌러 주세요.";
    }
    internal bool ObserveState(uint slot, uint? buttons)
    {
        if (!IsCapturing || slot >= 4) return false;
        if (!buttons.HasValue) { initialized[slot] = false; previous[slot] = 0; return false; }
        if (!initialized[slot]) { initialized[slot] = true; previous[slot] = buttons.Value; return false; }
        uint pressed = GamepadInput.Rising(previous[slot], buttons.Value); previous[slot] = buttons.Value;
        return pressed != 0 && CaptureButton(pressed);
    }
    internal bool CaptureButton(uint pressed)
    {
        if (!IsCapturing || !IsEnabled) return false;
        var button = GamepadInput.Buttons.FirstOrDefault(b => b.Mask == pressed);
        if (button.Mask == 0) { note.Text = "지원하는 버튼을 한 번에 하나씩 눌러 주세요. 스틱 방향 이동은 등록하지 않습니다."; return false; }
        if (configured.Contains(button.Id, StringComparer.OrdinalIgnoreCase)) { note.Text = button.Label + " 버튼은 이미 등록되어 있습니다."; return false; }
        if (string.Join(",", configured.Append(button.Id)).Length > 200) { note.Text = "게임패드 설정이 너무 깁니다. 필요 없는 항목을 삭제하세요."; return false; }
        configured.Add(button.Id); RenderChips(); CancelCapture(); ValueChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    internal bool RemoveButton(string id)
    {
        int index = configured.FindIndex(b => b.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        configured.RemoveAt(index); RenderChips(); ValueChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    private void RenderChips()
    {
        chips.Children.Clear();
        foreach (string id in configured) {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            string label = GamepadInput.Label(id);
            row.Children.Add(new TextBlock { Text = label, Foreground = Paint(dark ? 0xFFFFFF : 0x16283B),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var remove = new Button { Name = "RemoveGamepadButton", Content = "×", Tag = id, MinWidth = 22, MinHeight = 22,
                Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(0), BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = Paint(dark ? 0xFFFFFF : 0x16283B), Cursor = Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(remove, label + " 제거");
            remove.Click += (_, _) => RemoveButton(id); row.Children.Add(remove);
            chips.Children.Add(new Border { Background = Paint(dark ? 0x275981 : 0xD8E8F5), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 7, 5), Margin = new Thickness(0, 0, 7, 7), Child = row });
        }
        chips.Children.Add(add);
        legacyWarning.Text = configured.Any(id => !GamepadInput.Buttons.Any(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            ? "지원하지 않는 저장 값이 있습니다. 해당 항목을 삭제하고 버튼을 다시 등록하세요." : "";
    }
    private static Brush Paint(int color) => new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
}
