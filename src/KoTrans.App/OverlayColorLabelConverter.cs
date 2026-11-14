using System.Globalization;
using System.Windows.Data;

namespace KoTrans.App;

// Presentation only: profile values and ComboBox selections stay hexadecimal.
public sealed class OverlayColorLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string)?.ToUpperInvariant() switch {
            "#FFFFFF" => "흰색", "#FFE68A" => "노란색", "#A5DEFF" => "하늘색",
            "#D3D3D3" => "연한 회색", "#FF819A" => "코랄 핑크", "#D3A7FF" => "연보라색",
            "#000000" => "검은색", "#FF0000" => "빨간색",
            "#00FF00" => "초록색", "#0000FF" => "파란색", _ => "사용자 지정 색상"
        };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
