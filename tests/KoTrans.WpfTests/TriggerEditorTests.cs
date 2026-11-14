using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class TriggerEditorTests
{
    internal static void Controls()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KoTrans2.TriggerEditors", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(directory);
        var vault = new ApiKeyStore(directory);
        var profile = new Profile { AutomaticWatch = false };
        var hotkeys = new HotkeyOptions();
        var dialog = Track(new TranslationSettingsWindow(profile, hotkeys, vault));
        try
        {
            var keyboard = Named<KeyboardTriggerEditor>(dialog, "TriggerKeys");
            var gamepad = Named<GamepadTriggerEditor>(dialog, "TriggerPad");
            var add = Elements<Button>(keyboard).Single(b => b.Name == "AddTriggerKey");
            var capture = Elements<TriggerKeyCaptureBox>(keyboard).Single();
            var keyboardStatus = Named<TextBlock>(dialog, "TriggerKeyStatus");
            Check(keyboardStatus.Visibility == Visibility.Collapsed && keyboardStatus.Text == "",
                "Empty keyboard trigger guidance should not occupy vertical space");
            var keyChips = Named<WrapPanel>(dialog, "TriggerKeyChips");
            var padChips = Named<WrapPanel>(dialog, "GamepadButtonChips");
            var padAdd = Elements<Button>(gamepad).Single(b => b.Name == "AddGamepadButton");
            Check(ReferenceEquals(keyChips.Children[keyChips.Children.Count - 1], add)
                && ReferenceEquals(padChips.Children[padChips.Children.Count - 1], padAdd)
                && keyChips.Orientation == Orientation.Horizontal && padChips.Orientation == Orientation.Horizontal,
                "Keyboard and gamepad Add buttons must follow the last chip in their wrapping rows");
            keyboard.Value = "Enter,Space,F5,F6,F7,Tab";
            keyChips.Measure(new Size(180, double.PositiveInfinity));
            keyChips.Arrange(new Rect(0, 0, 180, keyChips.DesiredSize.Height));
            Check(add.TranslatePoint(new Point(), keyChips).Y > 0,
                "Keyboard Add button failed to wrap when many trigger chips occupy the row");
            keyboard.Value = "Enter,Space";
            Check(keyboard.Value == "Enter,Space" && keyboard.Keys.SequenceEqual(new[] { "Enter", "Space" }),
                "Existing keyboard triggers were not restored as individual chips");
            Check(gamepad.Value == "Joy1,Joy2"
                && Elements<Button>(gamepad).Count(b => b.Name == "RemoveGamepadButton") == 2
                && Elements<TextBlock>(gamepad).Any(t => t.Text == "A") && Elements<TextBlock>(gamepad).Any(t => t.Text == "B")
                && !Elements<CheckBox>(gamepad).Any(), "Stored A/B buttons were not restored as removable chips");
            var mouse = Named<ComboBox>(dialog, "MouseTrigger");
            var savedMouse = profile.Triggers.MouseButton;
            var converter = new MouseTriggerLabelConverter();
            (string Code, string Label)[] mouseLabels = [
                ("None", "없음"), ("Left", "왼쪽 클릭"), ("Right", "오른쪽 클릭"),
                ("Middle", "가운데 클릭 (휠 클릭)"), ("X1", "측면 버튼 1"), ("X2", "측면 버튼 2")
            ];
            Check(mouse.ItemTemplate is not null && mouse.SelectedItem as string == profile.Triggers.MouseButton
                && mouseLabels.All(pair => mouse.Items.Contains(pair.Code)
                    && Equals(converter.Convert(pair.Code, typeof(string), null!,
                        System.Globalization.CultureInfo.InvariantCulture), pair.Label)),
                "Mouse dropdown needs Korean display labels while retaining English codes");
            var labelPresenter = new ContentPresenter { Content = "Middle", ContentTemplate = mouse.ItemTemplate };
            var labelHost = Track(new Window { Content = labelPresenter });
            try {
                Layout(labelHost, 300, 55);
                Check(OverlayActivityTests.Visuals<TextBlock>(labelPresenter).Single().Text == "가운데 클릭 (휠 클릭)",
                    "Rendered mouse item lost its Korean display label");
            } finally { Close(labelHost); }
            mouse.SelectedItem = "Right";
            Check(profile.Triggers.MouseButton == savedMouse && mouse.SelectedItem as string == "Right",
                "Selecting a localized mouse option must not immediately mutate the saved profile");
            int changes = 0;
            keyboard.ValueChanged += (_, _) => changes++;

            Click(add);
            Check(keyboard.IsCapturing && capture.Visibility == Visibility.Visible && add.IsEnabled
                && keyboardStatus.Visibility == Visibility.Visible && keyboardStatus.Text.Contains("게임 키")
                && Equals(add.Content, "입력 취소"),
                "Adding a key must keep an enabled, readable cancel button and guidance");
            Check(add.Style?.Setters.OfType<Setter>().Any(s => s.Property == System.Windows.Controls.Button.TemplateProperty) == true
                && add.Style?.Triggers.OfType<Trigger>().Any(x => x.Property == UIElement.IsEnabledProperty) == true,
                "The add button must use explicit WPF normal/disabled theme templates");
            Click(add);
            Check(!keyboard.IsCapturing && keyboardStatus.Visibility == Visibility.Collapsed
                && Equals(add.Content, "+ 키 추가"),
                "Canceling key capture should restore compact spacing without a stale status line");
            Click(add);
            capture.CaptureRegisteredHotkey(0, 0x74); // F5, including registered-chord capture path.
            Check(!keyboard.IsCapturing && keyboard.Value == "Enter,Space,F5" && changes == 1,
                "Single F5 key did not become a third trigger");

            Click(add);
            Check(!keyboard.CaptureKey(Key.F5, ModifierKeys.None) && keyboard.IsCapturing && changes == 1,
                "Duplicate keyboard key unexpectedly added or ended input mode");
            Check(!keyboard.CaptureKey(Key.F6, ModifierKeys.Control)
                && !keyboard.CaptureKey(Key.LeftShift, ModifierKeys.None)
                && !keyboard.CaptureKey(Key.F7, ModifierKeys.Alt)
                && keyboard.IsCapturing,
                "Unsupported modifier or multi-key combination entered the legacy single-key format");
            Check(keyboard.CaptureKey(Key.Tab, ModifierKeys.None) && keyboard.Value == "Enter,Space,F5,Tab",
                "Tab was consumed for focus navigation instead of being recordable as a game key");
            Check(DesktopInput.ParseTriggers(new TriggerOptions { Keys = keyboard.Value, GamepadButtons = "" }).Keys.Count == 4,
                "Captured keys are not accepted by the production game input parser");
            var remove = Elements<Button>(keyboard).Single(b => b.Name == "RemoveTriggerKey" && Equals(b.Tag, "Space"));
            Click(remove);
            Check(keyboard.Value == "Enter,F5,Tab" && changes == 3,
                "Removing one key chip changed another trigger or did not mark the editor dirty");
            Click(add);
            Check(!keyboard.CaptureKey(Key.Escape, ModifierKeys.None) && !keyboard.IsCapturing
                && keyboard.Value == "Enter,F5,Tab" && changes == 3,
                "Esc cancel unexpectedly changed a saved key");

            Click(Elements<Button>(gamepad).Single(c => c.Name == "AddGamepadButton"));
            Check(gamepad.IsCapturing && !gamepad.IsPolling && gamepad.CaptureButton(0x4000), "Gamepad capture did not add X without polling a physical device in Background");
            Check(gamepad.Value == "Joy1,Joy2,Joy3" &&
                DesktopInput.ParseTriggers(new TriggerOptions { Keys = "", GamepadButtons = gamepad.Value }).Mask == 0x7000,
                "Captured X did not map A/B/X to the original XInput mask");
            Click(Elements<Button>(gamepad).Single(b => b.Name == "RemoveGamepadButton" && Equals(b.Tag, "Joy2")));
            Check(gamepad.Value == "Joy1,Joy3",
                "Removing B did not remove the corresponding Joy2 token");
            gamepad.Value = "Joy2,Joy1";
            Check(gamepad.Value == "Joy2,Joy1",
                "Loading a valid legacy order unexpectedly rewrote the stored configuration");
            gamepad.Value = "Joy9";
            Check(!dialog.ApplyChanges(profile, hotkeys) && profile.Triggers.GamepadButtons == "Joy1,Joy2",
                "Unknown legacy gamepad token was silently discarded");
            gamepad.Value = "Joy1,Joy3";
            keyboard.Value = "BadKeyboardKey";
            Check(!dialog.ApplyChanges(profile, hotkeys) && profile.Triggers.Keys == "Enter,Space",
                "Unknown legacy keyboard trigger was silently accepted");
            keyboard.Value = "Enter,F5,Tab";
            Check(dialog.ApplyChanges(profile, hotkeys)
                && profile.Triggers.Keys == "Enter,F5,Tab"
                && profile.Triggers.GamepadButtons == "Joy1,Joy3"
                && profile.Triggers.MouseButton == "Right",
                "New editors failed to persist legacy-compatible English key, mouse and gamepad values");
        }
        finally { Close(dialog); }

        // Real embedded profile page exercises the dirty event, save path and
        // profile reload without showing windows or touching the user's settings.
        var settings = new AppSettings(); settings.Validate(); settings.DefaultProfile.AutomaticWatch = false;
        var window = Track(new MainWindow(store, vault, () => [], _ => new PixelRect(0, 0, 1024, 768)));
        try
        {
            window.ApplyLoadedSettings(settings);
            window.ShowProfileSettingsPage(4);
            var keyboard = Named<KeyboardTriggerEditor>(window, "TriggerKeys");
            var gamepad = Named<GamepadTriggerEditor>(window, "TriggerPad");
            var embeddedMouse = Named<ComboBox>(window, "MouseTrigger");
            var mouseLabel = Elements<TextBlock>(window).Single(t => t.Text == "마우스 트리거");
            var gamepadLabel = Elements<TextBlock>(window).Single(t => t.Text == "게임패드 트리거 · 버튼을 직접 눌러 추가");
            foreach (var size in new[] { new Size(1060, 900), new Size(640, 900) })
            {
                Layout(window, size.Width, size.Height);
                var addKey = Elements<Button>(keyboard).Single(b => b.Name == "AddTriggerKey");
                double keyToMouse = FeedbackTests.Bounds(window, mouseLabel).Top - FeedbackTests.Bounds(window, addKey).Bottom;
                double mouseToPad = FeedbackTests.Bounds(window, gamepadLabel).Top - FeedbackTests.Bounds(window, embeddedMouse).Bottom;
                Check(keyToMouse >= 0 && Math.Abs(keyToMouse - mouseToPad) <= 6,
                    $"Keyboard/mouse spacing should match mouse/gamepad spacing at {size.Width}: {keyToMouse:F1} vs {mouseToPad:F1} DIP");
            }
            Check(embeddedMouse.SelectedItem as string == settings.DefaultProfile.Triggers.MouseButton && embeddedMouse.ItemTemplate is not null,
                "Embedded dark editor lost Korean mouse label template or English token");
            var add = Elements<Button>(keyboard).Single(b => b.Name == "AddTriggerKey");
            Click(add);
            Elements<TriggerKeyCaptureBox>(keyboard).Single().CaptureRegisteredHotkey(0, 0x74);
            Click(Elements<Button>(gamepad).Single(c => c.Name == "AddGamepadButton")); gamepad.CaptureButton(0x8000);
            Click(Button(window, "설정 저장"));
            var loaded = store.LoadAsync(); Complete(loaded);
            Check(loaded.Result.DefaultProfile.Triggers.Keys == "Enter,Space,F5"
                && loaded.Result.DefaultProfile.Triggers.GamepadButtons == "Joy1,Joy2,Joy4",
                "Embedded profile save lost keyboard or directly captured gamepad chips");
            window.ShowProfileSettingsPage(4);
            Check(keyboard.Value == "Enter,Space,F5" && gamepad.Value == "Joy1,Joy2,Joy4",
                "Saving/re-entering the trigger page lost editor state");

            window.ShowProfileSettingsPage(7);
            var password = Elements<PasswordBox>(window).Single();
            var model = Named<TextBox>(window, "Model");
            Check(password.MinHeight == model.MinHeight
                && password.Padding.Equals(model.Padding)
                && password.Margin.Equals(model.Margin),
                "Credential input is taller or has more padding than standard text fields");
        }
        finally { Close(window); Directory.Delete(directory, true); }
    }
}
