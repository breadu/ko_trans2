using System.Windows.Interop;
using Forms = System.Windows.Forms;
namespace KoTrans.App;

public partial class MainWindow
{
    private readonly Func<string,string?> pickOverlayColor;
    private void FillOverlayColor(ComboBox combo, Button picker, string value)
    {
        combo.ItemsSource = colors.Contains(value,StringComparer.OrdinalIgnoreCase) ? colors : [..colors,value];
        combo.SelectedItem = ((System.Collections.Generic.IEnumerable<string>)combo.ItemsSource).First(c=>c.Equals(value,StringComparison.OrdinalIgnoreCase));
        UpdateColorSwatch(picker,value);
    }
    private static void UpdateColorSwatch(Button picker, string value)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
        picker.Background = brush;
        var c=brush.Color; picker.Foreground = .2126*c.R + .7152*c.G + .0722*c.B > 140 ? Brushes.Black : Brushes.White;
    }
    private async void PickTranslationColor(object sender, RoutedEventArgs e) => await PickOverlayColorAsync(false);
    private async void PickOriginalColor(object sender, RoutedEventArgs e) => await PickOverlayColorAsync(true);
    private async Task PickOverlayColorAsync(bool original)
    {
        if (!ready || closing) return;
        var profile=Current;
        try {
            string? value=pickOverlayColor(original ? profile.OverlayOriginalColor : profile.OverlayColor);
            if (value is null || closing || Current.Id!=profile.Id) return;
            if (!System.Text.RegularExpressions.Regex.IsMatch(value,"^#[0-9a-fA-F]{6}$")) throw new InvalidDataException("글자 색상 값이 올바르지 않습니다.");
            bool wasChanging=changing; changing=true;
            try { FillOverlayColor(original ? OriginalFontColor : FontColor,original ? OriginalColorPicker : TranslationColorPicker,value.ToUpperInvariant()); }
            finally { changing=wasChanging; }
            ApplyOptions(); await SaveOverlayPreferencesAsync();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or InvalidDataException)
        { model.Status="색상 선택 실패: "+ex.Message; }
    }
    private string? PickWindowsColor(string current)
    {
        using var dialog=new Forms.ColorDialog { FullOpen=true, AnyColor=true, Color=System.Drawing.ColorTranslator.FromHtml(current) };
        var owner=new WindowInteropHelper(this).Handle;
        if (owner==IntPtr.Zero) throw new InvalidOperationException("설정창이 표시된 상태에서 색상을 선택하세요.");
        return dialog.ShowDialog(new ColorDialogOwner(owner))==Forms.DialogResult.OK
            ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}" : null;
    }
    private sealed class ColorDialogOwner(IntPtr handle) : Forms.IWin32Window { public IntPtr Handle=>handle; }
}
